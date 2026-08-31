-- RoS-Tools/Core/Events.lua
-- Single event frame. Modules hook in via the registry in Init.lua.
-- Core/Comm.lua and Core/Sync.lua are wired in directly here (not via the
-- module registry) -- see the comment at the top of Comm.lua for why.

local ADDON_NAME, ns = ...

ns.inCombat = false

local frame = CreateFrame("Frame", "RoSToolsEventFrame")
ns.frame = frame

frame:RegisterEvent("ADDON_LOADED")
frame:RegisterEvent("PLAYER_LOGIN")
frame:RegisterEvent("PLAYER_REGEN_DISABLED")
frame:RegisterEvent("PLAYER_REGEN_ENABLED")

--- Calls a Comm.lua entry point the same way Init.lua's module dispatch()
--- calls module methods: pcall-wrapped, so a bug in Comm.lua degrades to
--- "no live updates" instead of breaking the rest of the event handler.
local function call(what, method, ...)
  local target = ns[what]
  local fn = target and target[method]
  if type(fn) ~= "function" then return end
  local ok, err = pcall(fn, target, ...)
  if not ok then
    ns.Error(("%s:%s() failed: %s"):format(what, method, tostring(err)))
  end
end

local function callComm(method, ...) call("Comm", method, ...) end
local function callSync(method, ...) call("Sync", method, ...) end

frame:SetScript("OnEvent", function(_, event, ...)
  if event == "ADDON_LOADED" then
    local arg1 = ...
    if arg1 ~= ADDON_NAME then return end
    ns.LoadConfig()
    ns.Data:Build()
    callComm("OnInitialize")
    callSync("OnInitialize")
    ns:InitializeModules()
    frame:UnregisterEvent("ADDON_LOADED")

  elseif event == "PLAYER_LOGIN" then
    ns.playerRealmSlug = ns.Util.RealmToSlug(GetRealmName() or "")
    ns.playerName      = UnitName("player")
    callComm("OnEnable")
    callSync("OnEnable")
    ns:EnableModules()

    local count = ns.Data:Count()
    local age   = ns.Data:AgeInDays()
    local suffix = ""
    if age then
      suffix = (", exported %d day%s ago"):format(age, age == 1 and "" or "s")
      if ns.Data:IsStale() then
        suffix = suffix .. " " .. ns.Colorize("warn", "(stale)")
      end
    end
    ns.Print(("loaded -- %s entries%s. %s"):format(
      ns.Colorize("value", count), suffix, ns.Colorize("dim", "/ros for help")))

  elseif event == "PLAYER_REGEN_DISABLED" then
    ns.inCombat = true

  elseif event == "PLAYER_REGEN_ENABLED" then
    ns.inCombat = false

  elseif event == "CHAT_MSG_ADDON" then
    -- Both handlers see every message; each ignores prefixes that aren't
    -- its own. Comm.lua owns RoSTools1, Sync.lua owns RoSToolsD1.
    callComm("HandleAddonMessage", ...)
    callSync("HandleAddonMessage", ...)

  elseif event == "PLAYER_EQUIPMENT_CHANGED" then
    callComm("HandleEquipmentChanged")
  end
end)
