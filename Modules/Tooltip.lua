-- Riddled/Modules/Tooltip.lua
-- Injects the guild item level line into unit tooltips, and builds one
-- from scratch when you hover a player name in chat.
--
-- Retail (10.0.2+) routes every *unit* tooltip through TooltipDataProcessor,
-- which fires once per tooltip with the unit GUID already resolved.
-- That replaces the old OnShow / SetUnit hook soup, which fired on every
-- tooltip in the game (items, spells, action bar buttons) and forced a
-- lookup each time.
--
-- Chat player links are a different animal: Blizzard's
-- HYPERLINKS_WITH_TOOLTIPS deliberately excludes "player", so no tooltip
-- exists to append to and we own the whole thing. See the chat section.

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

--- Append the "Guild Item Level" block to any tooltip and stamp it so a
--- second pass can't duplicate it. Shared with Modules/Roster.lua, which
--- decorates the Communities hover tooltip.
--- @param spacer boolean pass true when appending to someone else's
---        tooltip, false when we built the tooltip ourselves.
local function addLines(tooltip, ilvl, spacer)
  if alreadyStamped(tooltip) then return end

  if spacer then tooltip:AddLine(" ") end

  tooltip:AddDoubleLine(
    ns.COLOR.brand .. "Guild Item Level" .. ns.COLOR.reset,
    ns.IlvlText(ilvl)
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

ns.AddIlvlLines = addLines

-- ------------------------------------------------------------------
-- Handlers
-- ------------------------------------------------------------------
local function shouldSkip()
  if not ns.db or not ns.db.enabled then return true end
  if ns.db.suppressInCombat and ns.inCombat then return true end
  return false
end

ns.TooltipSuppressed = shouldSkip

-- The last key we logged as missing, so hovering the same stranger for
-- three seconds doesn't produce three hundred identical debug lines.
local lastMissKey

local function logMiss(prefix, key)
  if not key or key == lastMissKey then return end
  lastMissKey = key
  ns.Debug(prefix, key)
end

local function onUnitTooltip(tooltip, data)
  if tooltip ~= GameTooltip then return end
  if shouldSkip() then return end
  if alreadyStamped(tooltip) then return end

  local ilvl, key

  -- Bail on anything that isn't a player before doing any work. This hook
  -- fires for every unit in the game -- NPCs, pets, totems, vehicles -- and
  -- the header fallback below will happily manufacture a key out of the last
  -- word of an NPC's name ("Auction House Resident" -> "Resident-<realm>"),
  -- burning a lookup and a debug line on something that can never match.
  local guid = data and data.guid
  if guid then
    if not guid:find("^Player%-") then return end
    ilvl, key = ns.Data:GetForGUID(guid)
  end

  if not ilvl then
    local _, unit = tooltip:GetUnit()
    if unit then
      if not UnitIsPlayer(unit) then return end
      ilvl, key = ns.Data:GetForUnit(unit)
    end
  end

  -- Last resort: parse the header. Covers inspect/hyperlink cases where
  -- neither a GUID nor a unit token exists.
  if not ilvl and not key then
    local left = _G["GameTooltipTextLeft1"]
    local text = left and left:GetText()
    if text then
      key  = ns.Util.NormalizeKey(text, ns.playerRealmSlug)
      ilvl = key and ns.Data:GetByKey(key)
    end
  end

  if not ilvl then
    logMiss("no entry for", key)
    return
  end

  addLines(tooltip, ilvl, true)
end

-- ------------------------------------------------------------------
-- Chat player links
--
-- A name in guild chat is a |Hplayer:Name-Realm:...| hyperlink. Blizzard
-- shows no tooltip for those -- ChatFrame_OnHyperlinkEnter only builds one
-- for the link types in HYPERLINKS_WITH_TOOLTIPS, and "player" isn't one --
-- so GameTooltip is free and we build the whole tooltip, header included.
--
-- The frames' own scripts are hooked rather than the global
-- ChatFrame_OnHyperlinkEnter, because chat replacement addons (Prat,
-- Chatter, ElvUI) reuse the frames but not always the global.
-- ------------------------------------------------------------------
local MAX_CHAT_FRAMES = 50

local chatTooltipOwned = false

--- Pull "Name-Realm" out of a player hyperlink. Returns nil for every
--- other link type, including BNplayer -- a Battle.net link identifies an
--- account, not a character, so there's nothing to look up.
local function playerFromLink(link)
  if type(link) ~= "string" then return nil end
  local linkType, rest = link:match("^([^:]+):(.+)$")
  if linkType ~= "player" then return nil end
  local name = rest:match("^([^:]+)")
  if not name or name == "" then return nil end
  return name
end

local function onChatLinkEnter(chatFrame, link)
  if not ns.db or not ns.db.chatTooltip then return end
  if shouldSkip() then return end

  local name = playerFromLink(link)
  if not name then return end

  local key  = ns.Util.NormalizeKey(name, ns.playerRealmSlug)
  local ilvl = key and ns.Data:GetByKey(key)
  if not ilvl then
    logMiss("chat: no entry for", key)
    return
  end

  -- If something else already claimed the tooltip for this link, append to
  -- it rather than blowing its contents away.
  if GameTooltip:IsShown() and GameTooltip:GetOwner() == chatFrame then
    addLines(GameTooltip, ilvl, true)
    return
  end

  local short, realm = name:match("^([^%-]+)%-(.+)$")
  local header = short
    and ("%s %s-%s%s"):format(short, ns.COLOR.dim, realm, ns.COLOR.reset)
    or name

  GameTooltip:SetOwner(chatFrame, "ANCHOR_CURSOR")
  GameTooltip:ClearLines()
  clearStamp(GameTooltip)
  GameTooltip:AddLine(header, 1, 1, 1)
  addLines(GameTooltip, ilvl, false)
  chatTooltipOwned = true
end

local function onChatLinkLeave()
  if not chatTooltipOwned then return end
  chatTooltipOwned = false
  GameTooltip:Hide()
end

local function hookChatFrames()
  for i = 1, MAX_CHAT_FRAMES do
    local cf = _G["ChatFrame" .. i]
    if cf and cf.HookScript and not cf.riddledChatHooked then
      cf.riddledChatHooked = true
      cf:HookScript("OnHyperlinkEnter", onChatLinkEnter)
      cf:HookScript("OnHyperlinkLeave", onChatLinkLeave)
    end
  end
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
    if ilvl then addLines(GameTooltip, ilvl, true) end
  end

  hooksecurefunc(GameTooltip, "SetUnit", handler)
  GameTooltip:HookScript("OnShow", handler)
end

-- ------------------------------------------------------------------
-- Lifecycle
-- ------------------------------------------------------------------
function Tooltip:OnEnable()
  -- Reset the stamp whenever the tooltip is recycled, on both paths.
  GameTooltip:HookScript("OnHide", function(tt)
    clearStamp(tt)
    chatTooltipOwned = false
  end)
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

  hookChatFrames()

  -- Whisper/temporary tabs are created on demand and reuse frame indices
  -- above NUM_CHAT_WINDOWS, so rescan whenever one opens.
  if type(_G.FCF_OpenTemporaryWindow) == "function" then
    hooksecurefunc("FCF_OpenTemporaryWindow", hookChatFrames)
  end
end
