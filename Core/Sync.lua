-- RoS-Tools/Core/Sync.lua
-- Roster snapshot propagation between guildmates over WoW's addon-message
-- channel. Where Core/Comm.lua moves one player's own ilvl in real time,
-- this moves the whole Data/GuildData.lua snapshot, so a guildmate who
-- never updates the addon still ends up on the newest export somebody in
-- the guild has.
--
-- Peer-symmetric by design: no server, no origin, no coordinator, no
-- election. Every client runs this identical state machine and is both a
-- potential requester and a potential server. The only ranking that exists
-- is `generated_epoch`, and the client that just adopted a snapshot is
-- immediately a valid source for the next one.
--
-- Not a ns:RegisterModule() citizen, for the same reason Comm.lua isn't --
-- it's infrastructure Data reads from, wired directly into Core/Events.lua
-- through a pcall-wrapped callSync(). A bug in here degrades to "no
-- snapshot sync" and breaks nothing else.
--
-- Two rules that most of the fiddly code below exists to serve:
--
--   * No failure is permanent. Every cap, cooldown and blacklist here is
--     time-bounded and cleared on the anti-entropy beat. A client that
--     gives up for the rest of a six-hour session is a bug, not caution --
--     the guild has to converge without anyone noticing it didn't.
--   * Membership is checked by transport where possible and by the roster
--     API where not. A GUILD message can only come from a guild member, so
--     it needs no check. A WHISPER can come from anyone, so it gets one.
--
-- See DATA-SYNC-DESIGN.md at the repo root for the full spec.

local _, ns = ...

local Sync = {}
ns.Sync = Sync

-- Wire-format version, not the addon version. Deliberately a separate
-- prefix from Comm.lua's RoSTools1: a 2.2.0 client never registered this
-- one, so CHAT_MSG_ADDON doesn't even fire for it.
local PREFIX = "RoSToolsD1"

-- Transport
local CHUNK_SIZE     = 200   -- bytes of body per message (255 cap, ~22 for the header)
local CHUNK_INTERVAL = 0.25  -- seconds between chunks
-- 200 chunks is ~40 KB, roughly 1300 members at present key lengths. The
-- old ceiling of 64 was ~12.8 KB, which a 500-member guild silently
-- exceeded -- and the resulting refusal looked exactly like every other
-- refusal, so nobody would ever have found out why sync "just didn't work".
local MAX_CHUNKS     = 200

-- Announcing
local LOGIN_MIN, LOGIN_MAX = 5, 15
local ADOPT_MIN, ADOPT_MAX = 3, 8
local ANTI_ENTROPY         = 1200  -- 20 minutes
local ANTI_ENTROPY_JITTER  = 300   -- +/- 5 minutes
-- Wide on purpose. One stale peer announcing is heard by every holder
-- online, and each of them answers -- so the jitter, not the message count,
-- is what keeps a raid-invite burst from turning into a whisper flood.
local REPLY_MIN, REPLY_MAX = 1, 10
local REPLY_COOLDOWN       = 60    -- per-peer, so nobody can farm replies out of us
local REPLY_WINDOW         = 60    -- how long after our own announce a whispered reply is plausible

-- Requesting
local COLLECT_WINDOW  = 3
local REQUEST_JITTER  = 4     -- spread so peers hearing one announce don't all ask at once
local REQUEST_TIMEOUT  = 30   -- before any chunk arrives; extended once we know the size
local MAX_TRANSFER_TIME = 180 -- hard ceiling on one transfer, however big it claims to be
local RETRY_MIN       = 10    -- backoff after a peer says it is momentarily busy
local RETRY_MAX       = 30
local MAX_ATTEMPTS    = 5     -- per anti-entropy window, not per session
-- Refunded attempts don't count against MAX_ATTEMPTS, so without a second
-- cap a peer that answers "busy" forever would be asked forever. This bounds
-- the refund loop the way MAX_ATTEMPTS bounds the terminal one.
local MAX_RETRY_ROUNDS = 8

-- Serving
local SERVE_COOLDOWN     = 30
local REQUESTER_COOLDOWN = 300
local MAX_SERVES         = 5   -- per anti-entropy window, not per session

-- Validation
local MAX_FUTURE  = 300         -- reject an epoch more than 5 minutes ahead of us
local MAX_AGE     = 90 * 86400  -- reject an export older than 90 days
local MAX_KEY_LEN = 48
-- Item level bounds for one entry. The ceiling is deliberately far above
-- anything retail issues today (the guild's highest is in the 600s): it exists
-- to reject nonsense, not to track the game. A ceiling set just above current
-- content is a dated time bomb -- the season it is passed, every export becomes
-- 100% "bad" entries and roster sharing stops guild-wide, with no message
-- anywhere and nothing in the addon to point at. Kept in step with
-- GuildDataValidator.MaxIlvl in the sidecar.
local MIN_ILVL, MAX_ILVL = 1, 9999
-- An absolute ceiling, not a comparison against GetNumGuildMembers(). See
-- the note in validate() -- WoW's own guild cap is well under this, so it
-- bounds memory without depending on an API that fills in asynchronously.
local MAX_ENTRIES = 5000
local MAX_BAD_FRACTION = 0.1    -- reject a snapshot losing >10% of entries to validation

local ROSTER_CACHE_TTL  = 30
local ROSTER_STABLE_FOR = 15   -- how long the member count must hold still before a miss counts

-- ------------------------------------------------------------------
-- Session state. None of this is persisted -- the only thing that survives
-- a reload is the adopted snapshot itself, in RoSToolsDB.
--
-- Every peer-keyed table below is keyed by the *normalized* key
-- ("Name-realm-slug"), never by the raw `sender` string, because the raw
-- form is not guaranteed to be spelled the same way on the GUILD and
-- WHISPER paths. The raw name is carried alongside, since that -- not the
-- normalized key -- is what SendAddonMessage needs as a whisper target.
-- ------------------------------------------------------------------
local announcePending  = false
local announceSuppress = false
local lastAnnounceAt   = nil   -- nil, not 0: GetTime() is client uptime, so 0 is not "long ago"

local collecting   = false
local retryPending = false
local retryRounds  = 0
local heard        = {}   -- key -> { epoch, name, share }
local tried        = {}   -- key -> true, peers asked in this window
local attempts     = 0
local pending      = nil  -- { target, targetKey, nonce, chunks, total, got }

local repliedTo  = {}
local serving    = nil    -- { target, nonce, chunks, index, gen, startedAt }
local serveGen   = 0      -- bumped whenever a serve is torn down out from under its timer
local lastServeAt = 0
local serveCount  = 0
local servedTo    = {}

-- Session-scoped, and deliberately NOT cleared by the anti-entropy beat: the
-- condition it reports is a property of the installed file, not a transient
-- one, so re-warning every 20 minutes would say nothing new.
local warnedTooBig = false

local rosterCache, rosterCacheAt = nil, 0
local rosterAskedAt = 0
local rosterTotal, rosterStableSince = nil, nil

-- ------------------------------------------------------------------
-- Helpers
-- ------------------------------------------------------------------
local replyVersion

local function enabled() return ns.db and ns.db.syncEnabled and true or false end
local function sharing() return enabled() and ns.db.syncShare and true or false end
local function myEpoch() return ns.Data:GeneratedEpoch() or 0 end

local function send(body, target)
  if target then
    C_ChatInfo.SendAddonMessage(PREFIX, body, "WHISPER", target)
  else
    C_ChatInfo.SendAddonMessage(PREFIX, body, "GUILD")
  end
end

local function refuse(target, nonce, reason)
  send(("X:%s:%s"):format(nonce, reason), target)
end

--- Normalized keys of everyone on the guild roster, or nil when the roster
--- hasn't loaded yet. Cached: this is called on every whispered message and
--- the roster is a few hundred entries.
local function guildRoster()
  local now = GetTime()
  if rosterCache and (now - rosterCacheAt) < ROSTER_CACHE_TTL then return rosterCache end

  local total = GetNumGuildMembers()
  if total ~= rosterTotal then
    rosterTotal, rosterStableSince = total, now
    rosterCache = nil   -- the roster grew or shrank under us; rebuild it
  end

  if not total or total == 0 then
    -- Ask again, gently. C_GuildInfo.GuildRoster() is throttled server-side,
    -- so this is best-effort -- but without it, a session where the initial
    -- request at login didn't take would never identify a whispering peer,
    -- and would answer every legitimate request with "wait" forever.
    if C_GuildInfo and C_GuildInfo.GuildRoster and (now - rosterAskedAt) > 15 then
      rosterAskedAt = now
      C_GuildInfo.GuildRoster()
    end
    return nil
  end

  local set = {}
  for i = 1, total do
    -- Only ever consulted about a sender that just messaged us, i.e. an
    -- online character, so this is unaffected by whether the guild UI is
    -- currently filtering offline members out of the index.
    local full = GetGuildRosterInfo(i)
    if full then
      local key = ns.Util.NormalizeKey(full, ns.playerRealmSlug)
      if key then set[key] = true end
    end
  end

  rosterCache, rosterCacheAt = set, now
  return set
end

--- true / false / nil, where nil means "we can't tell yet".
---
--- A hit is always definitive. A miss is not: GetNumGuildMembers() fills in
--- asynchronously, so a roster read mid-load is a real subset of the guild
--- and a genuine member reads as absent. Treating that as "outsider" made
--- the serve path answer a legitimate guildmate with silence instead of a
--- retryable refusal, costing them a terminal attempt and up to a full
--- anti-entropy window of staleness. So a miss only counts once the member
--- count has held still for a while.
local function isGuildMember(key)
  local set = guildRoster()
  if not set then return nil end
  if set[key] then return true end

  local stable = rosterStableSince and (GetTime() - rosterStableSince) > ROSTER_STABLE_FOR
  if not stable then return nil end
  return false
end

--- Split a string into fixed-size pieces. Split points are blind byte
--- offsets -- a piece can end mid-key, which is fine because reassembly
--- always happens before parsing.
local function split(s, size)
  local out, i, n = {}, 1, #s
  while i <= n do
    out[#out + 1] = s:sub(i, i + size - 1)
    i = i + size
  end
  return out
end

-- ------------------------------------------------------------------
-- Announcing
-- ------------------------------------------------------------------

local function doAnnounce()
  announcePending = false

  if announceSuppress then
    announceSuppress = false
    ns.Debug("sync: announce suppressed, a peer already restated our epoch")
    return
  end

  if not enabled() or not IsInGuild() then return end

  local epoch = myEpoch()
  if epoch == 0 then return end

  -- The share flag is a trailing field on purpose: a parser that predates
  -- it ignores it, which is the forward-compatibility rule this protocol
  -- adopted. Peers use it to avoid asking a receive-only client for data
  -- it will only refuse.
  send(("V:%d:%d:%d"):format(epoch, ns.Data:ShareableCount(), sharing() and 1 or 0))
  lastAnnounceAt = GetTime()
  ns.Debug(("sync: announced epoch %d"):format(epoch))
end

--- Clearing announceSuppress on EVERY entry, including the early return, is
--- load-bearing. Previously the flag survived the early return, so a peer's
--- newer announce could poison an already-armed login announce -- and since
--- that same announce is what makes us adopt, the post-adopt announce
--- ("we're a holder now") was the one most likely to be swallowed. That
--- silently turned the mesh back into a star around whoever exported.
local function scheduleAnnounce(minDelay, maxDelay)
  announceSuppress = false
  if announcePending then return end
  announcePending = true
  C_Timer.After(minDelay + math.random() * (maxDelay - minDelay), doAnnounce)
end

--- The heartbeat. Also the amnesty: every cap and blacklist in this file is
--- cleared here, so no failure outlives one window. Without this a client
--- that lost three races at raid invite stayed stale for the whole night.
local function scheduleAntiEntropy()
  local delay = ANTI_ENTROPY + math.random(-ANTI_ENTROPY_JITTER, ANTI_ENTROPY_JITTER)
  C_Timer.After(delay, function()
    -- Re-arm FIRST, and swallow anything the amnesty itself throws. This
    -- heartbeat is the whole recovery story in this file; if a bug in the
    -- body could stop it re-arming, every other safety net here would be
    -- one exception away from being switched off for the session.
    scheduleAntiEntropy()

    pcall(function()
      attempts     = 0
      retryRounds  = 0
      serveCount   = 0
      wipe(tried)
      wipe(heard)     -- epochs go stale and peers log out; ghosts waste attempts
      wipe(repliedTo)

      -- Coordination flags, not just caps. `collecting` in particular can be
      -- left set by a beginRequest() that early-returned (sync switched off
      -- mid-window), and while it is set nothing can ever start a request
      -- again.
      collecting   = false
      retryPending = false
      if pending and (GetTime() - pending.startedAt) > MAX_TRANSFER_TIME then
        pending = nil
      end
      if serving and (GetTime() - serving.startedAt) > MAX_TRANSFER_TIME then
        serving = nil
      end

      scheduleAnnounce(0, 5)
    end)
  end)
end

--- Tell one stale peer what we have. Gated on sharing(), not enabled(): a
--- receive-only client advertising itself as a source just burns the stale
--- peer's attempts on a refusal.
function replyVersion(targetKey, targetName)
  if not sharing() then return end

  local now = GetTime()
  if repliedTo[targetKey] and (now - repliedTo[targetKey]) < REPLY_COOLDOWN then return end
  repliedTo[targetKey] = now

  C_Timer.After(REPLY_MIN + math.random() * (REPLY_MAX - REPLY_MIN), function()
    if not sharing() then return end
    local epoch = myEpoch()
    if epoch == 0 then return end
    send(("V:%d:%d:1"):format(epoch, ns.Data:ShareableCount()), targetName)
  end)
end

-- ------------------------------------------------------------------
-- Requesting
-- ------------------------------------------------------------------

local beginRequest

--- Reasons a peer can refuse. Retryable ones are transient facts about that
--- peer *right now* -- it is mid-transfer, or cooling down. Treating those
--- as permanent was the single worst bug in the first cut: at raid invite
--- everybody's collect window expires in the same instant, one peer wins
--- and the rest get "busy", so the normal case burned every attempt and
--- blacklisted every holder.
--- Backoff seconds per retryable reason. "busy" clears in seconds -- the
--- peer is mid-transfer. "cool" is a minutes-long cooldown, so hammering it
--- every 20s would just be 30 wasted whispers. "wait" means the peer's guild
--- roster hadn't loaded yet, which resolves almost immediately.
local RETRYABLE = {
  busy = { 10, 30 },
  wait = { 10, 30 },
  cool = { 60, 180 },
}

local function scheduleRetry(reason)
  if retryPending or pending then return end
  local window = RETRYABLE[reason] or { RETRY_MIN, RETRY_MAX }
  retryPending = true
  C_Timer.After(window[1] + math.random() * (window[2] - window[1]), function()
    retryPending = false
    beginRequest()
  end)
end

--- `retryable` is the refusal reason when the peer told us to come back,
--- and nil when the failure was terminal for that peer.
local function abandon(reason, retryable)
  if not pending then return end

  local key = pending.targetKey
  ns.Debug(("sync: request to %s abandoned (%s)"):format(pending.target, reason))
  pending = nil

  if retryable and retryRounds < MAX_RETRY_ROUNDS then
    -- Give the peer back and refund the attempt -- it told us to come back,
    -- not to go away.
    retryRounds = retryRounds + 1
    tried[key] = nil
    attempts = math.max(0, attempts - 1)
    scheduleRetry(retryable)
  else
    beginRequest()
  end
end

--- Watchdog for the in-flight request.
---
--- The deadline lives on `pending`, not in the closure. C_Timer.After can't
--- be cancelled, so an earlier timer will always still fire -- and if it
--- decided on its own that time was up, extending the deadline when the
--- transfer turned out to be large would achieve nothing. A timer that
--- fires early re-arms itself for the remainder instead. That is what makes
--- raising MAX_CHUNKS actually work: without it, any transfer longer than
--- the initial 30s budget was killed by its own first watchdog, no matter
--- what the extension said.
local function armWatchdog(nonce)
  if not pending then return end
  C_Timer.After(math.max(1, pending.deadline - GetTime() + 0.1), function()
    if not pending or pending.nonce ~= nonce then return end
    if GetTime() < pending.deadline then
      armWatchdog(nonce)   -- came back too soon; the budget grew under us
      return
    end
    abandon("timeout", nil)
  end)
end

--- Pick a peer among those tied at the highest epoch we've heard -- at
--- random, not first-heard. First-heard would quietly make whoever has the
--- lowest latency into a hub, which is the thing this design exists to
--- avoid.
function beginRequest()
  if pending or not enabled() then return end
  collecting = false

  if attempts >= MAX_ATTEMPTS then
    ns.Debug("sync: attempt cap reached, waiting for the next anti-entropy window")
    return
  end

  local mine = myEpoch()
  local best, candidates = mine, {}
  for key, peer in pairs(heard) do
    if not tried[key] and peer.share then
      if peer.epoch > best then
        best, candidates = peer.epoch, { key }
      elseif peer.epoch == best and best > mine then
        candidates[#candidates + 1] = key
      end
    end
  end

  if #candidates == 0 then return end

  local key    = candidates[math.random(#candidates)]
  local target = heard[key].name
  tried[key] = true
  attempts = attempts + 1

  local nonce = math.random(100000, 999999)
  pending = { target = target, targetKey = key, nonce = nonce, chunks = {},
              total = nil, got = 0, epoch = best,
              deadline = GetTime() + REQUEST_TIMEOUT, startedAt = GetTime() }

  armWatchdog(nonce)
  send(("Q:%d:%d"):format(nonce, mine), target)
  ns.Debug(("sync: asked %s for epoch %d (attempt %d)"):format(target, best, attempts))
end

--- Heard someone newer. Collect for a few seconds before acting, so a burst
--- of raid-time logins resolves into one request -- plus a random tail, so
--- that the requests themselves don't all land on one peer in one instant.
local function noteNewer(key, sender, epoch, share)
  local peer = heard[key]
  if peer and peer.epoch >= epoch then
    peer.name, peer.share = sender, share
    return
  end
  heard[key] = { epoch = epoch, name = sender, share = share }

  if collecting or pending or retryPending then return end
  collecting = true
  C_Timer.After(COLLECT_WINDOW + math.random() * REQUEST_JITTER, beginRequest)
end

-- ------------------------------------------------------------------
-- Serving
-- ------------------------------------------------------------------

local function sendNextChunk(gen)
  -- C_Timer.After can't be cancelled, so a chain from a serve that has since
  -- been torn down would otherwise wake up, find a *new* serving table, and
  -- start sending its chunks in parallel with the real chain -- doubling the
  -- whisper rate for the same transfer.
  if not serving or serving.gen ~= gen then return end

  local piece = serving.chunks[serving.index]
  if not piece then
    ns.Debug(("sync: finished serving %s (%d chunks)"):format(serving.target, #serving.chunks))
    serving = nil
    return
  end

  send(("D:%d:%d:%d:%s"):format(serving.nonce, serving.index, #serving.chunks, piece), serving.target)
  serving.index = serving.index + 1
  C_Timer.After(CHUNK_INTERVAL, function() sendNextChunk(gen) end)
end

local function handleRequest(senderKey, sender, nonce, theirEpoch)
  -- Membership: a Q arrives by whisper, so the transport proves nothing.
  -- Unknown (roster not loaded) is answered with a retryable refusal rather
  -- than served -- the requester comes back in a few seconds, and we don't
  -- hand the roster to someone we couldn't identify.
  local member = isGuildMember(senderKey)
  if member == false then
    ns.Debug(("sync: ignoring request from non-member %s"):format(sender))
    return
  elseif member == nil then
    return refuse(sender, nonce, "wait")
  end

  if not sharing() then return refuse(sender, nonce, "off") end
  if serving then return refuse(sender, nonce, "busy") end
  if serveCount >= MAX_SERVES then return refuse(sender, nonce, "cool") end

  local now = GetTime()
  if lastServeAt > 0 and (now - lastServeAt) < SERVE_COOLDOWN then
    return refuse(sender, nonce, "busy")
  end
  if servedTo[senderKey] and (now - servedTo[senderKey]) < REQUESTER_COOLDOWN then
    return refuse(sender, nonce, "cool")
  end
  if myEpoch() <= theirEpoch then
    return refuse(sender, nonce, "old")
  end

  local body = ns.Data:Export()
  if not body then return refuse(sender, nonce, "nodata") end

  local chunks = split(body, CHUNK_SIZE)
  if #chunks > MAX_CHUNKS then
    -- Loud on purpose, but only once a session. This check sits above every
    -- serve throttle below, and those throttles are only armed on the success
    -- path -- so an oversized roster reaches this line on every request from
    -- every peer, and an unguarded Warn here put one "please report this" in
    -- the chat frame per request (measured: 56 in three hours with six peers
    -- online). A warning nobody can act on, repeating, is just noise that
    -- teaches people to ignore the addon.
    if not warnedTooBig then
      warnedTooBig = true
      ns.Warn(("roster is too large to share (%d chunks, limit %d) -- please report this"):format(
        #chunks, MAX_CHUNKS))
    end
    return refuse(sender, nonce, "big")
  end

  serveGen = serveGen + 1
  serving = { target = sender, nonce = nonce, chunks = chunks, index = 1,
              gen = serveGen, startedAt = now }
  lastServeAt = now
  servedTo[senderKey] = now
  serveCount = serveCount + 1

  ns.Debug(("sync: serving %s -- %d bytes in %d chunks"):format(sender, #body, #chunks))
  sendNextChunk(serveGen)
end

-- ------------------------------------------------------------------
-- Parsing and validation
-- ------------------------------------------------------------------

--- Character names are not ASCII -- "Arrow" with a slashed o is in the live
--- roster today -- so the name half is "anything that isn't a separator"
--- rather than %a, which is locale-dependent and would silently drop those
--- members. Realm slugs come from Blizzard and are ASCII.
---
--- `|` is excluded because adopted keys are printed straight into the chat
--- frame and the roster browser, where "|cffff0000" is markup, not text.
local function validKey(key)
  if type(key) ~= "string" then return false end
  if #key > MAX_KEY_LEN then return false end
  return key:find("^[^%s:;=|]+%-[%a%d%-]+$") ~= nil
end

--- The same check, exposed so Core/Data.lua can apply it on the SEND side and
--- keep a key no receiver would accept out of the body, instead of shipping
--- one that every receiver silently drops (and, when a ";" splits an entry in
--- two, counts as a failure against MAX_BAD_FRACTION). One definition, both
--- directions.
Sync.IsValidKey = validKey

--- Peer-supplied text on its way to the chat frame. The H: fields are
--- captured as [^:;]*, which accepts "|" quite happily, and a rejected
--- snapshot's identity is printed verbatim below -- so a peer could put
--- markup, or a fake item link, into a guildmate's chat frame. Nothing here
--- is meant to render as markup, so the escape character stops being one.
--- Same treatment Modules/Roster.lua gives its candidate dump.
local function safeText(s)
  return (tostring(s):gsub("|", "!"))
end

local function parseBody(body)
  local epoch, region, realm, guild, schema, rest =
    body:match("^H:(%d+):([^:;]*):([^:;]*):([^:;]*):([^:;]*);(.*)$")
  if not epoch then return nil, "malformed header" end

  local ilvls, good, bad = {}, 0, 0
  for entry in rest:gmatch("[^;]+") do
    local key, value = entry:match("^([^=]+)=(%d+)$")
    local ilvl = tonumber(value)
    if key and ilvl and ilvl >= MIN_ILVL and ilvl <= MAX_ILVL and validKey(key) then
      -- A legitimate body is serialized from a Lua table, so its keys are
      -- unique by construction. A repeat is therefore never an honest
      -- export -- and it is the cheap way to fake a big roster that
      -- collapses to a handful of entries once adopted, which would then
      -- propagate epidemically. One duplicate is enough to throw it out.
      if ilvls[key] ~= nil then return nil, "duplicate key: " .. key end
      ilvls[key] = math.floor(ilvl)
      good = good + 1
    else
      bad = bad + 1
    end
  end

  if good == 0 then return nil, "no usable entries" end
  if bad / (good + bad) > MAX_BAD_FRACTION then
    return nil, ("%d of %d entries failed validation"):format(bad, good + bad)
  end

  return {
    epoch  = tonumber(epoch),
    region = region, realm = realm, guild = guild,
    schema = tonumber(schema),
    ilvls  = ilvls,
    count  = good,
  }
end

--- Is this epoch even worth chasing? Applied to an announcement, before we
--- spend an attempt on it -- otherwise "V:9999999999" costs us a request
--- and a blacklist entry for a peer that has nothing.
local function plausibleEpoch(epoch)
  local now = time()
  if epoch > now + MAX_FUTURE then return false end
  if now - epoch > MAX_AGE then return false end
  return true
end

local function validate(snap)
  if snap.epoch <= myEpoch() then return false, "not newer than ours" end
  if not plausibleEpoch(snap.epoch) then return false, "epoch out of range" end

  local id = ns.Data:IdentityKey()
  local theirs = ("%s/%s/%s"):format(snap.region or "", snap.realm or "", snap.guild or "")
  if not id or id ~= theirs then
    -- Escaped: this reason string is printed, and every part of `theirs`
    -- came off the wire.
    return false, ("identity mismatch (%s)"):format(safeText(theirs))
  end

  -- Size is checked against a fixed ceiling, NOT against
  -- GetNumGuildMembers(). That count fills in asynchronously after
  -- C_GuildInfo.GuildRoster(), so for the first seconds of a session it
  -- returns arbitrary partial values -- which is exactly when sync runs.
  -- Any bound derived from it, in either direction, rejects perfectly good
  -- snapshots as a matter of routine. Undersized bodies are impossible
  -- anyway (a transfer only parses once every chunk has arrived), and
  -- oversized ones are already bounded by MAX_CHUNKS long before this.
  if snap.count > MAX_ENTRIES then
    return false, ("entry count %d exceeds the ceiling"):format(snap.count)
  end

  return true
end

local function adopt(snap, from)
  local ok, why = validate(snap)
  if not ok then
    ns.Debug(("sync: rejected snapshot from %s -- %s"):format(from, why))
    -- Fall through to the next-best holder, exactly as abandon() does for a
    -- refusal. Without this, a peer whose snapshot we transfer in full and then
    -- reject keeps its place at the top of `heard` while staying in `tried`, so
    -- the rest of the window is spent on nobody -- and the next window picks the
    -- same peer again, because its epoch is still the highest one we have heard.
    -- One client carrying a roster nobody can adopt (a different guild, an
    -- out-of-range entry, a key shape peers drop) could therefore stall every
    -- stale client in the guild indefinitely. This makes that cost one wasted
    -- transfer per window instead of the whole window.
    beginRequest()
    return
  end

  -- AdoptSnapshot reports what actually happened rather than what was
  -- attempted: it returns false when the rebuild did not land on the
  -- snapshot -- no identity key, no ns.db, or the shipped file still winning.
  -- Discarding that boolean announced "roster updated" for data we had just
  -- thrown away, and reset attempts/tried so the same dead end was picked
  -- again on the next beat.
  local adopted = ns.Data:AdoptSnapshot({
    epoch      = snap.epoch,
    schema     = snap.schema,
    ilvls      = snap.ilvls,
    receivedAt = time(),
    from       = from,
  })
  if not adopted then
    ns.Debug(("sync: AdoptSnapshot did not take the snapshot from %s"):format(from))
    -- Not refunding the attempt is the point of this branch -- the transfer
    -- really did cost us one. But we are still stale, so fall through to the
    -- next-best holder exactly as the reject path above does; returning here
    -- left nothing in flight until the anti-entropy beat 15-25 minutes later.
    beginRequest()
    return
  end

  attempts = 0
  wipe(tried)   -- a peer that failed us at an older epoch is a fine source at the next one

  if ns.db.syncNotify then
    ns.Print(("roster updated from %s -- %s entries, exported %s"):format(
      ns.Colorize("value", from), ns.Colorize("value", ns.Data:ShareableCount()),
      ns.Data:GeneratedAt() or "?"))
  end

  -- We are a holder now, so say so. This is the beat that makes propagation
  -- epidemic rather than a star around whoever runs the exporter.
  scheduleAnnounce(ADOPT_MIN, ADOPT_MAX)
end

local function handleChunk(senderKey, nonce, seq, total, piece)
  -- Pull-only. A chunk is accepted only against an outstanding request:
  -- right peer, right nonce, inside the timeout. An unsolicited snapshot
  -- push is dropped without ever being parsed.
  if not pending then return end
  if senderKey ~= pending.targetKey or nonce ~= pending.nonce then return end
  if not (seq and total) or total < 1 or total > MAX_CHUNKS then
    return abandon("bad chunk header", nil)
  end

  -- Pin `total` to whatever the first accepted chunk claimed. Trusting it
  -- per-message let a peer shrink it mid-stream, pushing `got` past the new
  -- total while indices below it were still nil -- table.concat then threw,
  -- and the throw escaped before `pending` was cleared, wedging the
  -- requester until its timeout.
  if pending.total == nil then
    pending.total = total
    -- Now that the size is known, give the transfer a budget that fits it.
    -- Capped, so a peer can't pin us open by claiming a huge chunk count and
    -- then going quiet.
    local budget = math.min(15 + total * CHUNK_INTERVAL * 3, MAX_TRANSFER_TIME)
    pending.deadline = math.max(pending.deadline, GetTime() + budget)
  elseif total ~= pending.total then
    return abandon("chunk count changed mid-transfer", nil)
  end

  if seq < 1 or seq > pending.total then return end

  if pending.chunks[seq] == nil then
    pending.chunks[seq] = piece
    pending.got = pending.got + 1
  end

  if pending.got < pending.total then return end

  -- Belt and braces: never hand table.concat a range it can't fill.
  for i = 1, pending.total do
    if pending.chunks[i] == nil then return abandon("missing chunk", nil) end
  end

  local body = table.concat(pending.chunks, "", 1, pending.total)
  local from = pending.target
  pending = nil

  local snap, why = parseBody(body)
  if not snap then
    ns.Debug(("sync: unparseable snapshot from %s -- %s"):format(from, why))
    beginRequest()   -- same reasoning as the reject path in adopt()
    return
  end

  adopt(snap, from)
end

-- ------------------------------------------------------------------
-- Lifecycle -- called from Core/Events.lua, each call pcall-wrapped there.
-- ------------------------------------------------------------------

--- ADDON_LOADED, after ns.LoadConfig() and ns.Data:Build().
function Sync:OnInitialize()
  C_ChatInfo.RegisterAddonMessagePrefix(PREFIX)
  ns.frame:RegisterEvent("CHAT_MSG_ADDON")
end

--- PLAYER_LOGIN. ns.playerName / ns.playerRealmSlug are set by now.
function Sync:OnEnable()
  -- No math.randomseed() here: retail's Lua sandbox does not expose it, and
  -- calling it threw before OnEnable could schedule anything. The client
  -- seeds its own RNG per session, which is what the jitter needs anyway.

  if C_GuildInfo and C_GuildInfo.GuildRoster then
    C_GuildInfo.GuildRoster()  -- so GetNumGuildMembers() is meaningful later
  end

  scheduleAnnounce(LOGIN_MIN, LOGIN_MAX)
  scheduleAntiEntropy()
end

--- CHAT_MSG_ADDON, routed here alongside Comm.lua's handler.
---
--- `sender` is server-supplied and the payload cannot influence it, so it
--- is the one field worth trusting. What it proves depends on the channel:
--- a GUILD message can only come from a guild member, while a WHISPER can
--- come from anyone at all -- which is why whispered messages are checked
--- against the roster and GUILD messages are not.
function Sync:HandleAddonMessage(prefix, message, channel, sender)
  if prefix ~= PREFIX or not enabled() then return end
  if not ns.playerName then return end  -- pre-PLAYER_LOGIN; we can't identify ourselves yet

  local selfKey   = ns.Util.MakeKey(ns.playerName, ns.playerRealmSlug)
  local senderKey = ns.Util.NormalizeKey(sender, ns.playerRealmSlug)
  if not senderKey or senderKey == selfKey then return end

  local kind = message:sub(1, 1)

  if kind == "V" then
    -- Trailing fields we don't recognize are ignored on purpose, so adding
    -- one later is a non-breaking change rather than a RoSToolsD2. The
    -- share flag is absent on a hypothetical older client; assume it shares.
    local epoch, count, share = message:match("^V:(%d+):(%d+):?(%d*)")
    epoch = tonumber(epoch)
    if not epoch or not count then return end

    if channel == "WHISPER" then
      -- A whispered V claims to be a holder answering our announce. Nothing
      -- about the transport backs that up, so: it must be a guild member,
      -- and it must land in the window after we actually announced. An
      -- unloaded roster fails CLOSED here -- losing the fast nudge costs a
      -- few minutes, while trusting an unidentified whisper is how an
      -- outsider gets a fabricated roster written to disk.
      if isGuildMember(senderKey) ~= true then return end
      if not lastAnnounceAt or (GetTime() - lastAnnounceAt) > REPLY_WINDOW then return end
    elseif channel ~= "GUILD" then
      return
    end

    local mine = myEpoch()

    -- Suppress only on an exact restatement of our own epoch. Suppressing on
    -- ">=" also swallowed our announce whenever a *newer* peer spoke -- and
    -- that is precisely when the guild most needs to hear that we are stale.
    if channel == "GUILD" and epoch == mine and announcePending then
      announceSuppress = true
    end

    if epoch > mine then
      if plausibleEpoch(epoch) then
        noteNewer(senderKey, sender, epoch, share ~= "0")
      end
    elseif channel == "GUILD" and epoch < mine then
      -- A stale peer just announced. Answer it directly with our version
      -- number -- a nudge, never a push; they still have to ask us for the
      -- data. Direct rather than broadcast because exactly one client needs
      -- to hear it, and immediate because otherwise a client that logs in
      -- after everyone else has announced waits on the anti-entropy beat.
      replyVersion(senderKey, sender)
    end

  elseif kind == "Q" and channel == "WHISPER" then
    local nonce, theirEpoch = message:match("^Q:(%d+):(%d+)")
    if nonce then
      handleRequest(senderKey, sender, tonumber(nonce), tonumber(theirEpoch) or 0)
    end

  elseif kind == "D" and channel == "WHISPER" then
    -- The chunk body can contain ':' (the snapshot header does), so the last
    -- capture deliberately swallows the rest of the message.
    local nonce, seq, total, piece = message:match("^D:(%d+):(%d+):(%d+):(.*)$")
    if nonce then
      handleChunk(senderKey, tonumber(nonce), tonumber(seq), tonumber(total), piece)
    end

  elseif kind == "X" and channel == "WHISPER" then
    local nonce, reason = message:match("^X:(%d+):(%a*)")
    if nonce and pending and tonumber(nonce) == pending.nonce and senderKey == pending.targetKey then
      reason = reason or ""
      abandon("refused: " .. reason, RETRYABLE[reason] and reason or nil)
    end
  end
end

-- ------------------------------------------------------------------
-- /ros sync
-- ------------------------------------------------------------------

--- Manual kick. Clears every self-imposed limit on both sides, but keeps
--- `heard` -- throwing it away meant the command depended on a peer
--- re-nudging us, which its own 60s reply cooldown often forbade, so
--- "/ros sync now" could be a guaranteed no-op.
function Sync:ForceSync()
  attempts    = 0
  retryRounds = 0
  serveCount  = 0
  serveGen    = serveGen + 1   -- orphan any chunk chain still in flight
  lastServeAt = 0
  pending    = nil
  serving    = nil
  collecting = false
  retryPending = false
  wipe(tried)
  wipe(repliedTo)
  wipe(servedTo)
  rosterCache = nil

  scheduleAnnounce(0, 1)
  C_Timer.After(COLLECT_WINDOW, beginRequest)
end

--- What this client is doing right now, for /ros sync.
function Sync:Status()
  local known = 0
  for _ in pairs(heard) do known = known + 1 end
  return {
    pending    = pending and pending.target or nil,
    serving    = serving and serving.target or nil,
    attempts   = attempts,
    serveCount = serveCount,
    known      = known,
  }
end
