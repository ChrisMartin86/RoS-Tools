-- Adversarial check: does Events.lua now SAY something when a Core file
-- fails to compile, and does it say it exactly once?
local errors, prints = {}, {}
local frame = { scripts = {}, events = {} }
function frame:RegisterEvent(e) self.events[e] = true end
function frame:UnregisterEvent(e) self.events[e] = nil end
function frame:SetScript(k, fn) self.scripts[k] = fn end

local ns = {
  Error = function(msg) errors[#errors+1] = msg end,
  Print = function(...) prints[#prints+1] = table.concat({...}, " ") end,
  Debug = function(msg) prints[#prints+1] = "[debug] " .. tostring(msg) end,
  Colorize = function(_, v) return tostring(v) end,
  LoadConfig = function() end,
  Util = { RealmToSlug = function() return "khadgar" end },
  Data = { Build = function() end, Count = function() return 3 end,
           AgeInDays = function() return 1 end, IsStale = function() return false end },
  InitializeModules = function() end,
  EnableModules = function() end,
  ModuleList = function() return "Tooltip, Roster" end,
}
-- Sync loads fine; Comm is the casualty of a syntax error, exactly as it was.
ns.Sync = { OnInitialize = function() end, OnEnable = function() end,
            HandleAddonMessage = function() end }
ns.Comm = nil

local env = setmetatable({
  CreateFrame = function() return frame end,
  UnitName = function() return "Chris" end,
  GetRealmName = function() return "Khadgar" end,
}, { __index = _G })

local chunk = assert(loadfile("Core/Events.lua"))
setfenv(chunk, env)
chunk("RoS-Tools", ns)

local onEvent = frame.scripts.OnEvent
onEvent(frame, "ADDON_LOADED", "RoS-Tools")
onEvent(frame, "PLAYER_LOGIN")
for _ = 1, 50 do onEvent(frame, "CHAT_MSG_ADDON", "RoSTools1", "600", "GUILD", "Someone") end
onEvent(frame, "PLAYER_EQUIPMENT_CHANGED")

local pass, fail = 0, 0
local function check(label, cond)
  if cond then pass = pass + 1; print("  PASS  " .. label)
  else fail = fail + 1; print("  FAIL  " .. label) end
end

print("== Events.lua names a Core file that failed to load ==")
local named = 0
for _, e in ipairs(errors) do if e:match("Core/Comm%.lua failed to load") then named = named + 1 end end
check("the missing file is reported", named > 0)
check("reported exactly once across 53 dispatches, not per-call", named == 1)
check("no error invented for Sync, which loaded fine", not table.concat(errors, "|"):match("Sync%.lua failed"))
check("the rest of PLAYER_LOGIN still ran", table.concat(prints, "|"):match("loaded %-%-") ~= nil)
check("the module list reaches debug output", table.concat(prints, "|"):match("modules loaded: Tooltip, Roster") ~= nil)

print(("\n== %d passed, %d failed =="):format(pass, fail))
os.exit(fail == 0 and 0 or 1)
