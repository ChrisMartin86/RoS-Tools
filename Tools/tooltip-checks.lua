-- Offline harness for Modules/Tooltip.lua -- the unit-tooltip path that
-- Tools/module-checks.lua does not load.
--
-- Every scenario here is here because it once failed in game. The 12.0
-- "secret values" ones reproduce a Mt. Hyjal raid crash: TooltipDataProcessor
-- handed us `data.guid` as a secret string and `guid:find("^Player%-")`
-- errored 639 times in one pull.
--
-- A real secret cannot be built in plain Lua, so there are two stand-ins and
-- a matching `issecretvalue`: a table that raises on ANY access (stricter
-- than the client -- touching it at all fails the run), and a plain string
-- flagged secret, which is what proves a guard rather than a `type()` check
-- is doing the rejecting. See markSecret / newSecret below.
--
-- Run from the repo root:   lua5.1 Tools/tooltip-checks.lua
-- Exits non-zero on any failure.

local ROOT = os.getenv("ROSTOOLS_ROOT") or "./"

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

local function plain(text)
  return (tostring(text or ""):gsub("|c%x%x%x%x%x%x%x%x", ""):gsub("|r", ""))
end

-- ---------- secret values ----------
local SECRETS = setmetatable({}, { __mode = "k" })

--- A secret that is a REAL Lua string. In game `type(secret) == "string"`, so
--- a pre-existing `type(x) ~= "string"` check waves one through -- and a table
--- stand-in would be stopped by that check instead of by the guard, making the
--- assertion pass for the wrong reason.
local function markSecret(str)
  SECRETS[str] = true
  return str
end

--- A stand-in for a 12.0 secret string: touching it in any way that the
--- client forbids (index, call, length, concat) blows up here too. The right
--- fixture wherever the point is that the code never touches it at all.
local function newSecret(what)
  local boom = function() error("touched a secret value (" .. what .. ")", 2) end
  local s = setmetatable({}, {
    __index = boom, __newindex = boom, __call = boom,
    __len = boom, __concat = boom, __tostring = boom,
  })
  SECRETS[s] = true
  return s
end

-- ---------- widget stubs ----------
local NOOP = function() end

local function newFontString(text)
  local fs = { text = text }
  function fs:GetText() return self.text end
  function fs:SetText(t) self.text = t end
  setmetatable(fs, { __index = function() return NOOP end })
  return fs
end

--- GameTooltip. Records the lines added, which is the only observable the
--- assertions need, plus the unit token GetUnit() should hand back.
local function newTooltip()
  local tt = { lines = {}, scripts = {}, shown = false }
  function tt:AddLine(t) self.lines[#self.lines + 1] = plain(t) end
  function tt:AddDoubleLine(l, r)
    self.lines[#self.lines + 1] = plain(l) .. " | " .. plain(r)
  end
  function tt:ClearLines() self.lines = {} end
  function tt:Show() self.shown = true end
  function tt:Hide() self.shown = false end
  function tt:IsShown() return self.shown end
  function tt:GetUnit() return self.unitName, self.unit end
  function tt:HasScript() return true end
  function tt:HookScript(k, fn) self.scripts[k] = fn end
  function tt:SetScript(k, fn) self.scripts[k] = fn end
  --- Everything the ilvl block rendered, joined.
  function tt:text() return table.concat(self.lines, "\n") end
  setmetatable(tt, { __index = function() return NOOP end })
  return tt
end

-- ---------- addon environment ----------
local FILES = { "Core/Init.lua", "Core/Util.lua", "Core/Config.lua",
                "Core/Data.lua", "Modules/Tooltip.lua" }

--- Stand up one addon instance with Tooltip enabled. Returns the namespace,
--- the GameTooltip stub, and the callback TooltipDataProcessor captured --
--- which is the entry point every unit-tooltip scenario drives.
local function newAddon(ilvls, opts)
  opts = opts or {}
  local env = {}
  for k, v in pairs(_G) do env[k] = v end
  local ns = {}
  local said = {}
  local tooltip = newTooltip()
  local captured

  env._G = env
  env.RoSToolsDB = {}
  env.time, env.date = os.time, os.date
  env.wipe = function(t) for k in pairs(t) do t[k] = nil end return t end
  env.tinsert = table.insert
  env.C_AddOns = { GetAddOnMetadata = function() return "test" end }
  env.DEFAULT_CHAT_FRAME = { AddMessage = function(_, m) said[#said + 1] = m end }
  env.GetAverageItemLevel = function() return 300, 295 end
  env.GetRealmName = function() return "Khadgar" end
  env.CreateFrame = function() return newTooltip() end
  env.UIParent = NOOP
  env.GameTooltip = tooltip
  env.GameTooltipTextLeft1 = newFontString(nil)
  env.hooksecurefunc = function() end

  -- The 12.0 detector. `opts.noSecretsAPI` models a pre-12.0 client, where
  -- the global is absent and nothing may be assumed secret.
  if not opts.noSecretsAPI then
    env.issecretvalue = function(v) return SECRETS[v] == true end
  else
    env.issecretvalue = nil
  end

  -- Unit-token side. A secret GUID must still resolve through here.
  --
  -- 12.0 rejects a secret for the `unit` argument outright -- "Secret values
  -- are only allowed during untainted execution for this argument" -- which is
  -- what a whole instance run of UnitIsPlayer errors turned out to be. The
  -- stubs raise the same way so a missing guard fails the run.
  local function noSecretUnit(fn)
    return function(u, ...)
      if SECRETS[u] then error("secret unit token passed to a Unit* API", 2) end
      return fn(u, ...)
    end
  end
  env.UnitExists = noSecretUnit(function(u) return opts.unitExists ~= false and u ~= nil end)
  env.UnitIsPlayer = noSecretUnit(function() return opts.unitIsPlayer ~= false end)
  env.UnitName = noSecretUnit(function() return opts.unitName or "Tester", opts.unitRealm end)
  env.GetPlayerInfoByGUID = function(guid)
    if SECRETS[guid] then error("GetPlayerInfoByGUID saw a secret", 2) end
    if type(guid) == "string" and guid:find("^Player%-") then
      return nil, nil, nil, nil, nil, "Peidae", "Khadgar"
    end
    return nil
  end

  env.Enum = { TooltipDataType = { Unit = 2 } }
  env.TooltipDataProcessor = {
    AddTooltipPostCall = function(_, fn) captured = fn end,
  }

  local guildData = {
    meta = { generated_epoch = os.time() - 600, generated_at = "x", region = "us",
             realm = "khadgar", guild = "riddle-of-steel", schema = 3 },
    ilvls = ilvls,
  }

  for _, f in ipairs(FILES) do
    local chunk = assert(loadfile(ROOT .. f))
    setfenv(chunk, env)
    if f == "Core/Data.lua" then ns.GuildData = guildData end
    chunk("RoS-Tools", ns)
  end

  ns.LoadConfig()
  ns.db.debug = opts.debug or false
  ns.playerName = "Tester"
  ns.playerRealmSlug = "khadgar"
  ns.Data:Build()
  ns:EnableModules()

  return {
    ns = ns, env = env, tooltip = tooltip, onUnitTooltip = captured,
    header = function(text) env.GameTooltipTextLeft1:SetText(text) end,
    --- Fresh tooltip between scenarios: the ilvl block stamps the frame and
    --- a stamped frame is skipped, so reusing one hides every later miss.
    reset = function()
      tooltip.lines = {}
      tooltip.RoSTools_ilvl_line = nil
      tooltip.unit, tooltip.unitName = nil, nil
      env.GameTooltipTextLeft1:SetText(nil)
    end,
    saidHas = function(needle)
      for i = 1, #said do
        if tostring(said[i]):find(needle, 1, true) then return true end
      end
      return false
    end,
  }
end

local KEY = "Peidae-khadgar"

-- ==================================================================
section("1. the processor callback registers")
-- Nothing below tests anything if it did not, so assert it first.
-- ==================================================================
local a = newAddon({ [KEY] = 620 })
check("TooltipDataProcessor captured our callback", type(a.onUnitTooltip) == "function",
      tostring(a.onUnitTooltip))

-- ==================================================================
section("2. baseline: a plain GUID still stamps the tooltip")
-- ==================================================================
a.reset()
a.onUnitTooltip(a.tooltip, { guid = "Player-1-DEADBEEF" })
check("ilvl line added from a readable GUID", a.tooltip:text():find("620", 1, true) ~= nil,
      a.tooltip:text())

-- ==================================================================
section("3. secret GUID -- the Mt. Hyjal crash")
-- The bug: `guid:find(\"^Player%-\")` indexed a secret string and errored
-- once per tooltip refresh. The fix must not touch the value at all.
-- ==================================================================
a.reset()
local secretGuid = newSecret("data.guid")
local ok, err = pcall(a.onUnitTooltip, a.tooltip, { guid = secretGuid })
check("a secret GUID does not error", ok, tostring(err))

-- Bailing quietly would be a regression of its own: the raid still wants the
-- line, and the unit token behind the tooltip is not secret.
local u = newAddon({ [KEY] = 620 }, { unitName = "Peidae" })
u.tooltip.unit = "mouseover"
local okFallback, fallbackErr = pcall(u.onUnitTooltip, u.tooltip, { guid = newSecret("data.guid") })
check("falls through to the unit token instead of bailing", okFallback, tostring(fallbackErr))
check("unit-token fallback still stamps the ilvl",
      u.tooltip:text():find("620", 1, true) ~= nil, u.tooltip:text())

-- ==================================================================
section("4. secret tooltip header")
-- FontString:GetText() hands back whatever was set on it, secrets included,
-- and NormalizeKey indexes its argument.
-- ==================================================================
a.reset()
a.header(newSecret("TextLeft1"))
local okHeader, headerErr = pcall(a.onUnitTooltip, a.tooltip, { guid = newSecret("data.guid") })
check("a secret header does not error", okHeader, tostring(headerErr))
check("nothing was stamped from a secret header", a.tooltip:text() == "", a.tooltip:text())

-- ==================================================================
section("5. Data:GetForGUID is guarded on its own")
-- Other callers reach it directly; the guard must not live only in Tooltip.
-- The fixture is a resolvable GUID marked secret, and the stubbed
-- GetPlayerInfoByGUID errors if it is ever handed one -- so this fails unless
-- the IsSecret guard (not the type check, which a secret passes) stops it.
-- ==================================================================
local secretButValid = markSecret("Player-1-CAFEBABE")
local okData, dataErr = pcall(function() return a.ns.Data:GetForGUID(secretButValid) end)
check("GetForGUID never passes a secret to GetPlayerInfoByGUID", okData, tostring(dataErr))
check("GetForGUID returns nil for a secret", okData and a.ns.Data:GetForGUID(secretButValid) == nil)

-- ==================================================================
section("6. pre-12.0 clients, where issecretvalue does not exist")
-- The guard must degrade to \"nothing is secret\", not to \"everything is\".
-- ==================================================================
local b = newAddon({ [KEY] = 620 }, { noSecretsAPI = true })
check("IsSecret is false without the API", b.ns.Util.IsSecret("Player-1-DEADBEEF") == false)
b.reset()
b.onUnitTooltip(b.tooltip, { guid = "Player-1-DEADBEEF" })
check("GUID path still works on an old client",
      b.tooltip:text():find("620", 1, true) ~= nil, b.tooltip:text())

-- ==================================================================
section("7. non-player GUIDs are still rejected early")
-- The cheap bail that keeps NPC hovers from manufacturing a key.
-- ==================================================================
local c = newAddon({ ["Resident-khadgar"] = 999 })
c.reset()
c.tooltip.unit = nil
c.header("Auction House Resident")
c.onUnitTooltip(c.tooltip, { guid = "Creature-0-1234" })
check("an NPC GUID stamps nothing", c.tooltip:text() == "", c.tooltip:text())

-- ==================================================================
section("8. secret unit tokens")
-- The instance case: data.guid is secret, so the GUID path bails and the unit
-- path runs -- but GetUnit() hands back a secret token too, and UnitIsPlayer
-- errors on it before it can filter anything. 109 of these in one run.
-- ==================================================================
-- markSecret keys on the value, and Lua interns strings -- so marking
-- "mouseover" here would make that token secret for the rest of the run and
-- quietly break the plain-token check below. Each secret token gets its own
-- literal.
local d = newAddon({ [KEY] = 620 })
d.reset()
d.tooltip.unit = markSecret("secret-unit-token")
d.header("Some Instance Trash")
local okUnit, unitErr = pcall(d.onUnitTooltip, d.tooltip, { guid = newSecret("data.guid") })
check("a secret unit token does not error", okUnit, tostring(unitErr))
check("nothing was stamped from a secret unit token", d.tooltip:text() == "", d.tooltip:text())

-- Direct callers (the Classic/legacy hook) reach GetForUnit without going
-- through Tooltip, so the guard must live there as well.
local okGFU, gfuErr = pcall(function() return d.ns.Data:GetForUnit(markSecret("secret-target-token")) end)
check("GetForUnit never passes a secret to a Unit* API", okGFU, tostring(gfuErr))
check("GetForUnit returns nil for a secret token",
      okGFU and d.ns.Data:GetForUnit(markSecret("secret-target-token")) == nil)

-- A normal token still works.
local e = newAddon({ [KEY] = 480 }, { unitName = "Peidae" })
e.reset()
e.tooltip.unit = "mouseover"
e.onUnitTooltip(e.tooltip, { guid = newSecret("data.guid") })
check("a plain unit token still stamps",
      e.tooltip:text():find("480", 1, true) ~= nil, e.tooltip:text())

-- ==================================================================
print(("\n%d passed, %d failed"):format(pass, fail))
if fail > 0 then os.exit(1) end
