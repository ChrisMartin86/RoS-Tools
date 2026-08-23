-- Riddled/Modules/Tooltip.lua
-- Injects the guild item level line into unit tooltips.
--
-- Retail (10.0.2+) routes every tooltip through TooltipDataProcessor,
-- which fires once per tooltip with the unit GUID already resolved.
-- That replaces the old OnShow / SetUnit hook soup, which fired on every
-- tooltip in the game (items, spells, action bar buttons) and forced a
-- lookup each time.

local _, ns = ...

local Tooltip = ns:RegisterModule("Tooltip")

local MARKER = "Riddled_ilvl_line"

local function alreadyStamped(tooltip)
  return tooltip[MARKER] == true
end

local function stamp(tooltip)
  tooltip[MARKER] = true
end

local function clearStamp(tooltip)
  tooltip[MARKER] = nil
end

-- ------------------------------------------------------------------
-- Line rendering
-- ------------------------------------------------------------------
local function buildValueText(ilvl)
  local text = ns.ColorForIlvl(ilvl) .. ilvl .. ns.COLOR.reset

  if ns.db.showDelta then
    local mine = ns.Data:PlayerIlvl()
    if mine then
      local delta = ilvl - mine
      if delta ~= 0 then
        local color = delta > 0 and "|cffff6666" or "|cff66ff66"
        text = ("%s %s%+d%s"):format(text, color, delta, ns.COLOR.reset)
      end
    end
  end

  return text
end

local function addLines(tooltip, ilvl)
  tooltip:AddLine(" ")
  tooltip:AddDoubleLine(
    ns.COLOR.brand .. "Guild Item Level" .. ns.COLOR.reset,
    buildValueText(ilvl)
  )

  local generated = ns.Data:GeneratedAt()
  if generated and ns.db.showTimestamp then
    local line = ns.COLOR.dim .. generated .. ns.COLOR.reset
    if ns.db.showStaleWarn and ns.Data:IsStale() then
      local days = ns.Data:AgeInDays()
      line = line .. ("  %s(%dd old)%s"):format(ns.COLOR.warn, days, ns.COLOR.reset)
    end
    tooltip:AddLine(line, 1, 1, 1, true)
  end

  stamp(tooltip)
  tooltip:Show()
end

-- ------------------------------------------------------------------
-- Handlers
-- ------------------------------------------------------------------
local function shouldSkip()
  if not ns.db.enabled then return true end
  if ns.db.suppressInCombat and ns.inCombat then return true end
  return false
end

local function onUnitTooltip(tooltip, data)
  if tooltip ~= GameTooltip then return end
  if shouldSkip() then return end
  if alreadyStamped(tooltip) then return end

  local ilvl, key

  if data and data.guid then
    ilvl, key = ns.Data:GetForGUID(data.guid)
  end

  if not ilvl then
    local _, unit = tooltip:GetUnit()
    if unit then ilvl, key = ns.Data:GetForUnit(unit) end
  end

  -- Last resort: parse the header. Covers inspect/hyperlink cases where
  -- no unit token exists.
  if not ilvl then
    local left = _G["GameTooltipTextLeft1"]
    local text = left and left:GetText()
    if text then
      key  = ns.Util.NormalizeKey(text, ns.playerRealmSlug)
      ilvl = key and ns.Data:GetByKey(key)
    end
  end

  if not ilvl then
    if key then ns.Debug("no entry for", key) end
    return
  end

  addLines(tooltip, ilvl)
end

-- ------------------------------------------------------------------
-- Legacy path (Classic / anything without TooltipDataProcessor)
-- ------------------------------------------------------------------
local function installLegacyHooks()
  local function handler()
    if shouldSkip() then return end
    if not GameTooltip:IsShown() then return end
    if alreadyStamped(GameTooltip) then return end

    local _, unit = GameTooltip:GetUnit()
    if not unit then return end

    local ilvl = ns.Data:GetForUnit(unit)
    if ilvl then addLines(GameTooltip, ilvl) end
  end

  hooksecurefunc(GameTooltip, "SetUnit", handler)
  GameTooltip:HookScript("OnShow", handler)
end

-- ------------------------------------------------------------------
-- Lifecycle
-- ------------------------------------------------------------------
function Tooltip:OnEnable()
  -- Reset the stamp whenever the tooltip is recycled, on both paths.
  GameTooltip:HookScript("OnHide", function(tt) clearStamp(tt) end)
  if GameTooltip.HookScript and GameTooltip:HasScript("OnTooltipCleared") then
    GameTooltip:HookScript("OnTooltipCleared", function(tt) clearStamp(tt) end)
  end

  if TooltipDataProcessor and TooltipDataProcessor.AddTooltipPostCall
     and Enum and Enum.TooltipDataType and Enum.TooltipDataType.Unit then
    TooltipDataProcessor.AddTooltipPostCall(Enum.TooltipDataType.Unit, onUnitTooltip)
    ns.Debug("tooltip: using TooltipDataProcessor")
  else
    installLegacyHooks()
    ns.Debug("tooltip: using legacy hooks")
  end
end
