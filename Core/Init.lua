-- RoS-Tools/Core/Init.lua
-- Namespace bootstrap, module registry, logging helpers.

local ADDON_NAME, ns = ...

ns.ADDON_NAME = ADDON_NAME
ns.VERSION    = C_AddOns and C_AddOns.GetAddOnMetadata
                and C_AddOns.GetAddOnMetadata(ADDON_NAME, "Version")
                or "dev"

-- ------------------------------------------------------------------
-- Colors
-- ------------------------------------------------------------------
ns.COLOR = {
  brand  = "|cff00ff88",
  value  = "|cffffff00",
  dim    = "|cffaaaaaa",
  warn   = "|cffff8800",
  error  = "|cffff4444",
  reset  = "|r",
}

local COLOR = ns.COLOR

function ns.Colorize(color, text)
  return (COLOR[color] or "") .. tostring(text) .. COLOR.reset
end

-- ------------------------------------------------------------------
-- Logging
-- ------------------------------------------------------------------
local PREFIX = COLOR.brand .. "RoS-Tools" .. COLOR.reset .. ": "

function ns.Print(...)
  local parts = {}
  for i = 1, select("#", ...) do
    parts[i] = tostring((select(i, ...)))
  end
  DEFAULT_CHAT_FRAME:AddMessage(PREFIX .. table.concat(parts, " "))
end

function ns.Warn(msg)
  ns.Print(COLOR.warn .. tostring(msg) .. COLOR.reset)
end

function ns.Error(msg)
  ns.Print(COLOR.error .. tostring(msg) .. COLOR.reset)
end

function ns.Debug(...)
  if not (ns.db and ns.db.debug) then return end
  ns.Print(COLOR.dim .. "[debug]" .. COLOR.reset, ...)
end

-- ------------------------------------------------------------------
-- Module registry
--
-- Modules are plain tables with optional OnInitialize / OnEnable
-- methods. OnInitialize runs at ADDON_LOADED (SavedVariables ready),
-- OnEnable runs at PLAYER_LOGIN (game APIs ready).
-- ------------------------------------------------------------------
ns.modules = {}
local moduleOrder = {}

function ns:RegisterModule(name, module)
  module = module or {}
  module.name = name
  self.modules[name] = module
  moduleOrder[#moduleOrder + 1] = name
  return module
end

function ns:GetModule(name)
  return self.modules[name]
end

local function dispatch(method)
  for i = 1, #moduleOrder do
    local module = ns.modules[moduleOrder[i]]
    if module and type(module[method]) == "function" then
      local ok, err = pcall(module[method], module)
      if not ok then
        ns.Error(("module %s:%s() failed: %s"):format(module.name, method, tostring(err)))
      end
    end
  end
end

function ns:InitializeModules() dispatch("OnInitialize") end
function ns:EnableModules()     dispatch("OnEnable")     end
