-- Offline protocol harness for Core/Sync.lua.
--
-- Simulates N WoW clients running the real Core/*.lua files against a
-- stubbed WoW API, a fake addon-message bus and a virtual clock. There is
-- no game client in CI and none on a dev box either, so this is the only
-- thing standing between a protocol regression and a guild full of wrong
-- rosters.
--
-- Run from the repo root:   lua5.1 Tools/sync-harness.lua
-- Exits non-zero on any failure.
--
-- Every scenario below is here because it once failed. Read the label as a
-- bug report, not a feature list.
--
-- Excluded from packaging and from luacheck (Tools/ is in .luacheckrc's
-- exclude_files), so it never ships to CurseForge.

local ROOT = os.getenv("ROSTOOLS_ROOT") or "./"

-- ---------- virtual clock ----------
local now, timers = 0, {}
local function After(delay, fn) timers[#timers + 1] = { at = now + delay, fn = fn } end
local function pump(seconds, step)
  step = step or 0.05
  local target = now + seconds
  while now < target do
    now = now + step
    local due = {}
    for i = #timers, 1, -1 do
      if timers[i].at <= now then due[#due + 1] = timers[i]; table.remove(timers, i) end
    end
    table.sort(due, function(a, b) return a.at < b.at end)
    for _, t in ipairs(due) do t.fn() end
  end
end

-- ---------- message bus ----------
local clients, online, roster, log = {}, {}, {}, {}
local errors = {}

-- How many characters the *guild* has, as GetNumGuildMembers() would report.
-- Distinct from how many of them this harness bothers to simulate: the
-- snapshot plausibility check compares entry count against guild size, so a
-- scenario carrying a 220-entry roster must say so or its own data looks
-- fabricated.
local guildSize = 221

local function deliver(prefix, msg, channel, sender, target)
  log[#log + 1] = ("%8.2f  %s -> %s [%s] %s"):format(now, sender, target or "GUILD", channel,
    #msg > 64 and (msg:sub(1, 64) .. "...") or msg)
  for name, c in pairs(clients) do
    if online[name] then
      -- A GUILD addon message only reaches guild members. That is the
      -- transport guarantee the trust model leans on, so the bus enforces it.
      local reach = (channel == "GUILD" and roster[sender] and roster[name])
                 or (channel == "WHISPER" and name == target)
      if reach then c.frame.handler(c.frame, "CHAT_MSG_ADDON", prefix, msg, channel, sender) end
    end
  end
end

-- ---------- client factory ----------
local FILES = { "Core/Init.lua", "Core/Util.lua", "Core/Config.lua",
                "Core/Data.lua", "Core/Comm.lua", "Core/Sync.lua", "Core/Events.lua" }

local function newClient(name, epoch, ilvls, opts)
  opts = opts or {}
  local env = {}
  for k, v in pairs(_G) do env[k] = v end
  local ns = {}
  local frame

  -- Per-client RNG, so clients draw independent jitter and a run is
  -- reproducible. opts.fixed01 pins math.random() to a constant, which is
  -- how the timing-sensitive scenarios below are made deterministic.
  local seed = opts.seed or 12345
  local function nextRand()
    seed = (1103515245 * seed + 12345) % 2147483648
    return seed / 2147483648
  end
  local mathEnv = {}
  for k, v in pairs(math) do mathEnv[k] = v end
  mathEnv.random = function(a, b)
    local r = opts.fixed01 or nextRand()
    if not a then return r end
    if not b then a, b = 1, a end
    return a + math.floor(r * (b - a + 1)) % (b - a + 1)
  end
  env.math = mathEnv

  env.RoSToolsDB = {}
  env.time, env.date = os.time, os.date
  env.C_AddOns = { GetAddOnMetadata = function() return "test" end }
  -- Always captured, not only when verbose: several scenarios assert on what
  -- this client told its user (the login count line, an adoption notice, a
  -- rejection reason printed with peer-supplied text in it).
  local said = {}
  env.DEFAULT_CHAT_FRAME = { AddMessage = function(_, m)
    said[#said + 1] = m
    if opts.verbose then
      print(("    [%s] %s"):format(name, (m:gsub("|c%x%x%x%x%x%x%x%x", ""):gsub("|r", ""))))
    end
  end }
  env.wipe = function(t) for k in pairs(t) do t[k] = nil end return t end
  env.CreateFrame = function()
    frame = { events = {} }
    function frame:RegisterEvent(e) self.events[e] = true end
    function frame:UnregisterEvent(e) self.events[e] = nil end
    function frame:SetScript(_, fn)
      self.handler = function(...)
        local ok, err = pcall(fn, ...)
        if not ok then errors[#errors + 1] = ("%s: %s"):format(name, tostring(err)) end
      end
    end
    return frame
  end
  -- Per-client timer accounting. C_Timer.After cannot be cancelled, so
  -- "how many timers are still armed" is a real, observable property of the
  -- code under test -- and for the deferred-broadcast guard in Comm.lua it is
  -- the ONLY observable one: every stacked retry is armed for the same
  -- instant, so a duplicate send is swallowed by lastBroadcastIlvl and the
  -- bus log looks identical either way.
  local armed = {}
  env.C_Timer = { After = function(delay, fn)
    local rec = { at = now + delay, fired = false }
    armed[#armed + 1] = rec
    After(delay, function() rec.fired = true; fn() end)
  end }
  env.C_ChatInfo = {
    RegisterAddonMessagePrefix = function() end,
    SendAddonMessage = function(prefix, msg, channel, target)
      if #msg > 255 then
        errors[#errors + 1] = ("%s sent a %d-byte message (cap is 255)"):format(name, #msg)
        return
      end
      deliver(prefix, msg, channel, name, target)
    end,
  }
  env.IsInGuild = function() return true end
  env.GetTime = function() return now end
  env.C_GuildInfo = { GuildRoster = function() end }
  env.GetNumGuildMembers = function()
    if opts.rosterLoading then return opts.rosterLoading() end
    return guildSize
  end
  env.GetGuildRosterInfo = function(i)
    local names = {}
    for who in pairs(roster) do names[#names + 1] = who end
    table.sort(names)
    return names[i]
  end
  env.UnitName = function() return name end
  -- opts.realmName lets a scenario put a client on a realm whose display
  -- name has a space in it -- the shape that used to be eaten by the title
  -- heuristic in NormalizeKey.
  env.GetRealmName = function() return opts.realmName or "Khadgar" end
  env.GetAverageItemLevel = function() return 300, 295 end
  env.UnitExists = function() return false end
  env.UnitIsPlayer = function() return false end
  env.GetPlayerInfoByGUID = function() return nil end

  -- opts.guild lets a scenario stand up a client whose Data/GuildData.lua is
  -- for a different guild -- what a mistyped sidecar data URL produces.
  local guildData = {
    meta = { generated_epoch = epoch, generated_at = "x", region = "us",
             realm = "khadgar", guild = opts.guild or "riddle-of-steel", schema = 3 },
    ilvls = ilvls,
  }

  for _, f in ipairs(FILES) do
    local chunk = assert(loadfile(ROOT .. f))
    setfenv(chunk, env)
    if f == "Core/Data.lua" then ns.GuildData = guildData end
    chunk("RoS-Tools", ns)
  end

  local c = {
    name = name, ns = ns, env = env, frame = frame, said = said,
    --- Timers this client has armed that have not fired yet.
    liveTimers = function()
      local n = 0
      for i = 1, #armed do
        if not armed[i].fired then n = n + 1 end
      end
      return n
    end,
    --- Did this client print anything containing `needle`? Colour escapes
    --- stripped, so an assertion matches the words, not the markup.
    saidHas = function(needle)
      for i = 1, #said do
        local line = said[i]:gsub("|c%x%x%x%x%x%x%x%x", ""):gsub("|r", "")
        if line:find(needle, 1, true) then return true end
      end
      return false
    end,
  }
  clients[name] = c
  online[name] = true
  if not opts.outsider then roster[name] = true end
  return c
end

local function login(c)
  c.frame.handler(c.frame, "ADDON_LOADED", "RoS-Tools")
  c.frame.handler(c.frame, "PLAYER_LOGIN")
end

local function reset(size)
  now, timers, clients, online, roster, log, errors = 0, {}, {}, {}, {}, {}, {}
  guildSize = size or 221
end

--- Send a raw message to one client as if it came from `from`.
local function inject(c, msg, channel, from)
  c.frame.handler(c.frame, "CHAT_MSG_ADDON", "RoSToolsD1", msg, channel, from)
end

-- ---------- assertions ----------
local pass, fail = 0, 0
local function check(label, cond, detail)
  if cond then
    pass = pass + 1
    print("  PASS  " .. label)
  else
    fail = fail + 1
    print("  FAIL  " .. label .. (detail and ("  -- " .. detail) or ""))
  end
end
local function section(s) print("\n== " .. s .. " ==") end

-- ---------- fixtures ----------
local NEW, OLD = os.time() - 600, os.time() - 86400 * 5

local function roster220(base, opts)
  opts = opts or {}
  local t = {}
  for i = 1, 220 do
    t[("%s%03d-%s"):format(opts.prefix or "Toon", i, opts.realm or "khadgar")] = base + (i % 40)
  end
  t["Arr\195\184w-antonidas"] = base + 7    -- non-ASCII name, must survive validation
  if opts.departed then t["Departed-khadgar"] = 111 end
  return t
end

-- ==================================================================
section("1. baseline: a stale peer adopts from a newer one")
-- ==================================================================
local A = newClient("Alpha", NEW, roster220(300), { seed = 11, verbose = true })
local B = newClient("Bravo", OLD, roster220(100, { departed = true }), { seed = 22, verbose = true })
login(A); login(B); pump(60)

check("adopted the newer epoch", B.ns.Data:GeneratedEpoch() == NEW)
check("source reports 'sync'", (select(1, B.ns.Data:SourceInfo())) == "sync")
check("values replaced", B.ns.Data:GetByKey("Toon001-khadgar") == A.ns.Data:GetByKey("Toon001-khadgar"))
check("non-ASCII name survived", B.ns.Data:GetByKey("Arr\195\184w-antonidas") ~= nil)
check("DEPARTURE: removed member is gone", B.ns.Data:GetByKey("Departed-khadgar") == nil)
check("counts match", A.ns.Data:Count() == B.ns.Data:Count())
check("snapshot persisted", next(B.env.RoSToolsDB.syncedData or {}) ~= nil)
check("holder adopted nothing", (select(1, A.ns.Data:SourceInfo())) == "file")

-- ==================================================================
section("2. relay: a peer that just adopted becomes a source")
-- ==================================================================
online["Alpha"] = false
local C = newClient("Charlie", OLD, roster220(100), { seed = 33, verbose = true })
login(C); pump(90)
check("RELAY: adopted with the original holder offline", C.ns.Data:GeneratedEpoch() == NEW)
check("source was the relay, not the origin", (select(2, C.ns.Data:SourceInfo())).from == "Bravo")

-- ==================================================================
section("3. shipped file newer than the snapshot reclaims priority")
-- ==================================================================
B.ns.GuildData.meta.generated_epoch = os.time()
B.ns.Data:Build()
check("file wins", (select(1, B.ns.Data:SourceInfo())) == "file")
check("stale snapshot cleared from SavedVariables", next(B.env.RoSToolsDB.syncedData or {}) == nil)

-- ==================================================================
section("4. REGRESSION: the adopter must announce (the epidemic beat)")
-- Once broken by announceSuppress surviving scheduleAnnounce's early
-- return: a newer peer's announce poisoned the adopter's pending announce,
-- so nobody downstream ever heard about the new data.
-- ==================================================================
reset()
local hold = newClient("Holder", NEW, roster220(300), { seed = 11, fixed01 = 0 })   -- announces at 5s
local late = newClient("Late",   OLD, roster220(100), { seed = 22, fixed01 = 1 })   -- announces at 15s
login(hold); login(late); pump(60)

local announced = false
for _, line in ipairs(log) do
  if line:find("Late %-> GUILD %[GUILD%] V:" .. tostring(NEW)) then announced = true end
end
check("adopter reached the new epoch", late.ns.Data:GeneratedEpoch() == NEW)
check("BEAT 4: adopter announced its new epoch on GUILD", announced)

-- ==================================================================
section("5. REGRESSION: contention -- one holder, four stale peers")
-- Once broken by treating a 'busy' refusal as permanent: everyone's
-- collect window expired in the same instant, one peer won, and the other
-- three blacklisted the only holder in the guild for the whole session.
-- ==================================================================
reset()
local h = newClient("Holder", NEW, roster220(300), { seed = 7 })
login(h)
local stale = {}
for i = 1, 4 do
  stale[i] = newClient("Stale" .. i, OLD, roster220(100), { seed = 100 + i * 37 })
  login(stale[i])
end
pump(600)
local converged = 0
for i = 1, 4 do
  if stale[i].ns.Data:GeneratedEpoch() == NEW then converged = converged + 1 end
end
check("all four contending peers converged", converged == 4, ("only %d of 4"):format(converged))

-- ==================================================================
section("6. REGRESSION: a wedged client recovers on the anti-entropy beat")
-- Once broken by attempts/tried never resetting: three refusals and the
-- client stayed stale until relog, with willing holders in earshot.
-- ==================================================================
reset()
local victim = newClient("Victim", OLD, roster220(100), { seed = 5 })
login(victim); pump(2)
roster["Ghost1"], roster["Ghost2"], roster["Ghost3"] = true, true, true
roster["Ghost4"], roster["Ghost5"] = true, true
for i = 1, 5 do
  inject(victim, ("V:%d:220:1"):format(NEW), "GUILD", "Ghost" .. i)
end
pump(120)   -- every request times out; the ghosts do not exist
check("attempt budget was spent", victim.ns.Sync:Status().attempts >= 1)
local real = newClient("RealHolder", NEW, roster220(300), { seed = 9 })
login(real)
pump(2400)  -- past one anti-entropy window
check("RECOVERY: converged after the amnesty beat", victim.ns.Data:GeneratedEpoch() == NEW,
      "still " .. tostring(victim.ns.Data:GeneratedEpoch()))

-- ==================================================================
section("7. hostile input")
-- ==================================================================
reset()
local v = newClient("Victim", OLD, roster220(100), { seed = 3 })
login(v); pump(20)
local before = v.ns.Data:GeneratedEpoch()

-- 7a. An outsider whispers a version claim and then a full snapshot.
local mal = newClient("Mallory", NEW, roster220(999), { seed = 4, outsider = true })
local body = mal.ns.Data:Export()
inject(v, ("V:%d:220:1"):format(NEW), "WHISPER", "Mallory")
pump(20)
check("outsider's whispered version claim ignored", v.ns.Data:GeneratedEpoch() == before)

-- 7b. Unsolicited snapshot push.
inject(v, ("D:%d:1:1:%s"):format(999999, body), "WHISPER", "Mallory")
check("unsolicited snapshot push ignored", v.ns.Data:GeneratedEpoch() == before)

-- 7c. An outsider asks a holder to serve the roster.
reset()
local holder = newClient("Holder", NEW, roster220(300), { seed = 8 })
login(holder); pump(20)
local sent = #log
newClient("Spy", OLD, {}, { seed = 6, outsider = true })
inject(holder, "Q:424242:0", "WHISPER", "Spy")
pump(20)
local served = 0
for i = sent + 1, #log do
  if log[i]:find("%-> Spy %[WHISPER%] D:") then served = served + 1 end
end
check("outsider was not served the roster", served == 0, served .. " chunks leaked")

-- 7d. A body of one key repeated 300 times must not pass as 300 members and
--     collapse the roster to a single entry -- which would then be
--     re-announced to the guild as authoritative.
--
--     The peer here is a GHOST: it announces but never serves, so the
--     victim's request stays outstanding and we can answer it ourselves
--     with a hostile body under the real nonce. An earlier version of this
--     test scraped the nonce from the wrong log line, silently got 0, and
--     passed without the code under test ever running.
reset()
local target = newClient("Target", OLD, roster220(100), { seed = 2 })
login(target); pump(2)
roster["Ghost"] = true
inject(target, ("V:%d:300:1"):format(NEW), "GUILD", "Ghost")
pump(10)

local nonce
for i = #log, 1, -1 do
  local n = log[i]:match("Target %-> Ghost %[WHISPER%] Q:(%d+):")
  if n then nonce = tonumber(n) break end
end
check("victim did ask the ghost (test is not vacuous)", nonce ~= nil)

local dupeBody = { ("H:%d:us:khadgar:riddle-of-steel:3;"):format(NEW) }
for _ = 1, 300 do dupeBody[#dupeBody + 1] = "Fake-khadgar=500;" end
dupeBody = table.concat(dupeBody)
local pieces = {}
for j = 1, #dupeBody, 200 do pieces[#pieces + 1] = dupeBody:sub(j, j + 199) end
for i, piece in ipairs(pieces) do
  inject(target, ("D:%d:%d:%d:%s"):format(nonce, i, #pieces, piece), "WHISPER", "Ghost")
end
check("duplicate-key body rejected, roster intact",
      target.ns.Data:Count() == 221, "count is " .. target.ns.Data:Count())

-- 7e. UI markup in a key must be rejected, not printed.
reset()
local t2 = newClient("Target", OLD, roster220(100), { seed = 2 })
login(t2)
check("validKey pattern rejects '|' markup",
      ("|cffff0000Impostor-khadgar"):find("^[^%s:;=|]+%-[%a%d%-]+$") == nil)
check("validKey pattern still accepts an ordinary key",
      ("Arr\195\184w-antonidas"):find("^[^%s:;=|]+%-[%a%d%-]+$") ~= nil)

-- ==================================================================
section("8. REGRESSION: a large guild still transfers")
-- The old 64-chunk ceiling silently refused any guild past ~400 members.
-- ==================================================================
-- 900 members is ~154 chunks, ~39s of chunk stream. That is past the
-- initial 30s request watchdog, which is exactly the case a nonce-guarded
-- (rather than deadline-guarded) timeout killed: the holder sent every
-- chunk and the requester threw the transfer away before the last one.
reset(900)
local big = {}
for i = 1, 900 do big[("Thunderbringer%03d-moon-guard"):format(i)] = 300 + (i % 40) end
local bigOld = {}
for i = 1, 900 do bigOld[("Thunderbringer%03d-moon-guard"):format(i)] = 100 end
local src = newClient("Src", NEW, big,    { seed = 13 })
local dst = newClient("Dst", OLD, bigOld, { seed = 14 })
login(src); login(dst); pump(400)
check("900-member roster transferred", dst.ns.Data:GeneratedEpoch() == NEW,
      "epoch " .. tostring(dst.ns.Data:GeneratedEpoch()))
check("values actually replaced across the large transfer",
      dst.ns.Data:GetByKey("Thunderbringer001-moon-guard")
        == src.ns.Data:GetByKey("Thunderbringer001-moon-guard"))

-- ==================================================================
section("9. REGRESSION: a partially-loaded guild roster must not reject good data")
-- GetNumGuildMembers() fills in asynchronously, so during the first seconds
-- of a session it returns partial values -- exactly when sync runs. A
-- two-sided plausibility check rejected a perfectly good snapshot on that
-- partial count, and the rejection was permanent for the session.
-- ==================================================================
reset()
local loading = 40   -- roster still filling in
local src2 = newClient("Src", NEW, roster220(300), { seed = 21 })
local dst2 = newClient("Dst", OLD, roster220(100), { seed = 22,
                       rosterLoading = function() return loading end })
login(src2); login(dst2); pump(60)
check("adopted while the roster was still loading", dst2.ns.Data:GeneratedEpoch() == NEW,
      "epoch " .. tostring(dst2.ns.Data:GeneratedEpoch()))

-- ==================================================================
section("10. REGRESSION: pre-2.0 leftovers must not leak into the guild")
-- The legacy RiddledTooltip_DB backfill is local archaeology. Exporting it
-- pushed long-departed members onto every client -- and realm-less legacy
-- keys failed enough of the receiver's per-entry checks to make it reject
-- the whole snapshot, so the guild's newest holder became the one client
-- nobody could sync from.
-- ==================================================================
reset()
local legacy = newClient("Legacy", NEW, roster220(300), { seed = 31 })
legacy.env.RiddledTooltip_DB = { ["Ghost001-khadgar"] = 404, ["Barename"] = 250 }
legacy.env.RiddledTooltip_Meta = { region = "eu", guild = "other-guild", generated_epoch = 1 }
legacy.ns.Data:Build()
local exported = legacy.ns.Data:Export() or ""
check("legacy key not exported", not exported:find("Ghost001", 1, true))
check("realm-less legacy key not exported", not exported:find("Barename", 1, true))
check("legacy meta did not hijack the guild identity",
      legacy.ns.Data:Meta().guild == "riddle-of-steel"
      and legacy.ns.Data:Meta().region == "us",
      legacy.ns.Data:Meta().region .. "/" .. tostring(legacy.ns.Data:Meta().guild))

local plain = newClient("Plain", OLD, roster220(100), { seed = 32 })
login(legacy); login(plain); pump(60)
check("peer still converged from a client with legacy data",
      plain.ns.Data:GeneratedEpoch() == NEW)
check("ghost did not propagate", plain.ns.Data:GetByKey("Ghost001-khadgar") == nil)

-- ==================================================================
section("11. REGRESSION: an unloadable data file must not erase the snapshot")
-- chooseSnapshot() deleted every stored entry when IdentityKey() was nil.
-- WoW skips a Lua file with a syntax error and keeps loading, so "the
-- shipped export didn't load" is a real state -- and it used to destroy the
-- only copy of the adopted roster, permanently.
-- ==================================================================
reset()
local keeper = newClient("Keeper", OLD, roster220(100), { seed = 41 })
login(keeper); pump(1)
keeper.ns.Data:AdoptSnapshot({ epoch = NEW, schema = 3, ilvls = roster220(300),
                               receivedAt = os.time(), from = "Someone" })
check("snapshot adopted", (select(1, keeper.ns.Data:SourceInfo())) == "sync")
keeper.ns.GuildData = nil           -- as if Data/GuildData.lua failed to load
keeper.ns.Data:Build()
check("stored snapshot survived a missing data file",
      next(keeper.env.RoSToolsDB.syncedData or {}) ~= nil)

-- ==================================================================
section("12. REGRESSION: /ros set rejects a value of the wrong type")
-- `/ros set staleDays soon` used to persist the string, and every later
-- comparison threw -- at login and on every player tooltip -- until the
-- user hand-edited SavedVariables.
-- ==================================================================
reset()
local opt = newClient("Opt", NEW, roster220(300), { seed = 51 })
login(opt)
local okSet, errSet = opt.ns.SetOption("staleDays", "soon")
check("string into a numeric option is refused", okSet == nil, tostring(errSet))
check("the old value is intact and still comparable",
      type(opt.ns.db.staleDays) == "number")
check("a valid numeric set still works", opt.ns.SetOption("staleDays", 30) == 30)
check("a boolean toggle still works", type(opt.ns.SetOption("syncShare")) == "boolean")

-- ==================================================================
section("14. REGRESSION: a peer whose snapshot we reject must not block us")
-- Found by audit, not in the field. A transfer that SUCCEEDS and is then
-- rejected used to return without re-requesting, while a refusal fell
-- through to the next holder. So one client carrying a roster nobody can
-- adopt -- a mistyped sidecar data URL pointing at another guild's export,
-- say -- sat at the top of `heard` with the highest epoch, was chosen
-- every window, and every stale client in the guild spent its whole
-- window transferring a snapshot it would throw away. Measured before the
-- fix: 3 clients, 3 simulated hours, zero convergence.
-- ==================================================================
reset()
local WRONG = os.time() - 300           -- newest epoch in the guild, and unusable
local RIGHT = os.time() - 900           -- older, but the real roster
local poison = newClient("Poison", WRONG, roster220(300), { seed = 61, guild = "other-guild" })
local good   = newClient("Good",   RIGHT, roster220(300), { seed = 62 })
local stale  = newClient("Stale",  OLD,   roster220(100), { seed = 63 })
login(poison); login(good); login(stale); pump(120)

check("FALL-THROUGH: reached the real roster despite the higher bad epoch",
      stale.ns.Data:GeneratedEpoch() == RIGHT,
      "epoch is " .. tostring(stale.ns.Data:GeneratedEpoch()))
check("adopted from the legitimate holder",
      (select(2, stale.ns.Data:SourceInfo())).from == "Good")
check("the wrong-guild roster was never adopted",
      stale.ns.Data:GeneratedEpoch() ~= WRONG)

-- ==================================================================
section("15. an out-of-range item level no longer has a dated ceiling")
-- The per-entry cap used to be 999, a few content patches above the
-- guild's real numbers. The season retail passed it, every export would
-- have become 100% invalid entries and sharing would have stopped
-- guild-wide with nothing to point at.
-- ==================================================================
reset()
local high = roster220(300)
high["Bigilvl-khadgar"] = 1400          -- plausible in a few expansions' time
local futureHolder = newClient("Future", NEW, high, { seed = 71 })
local futurePeer   = newClient("Peer",   OLD, roster220(100), { seed = 72 })
login(futureHolder); login(futurePeer); pump(90)

check("a 1400 ilvl still propagates", futurePeer.ns.Data:GetByKey("Bigilvl-khadgar") == 1400)
check("and the rest of the roster came with it",
      futurePeer.ns.Data:Count() == futureHolder.ns.Data:Count())

-- ==================================================================
section("16. REGRESSION: NormalizeKey must survive a realm with a space")
-- The title heuristic ("Brewmaster Peidae" -> "Peidae") took the last
-- whitespace-delimited token of the WHOLE string, so a realm display name
-- containing a space was eaten alive: "Peidae-Moon Guard" normalized to
-- "Guard-khadgar", and every cross-realm lookup on such a realm -- tooltip
-- headers and Comm's sender key both land here -- silently resolved to
-- nothing. Split first, then apply the heuristic to the name half only.
-- ==================================================================
reset()
local ukey = newClient("Keys", NEW, roster220(300), { seed = 81 })
login(ukey)
local NK = ukey.ns.Util.NormalizeKey

check("bare name falls back to our realm",
      NK("Peidae", "khadgar") == "Peidae-khadgar", tostring(NK("Peidae", "khadgar")))
check("leading title is still dropped",
      NK("Brewmaster Peidae", "khadgar") == "Peidae-khadgar",
      tostring(NK("Brewmaster Peidae", "khadgar")))
check("space-stripped realm still slugs",
      NK("Peidae-MoonGuard", "khadgar") == "Peidae-moon-guard",
      tostring(NK("Peidae-MoonGuard", "khadgar")))
check("SPACED REALM: 'Peidae-Moon Guard' keeps its name and its realm",
      NK("Peidae-Moon Guard", "khadgar") == "Peidae-moon-guard",
      tostring(NK("Peidae-Moon Guard", "khadgar")))
check("title AND spaced realm together",
      NK("Brewmaster Peidae-Moon Guard", "khadgar") == "Peidae-moon-guard",
      tostring(NK("Brewmaster Peidae-Moon Guard", "khadgar")))
check("a colored spaced-realm name normalizes the same way",
      NK("|cffff8000Peidae-Moon Guard|r", "khadgar") == "Peidae-moon-guard",
      tostring(NK("|cffff8000Peidae-Moon Guard|r", "khadgar")))

-- The degenerate inputs. There is no post-title empty check inside
-- NormalizeKey -- it could not fire, because `text` is trimmed and non-empty
-- before the split -- so these pin the precondition that makes that true.
check("an empty or whitespace-only string is refused up front",
      NK("", "khadgar") == nil and NK("   ", "khadgar") == nil
        and NK("|cffff8000|r", "khadgar") == nil)
check("a name half padded by the split still yields the name",
      NK("Peidae -Moon Guard", "khadgar") == "Peidae-moon-guard",
      tostring(NK("Peidae -Moon Guard", "khadgar")))

-- ==================================================================
section("17. REGRESSION: a broadcast blocked by the cooldown must be deferred")
-- The 60s self-broadcast cooldown DROPPED the change instead of holding it.
-- Nothing but PLAYER_EQUIPMENT_CHANGED re-enters that path, so a genuine
-- upgrade landing inside the window was never broadcast at all -- the guild
-- kept seeing the pre-upgrade number for the rest of the session.
-- ==================================================================
reset()
local sender = newClient("Sender", NEW, roster220(300), { seed = 91 })
local recvr  = newClient("Recvr",  NEW, roster220(300), { seed = 92 })
login(sender); login(recvr)

local myIlvl = 300
sender.env.GetAverageItemLevel = function() return myIlvl, myIlvl end
local SENDER_KEY = "Sender-khadgar"

sender.ns.Comm:HandleEquipmentChanged()
pump(5)
check("the first broadcast lands (test is not vacuous)",
      recvr.ns.Data:GetByKey(SENDER_KEY) == 300,
      tostring(recvr.ns.Data:GetByKey(SENDER_KEY)))

myIlvl = 320
sender.ns.Comm:HandleEquipmentChanged()
pump(15)   -- ~t=20, well inside the 60s cooldown
check("the upgrade is held, not sent, inside the cooldown",
      recvr.ns.Data:GetByKey(SENDER_KEY) == 300,
      tostring(recvr.ns.Data:GetByKey(SENDER_KEY)))

pump(60)   -- past the cooldown, with NO further equipment change at all
check("DEFERRED: the held upgrade is broadcast when the cooldown lifts",
      recvr.ns.Data:GetByKey(SENDER_KEY) == 320,
      "still " .. tostring(recvr.ns.Data:GetByKey(SENDER_KEY)))

-- A full gear swap fires PLAYER_EQUIPMENT_CHANGED per slot. C_Timer.After
-- cannot be cancelled, so an unguarded deferral arms one timer per change
-- and holds them all until the window lifts.
--
-- Counting SENDS cannot see that, and the two assertions that did were
-- vacuous: deleting the `if not cooldownPending then` guard entirely left
-- this whole harness green. Every stacked timer is armed for the same
-- instant, so the first one to fire sets lastBroadcastIlvl and the rest
-- return early -- one send either way. What the guard actually buys is
-- BOUNDED TIMER ACCUMULATION, so that is what is measured: how many timers
-- this client is still holding when the burst is over.
--
-- The pump inside the loop matters. Without it the debounce swallows the
-- whole burst into one call and only one deferral is ever armed, guard or
-- no guard; each change has to reach broadcastIfChanged() on its own for the
-- accumulation to exist at all.
myIlvl = 340
local timersBefore = sender.liveTimers()
for _ = 1, 6 do
  sender.ns.Comm:HandleEquipmentChanged()
  pump(4)   -- past the 2s debounce, still well inside the 60s cooldown
end
local stillArmed = sender.liveTimers() - timersBefore
check("BOUNDED: a burst inside the cooldown leaves ONE retry timer armed, not one per change",
      stillArmed == 1, stillArmed .. " timers still pending after 6 equipment changes")

pump(180)
local sends340 = 0
for _, line in ipairs(log) do
  if line:find("Sender %-> GUILD %[GUILD%] 340") then sends340 = sends340 + 1 end
end
check("a burst of equipment changes still broadcasts exactly once",
      sends340 == 1, sends340 .. " sends of 340")
check("and the burst value did arrive", recvr.ns.Data:GetByKey(SENDER_KEY) == 340,
      tostring(recvr.ns.Data:GetByKey(SENDER_KEY)))

-- ==================================================================
section("18. REGRESSION: Export must refuse a header no peer can parse")
-- The H: header is ":"-delimited and parseBody captures each field as
-- [^:;]*, so a ":" in meta.guild produced a body that EVERY receiver
-- transferred in full and then discarded as "malformed header" -- while the
-- holder burned a serve out of its per-window budget on each one. Refusing
-- to serialize turns that into a "nodata" refusal peers fall through in one
-- round trip.
-- ==================================================================
reset()
local BAD_GUILDS = { "riddle:of:steel", "riddle;of;steel", "riddle|of|steel" }
for i, badGuild in ipairs(BAD_GUILDS) do
  local c = newClient("Bad" .. i, NEW, roster220(300), { seed = 200 + i, guild = badGuild })
  check(("Export refuses meta.guild = %q"):format(badGuild), c.ns.Data:Export() == nil,
        tostring(c.ns.Data:Export()))
end
check("a clean identity still exports",
      newClient("Clean", NEW, roster220(300), { seed = 210 }).ns.Data:Export() ~= nil)

reset()
local holder2 = newClient("Holder", NEW, roster220(300), { seed = 211, guild = "riddle:of:steel" })
local peer2   = newClient("Peer",   OLD, roster220(100), { seed = 212, guild = "riddle:of:steel" })
login(holder2); login(peer2); pump(180)

check("the holder served nothing it knew nobody could parse",
      holder2.ns.Sync:Status().serveCount == 0,
      holder2.ns.Sync:Status().serveCount .. " serves burned")
local refusedNodata = false
for _, line in ipairs(log) do
  if line:find("Holder %-> Peer %[WHISPER%] X:%d+:nodata") then refusedNodata = true end
end
check("the peer was refused with 'nodata' instead of being fed a bad body", refusedNodata)
check("the peer is still on its own file, having adopted nothing",
      peer2.ns.Data:GeneratedEpoch() == OLD, tostring(peer2.ns.Data:GeneratedEpoch()))

-- ==================================================================
section("19. REGRESSION: the announced count must describe what Export() writes")
-- The V: announcement used to quote a count that included the pre-2.0
-- RiddledTooltip_DB backfill, which Export() excludes -- so it advertised a
-- roster size the wire body never contained, on exactly the clients that
-- have legacy leftovers.
--
-- The wire number lives in ShareableCount(), separately from Count(), which
-- is the size of the table this client answers lookups from. Conflating the
-- two is what made "/ros reload" and the login line under-report; see
-- section 21.
-- ==================================================================
reset()
local acct = newClient("Acct", NEW, roster220(300), { seed = 131 })
acct.env.RiddledTooltip_DB = { ["Ghost001-khadgar"] = 404, ["Ghost002-khadgar"] = 405 }
acct.ns.Data:Build()

local acctBody, acctN = acct.ns.Data:Export()
check("ShareableCount() equals the number of entries Export() writes",
      acct.ns.Data:ShareableCount() == acctN,
      ("ShareableCount()=%d, Export()=%d"):format(acct.ns.Data:ShareableCount(), acctN))
check("and Count() is the bigger local number, legacy entries included",
      acct.ns.Data:Count() == acctN + 2,
      ("Count()=%d, Export()=%d"):format(acct.ns.Data:Count(), acctN))
check("legacy keys still resolve locally (the tooltip path wants them)",
      acct.ns.Data:GetByKey("Ghost001-khadgar") == 404)
check("and are still kept off the wire",
      not acctBody:find("Ghost001", 1, true) and not acctBody:find("Ghost002", 1, true))

login(acct); pump(60)
local announcedCount
for _, line in ipairs(log) do
  local n = line:match("Acct %-> GUILD %[GUILD%] V:%d+:(%d+):")
  if n then announcedCount = tonumber(n) end
end
check("the V: announcement quotes the serializable count (test is not vacuous)",
      announcedCount ~= nil)
check("ANNOUNCED SIZE: what we advertise is what we would send",
      announcedCount == acctN, ("announced %s, body has %d"):format(tostring(announcedCount), acctN))

-- ==================================================================
section("20. REGRESSION: adopt() must believe AdoptSnapshot's return value")
-- AdoptSnapshot re-runs the source selection and reports what actually
-- happened. adopt() threw that boolean away, so a snapshot that validated
-- and was then discarded by the rebuild still printed "roster updated
-- from ..." and still reset attempts/tried -- handing the next window back
-- to the same dead end.
--
-- This section used to drive that by writing ns.GuildData.meta.generated_epoch
-- straight into a running client with no rebuild, which put the addon in a
-- state it has no path into: the shipped file is read once, at load, and
-- every rebuild re-reads it, so the built epoch can never trail it. Testing
-- an unreachable state proves nothing about the reachable ones, so it is
-- gone. What is asserted instead is the contract itself, in two halves.
--
-- 20a: AdoptSnapshot's return value, driven entirely through Data's public
-- API -- both ways it can legitimately say "no".
-- ==================================================================
reset()
local ret = newClient("Ret", NEW, roster220(300), { seed = 121 })
login(ret); pump(1)

check("AdoptSnapshot refuses a snapshot the shipped file already beats",
      ret.ns.Data:AdoptSnapshot({ epoch = OLD, schema = 3, ilvls = roster220(100),
                                  receivedAt = os.time(), from = "Nobody" }) == false)
check("and the client is still on its own file",
      (select(1, ret.ns.Data:SourceInfo())) == "file")
check("AdoptSnapshot accepts one that beats it (test is not vacuous)",
      ret.ns.Data:AdoptSnapshot({ epoch = os.time() - 60, schema = 3, ilvls = roster220(500),
                                  receivedAt = os.time(), from = "Somebody" }) == true)

-- Without an identity there is nothing to key the store by, and a client
-- whose Data/GuildData.lua failed to load is a real state -- see section 11.
local noid = newClient("NoId", NEW, roster220(300), { seed = 123 })
login(noid); pump(1)
noid.ns.GuildData = nil
check("AdoptSnapshot refuses when there is no identity to store under",
      noid.ns.Data:AdoptSnapshot({ epoch = os.time(), schema = 3, ilvls = roster220(300),
                                   receivedAt = os.time(), from = "Nobody" }) == false)

-- 20b: what adopt() does with a "no". The state is not reachable from the
-- outside -- every path that produces it is caught by validate() first --
-- so it is driven by stubbing the collaborator whose answer adopt() is
-- required to honour, rather than by faking an impossible client state.
-- Two holders are online, so falling through to the next one is observable.
reset()
local sink  = newClient("Sink",   OLD, roster220(100), { seed = 124 })
local one   = newClient("HolderA", NEW, roster220(300), { seed = 125 })
local two   = newClient("HolderB", NEW, roster220(300), { seed = 126 })
login(sink); login(one); login(two)
sink.ns.Data.AdoptSnapshot = function() return false end   -- every adopt is refused
pump(240)

local askedBy = {}
for _, line in ipairs(log) do
  local who = line:match("Sink %-> (%a+) %[WHISPER%] Q:")
  if who then askedBy[who] = true end
end
local askedCount = 0
for _ in pairs(askedBy) do askedCount = askedCount + 1 end

check("the refused snapshot was not adopted (test is not vacuous)",
      sink.ns.Data:GeneratedEpoch() == OLD, tostring(sink.ns.Data:GeneratedEpoch()))
check("ADOPT RETURN: no success line for a snapshot that was not adopted",
      not sink.saidHas("roster updated from"), table.concat(sink.said, " | "))
check("and the attempt budget was not refunded on the failed adopt",
      sink.ns.Sync:Status().attempts >= 1,
      "attempts = " .. sink.ns.Sync:Status().attempts)
check("FALL-THROUGH: it tried the other holder instead of going quiet until anti-entropy",
      askedCount >= 2, ("asked %d distinct holders"):format(askedCount))

-- ==================================================================
section("21. REGRESSION: the login line counts the LOCAL table")
-- Count() became the wire number Sync announces, and the login line was
-- changed with it -- so a client whose entries are all pre-2.0 leftovers was
-- told it had "loaded -- 0 entries" while every tooltip on screen worked.
-- ==================================================================
reset()
local mixed = newClient("Mixed", NEW, { ["Real001-khadgar"] = 620 }, { seed = 141 })
mixed.env.RiddledTooltip_DB = { ["Ghost001-khadgar"] = 404, ["Ghost002-khadgar"] = 405 }
login(mixed)
check("the legacy entries answer lookups (test is not vacuous)",
      mixed.ns.Data:GetByKey("Ghost001-khadgar") == 404)
check("LOGIN COUNT: the line counts everything the client can answer with",
      mixed.saidHas("loaded -- 3 entries"), table.concat(mixed.said, " | "))

reset()
local allLegacy = newClient("Legacy2", NEW, {}, { seed = 142 })
allLegacy.env.RiddledTooltip_DB = { ["Ghost001-khadgar"] = 404 }
login(allLegacy)
check("an all-legacy client is not told it loaded nothing",
      allLegacy.saidHas("loaded -- 1 entries"), table.concat(allLegacy.said, " | "))
check("while the wire number for such a client is still zero",
      allLegacy.ns.Data:ShareableCount() == 0 and allLegacy.ns.Data:Export() == nil)

-- ==================================================================
section("22. REGRESSION: a spaced realm through Comm's self-echo path")
-- Comm keys an update by `sender`, and compares it against our own key to
-- drop our own GUILD echo. Both sides go through NormalizeKey, which used to
-- take the last whitespace-delimited token of the WHOLE string -- so on a
-- realm whose display name has a space in it, "Peidae-MoonGuard" normalized
-- to "Guard-moon-guard": the echo was not recognized as ours, and every
-- guildmate's update landed under a mangled key that nothing could look up.
-- ==================================================================
reset()
local spaced = newClient("Peidae", NEW, roster220(300), { seed = 151, realmName = "Moon Guard" })
login(spaced)
check("the client knows its own realm slug (test is not vacuous)",
      spaced.ns.playerRealmSlug == "moon-guard", tostring(spaced.ns.playerRealmSlug))

-- Our own broadcast, echoed back to us by the server exactly as WoW does.
spaced.frame.handler(spaced.frame, "CHAT_MSG_ADDON", "RoSTools1", "615", "GUILD",
                     "Peidae-MoonGuard")
check("SELF-ECHO: our own broadcast is recognized and dropped",
      spaced.ns.Data:IsLive("Peidae-moon-guard") == false)

-- A guildmate on the same spaced realm still gets through, under a key the
-- rest of the addon can actually look up.
spaced.frame.handler(spaced.frame, "CHAT_MSG_ADDON", "RoSTools1", "618", "GUILD",
                     "Helltz-MoonGuard")
check("a guildmate's update lands under its real key",
      spaced.ns.Data:GetByKey("Helltz-moon-guard") == 618,
      tostring(spaced.ns.Data:GetByKey("Helltz-moon-guard")))
check("and nothing was filed under the mangled 'Guard-...' key",
      spaced.ns.Data:GetByKey("Guard-moon-guard") == nil)

-- ==================================================================
section("23. a key no receiver would accept never reaches the wire")
-- Per-entry keys were exported verbatim while the meta fields were guarded.
-- A ";" in a key from a hand-edited or badly generated Data/GuildData.lua
-- does not just lose that member: it splits the entry in two on the wire, so
-- the receiver counts one unparseable fragment AND one phantom member, and
-- past MAX_BAD_FRACTION it throws the whole snapshot away -- guild-wide,
-- with nothing on the exporting client to point at.
-- ==================================================================
reset()
local dirty = roster220(300)
dirty["Semi;colon-khadgar"] = 615
dirty["Equals=sign-khadgar"] = 616
local bad = newClient("Dirty", NEW, dirty, { seed = 161 })
login(bad); pump(1)

check("the unexportable keys still resolve locally (they are real data)",
      bad.ns.Data:GetByKey("Semi;colon-khadgar") == 615)
local badBody, badN = bad.ns.Data:Export()
check("EXPORT FILTER: neither key is on the wire",
      not badBody:find("Semi;colon", 1, true) and not badBody:find("Equals=sign", 1, true))
check("and the announced count matches the body that would be sent",
      bad.ns.Data:ShareableCount() == badN and badN == bad.ns.Data:Count() - 2,
      ("Shareable=%d body=%d local=%d"):format(
        bad.ns.Data:ShareableCount(), badN, bad.ns.Data:Count()))
check("the client was told once that some of its data cannot be shared",
      bad.saidHas("cannot be put on the wire"), table.concat(bad.said, " | "))

local peer3 = newClient("Clean", OLD, roster220(100), { seed = 162 })
login(peer3); pump(120)
check("and a peer adopts the rest of the roster without rejecting the snapshot",
      peer3.ns.Data:GeneratedEpoch() == NEW, tostring(peer3.ns.Data:GeneratedEpoch()))
check("with the bad keys simply absent, not mangled into phantom members",
      peer3.ns.Data:GetByKey("Semi;colon-khadgar") == nil
        and peer3.ns.Data:GetByKey("colon-khadgar") == nil)

-- ==================================================================
section("24. peer-supplied identity text must not reach the chat frame as markup")
-- parseBody captures each H: field as [^:;]*, which accepts "|" -- and the
-- rejection reason built from those fields is printed. A guildmate could
-- therefore put color escapes, or something dressed up as an item link, into
-- another player's chat frame. The send-side guard in Data.lua does not
-- cover this: what gets printed is what a PEER sent, not what we would send.
-- ==================================================================
reset()
local mark = newClient("Marked", OLD, roster220(100), { seed = 171 })
login(mark); pump(2)
mark.ns.db.debug = true          -- the rejection reason is a debug line
roster["Ghost"] = true
inject(mark, ("V:%d:300:1"):format(NEW), "GUILD", "Ghost")
pump(10)

local mnonce
for i = #log, 1, -1 do
  local n = log[i]:match("Marked %-> Ghost %[WHISPER%] Q:(%d+):")
  if n then mnonce = tonumber(n) break end
end
check("the victim did ask the ghost (test is not vacuous)", mnonce ~= nil)

-- A well-formed body for a DIFFERENT guild, whose name is markup.
local evil = { ("H:%d:us:khadgar:|cffff0000CLICK ME|r:3;"):format(NEW) }
for i = 1, 30 do evil[#evil + 1] = ("Evil%03d-khadgar=500;"):format(i) end
evil = table.concat(evil)
local evilPieces = {}
for j = 1, #evil, 200 do evilPieces[#evilPieces + 1] = evil:sub(j, j + 199) end
for i, piece in ipairs(evilPieces) do
  inject(mark, ("D:%d:%d:%d:%s"):format(mnonce, i, #evilPieces, piece), "WHISPER", "Ghost")
end

check("the wrong-guild snapshot was rejected (test is not vacuous)",
      mark.ns.Data:GeneratedEpoch() == OLD, tostring(mark.ns.Data:GeneratedEpoch()))
local printedRejection, printedMarkup = false, false
for i = 1, #mark.said do
  if mark.said[i]:find("CLICK ME", 1, true) then
    printedRejection = true
    if mark.said[i]:find("|cffff0000", 1, true) then printedMarkup = true end
  end
end
check("the reason really was printed (test is not vacuous)", printedRejection,
      table.concat(mark.said, " | "))
check("ESCAPED: the peer's color escape reached the chat frame as text, not markup",
      not printedMarkup, table.concat(mark.said, " | "))

-- ==================================================================
section("13. no client raised a Lua error")
-- ==================================================================
check("no runtime errors across every scenario", #errors == 0,
      table.concat(errors, " | "))

print(("\n== %d passed, %d failed ==\n"):format(pass, fail))
if fail > 0 then
  print("-- bus log (last scenario) --")
  for i = math.max(1, #log - 40), #log do print(log[i]) end
end
os.exit(fail == 0 and 0 or 1)
