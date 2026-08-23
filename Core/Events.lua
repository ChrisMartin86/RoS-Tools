-- Riddled/Core/Events.lua
-- Single event frame. Modules hook in via the registry in Init.lua.

local ADDON_NAME, ns = ...

ns.inCombat = false

local frame = CreateFrame("Frame", "RiddledEventFrame")
ns.frame = frame

frame:RegisterEvent("ADDON_LOADED")
frame:RegisterEvent("PLAYER_LOGIN")
frame:RegisterEvent("PLAYER_REGEN_DISABLED")
frame:RegisterEvent("PLAYER_REGEN_ENABLED")

frame:SetScript("OnEvent", function(_, event, arg1)
  if event == "ADDON_LOADED" then
    if arg1 ~= ADDON_NAME then return end
    ns.LoadConfig()
    ns.Data:Build()
    ns:InitializeModules()
    frame:UnregisterEvent("ADDON_LOADED")

  elseif event == "PLAYER_LOGIN" then
    ns.playerRealmSlug = ns.Util.RealmToSlug(GetRealmName() or "")
    ns.playerName      = UnitName("player")
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
      ns.Colorize("value", count), suffix, ns.Colorize("dim", "/riddle for help")))

  elseif event == "PLAYER_REGEN_DISABLED" then
    ns.inCombat = true

  elseif event == "PLAYER_REGEN_ENABLED" then
    ns.inCombat = false
  end
end)
