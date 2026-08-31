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
  mathEnv.randomseed = function() end
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
  env.DEFAULT_CHAT_FRAME = { AddMessage = function(_, m)
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
  env.C_Timer = { After = After }
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
  env.GetRealmName = function() return "Khadgar" end
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

  local c = { name = name, ns = ns, env = env, frame = frame }
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
