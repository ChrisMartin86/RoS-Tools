-- Riddled/Modules/Roster.lua
-- Appends the item level to names in the Guild & Communities roster.
--
-- Blizzard rearranges the Communities frame fairly often and the entry
-- widget layout is not a stable API, so this module never assumes a
-- specific child name. It walks the entry's font strings, finds the one
-- currently showing the member's name, and appends to it. If the shape
-- changes out from under us, it degrades to a no-op instead of erroring.

local _, ns = ...

local Roster = ns:RegisterModule("Roster")

local SUFFIX_PATTERN = " |cff%x%x%x%x%x%x%x%x%(%d+%)|r$"

local function stripSuffix(text)
  if not text then return text end
  return (text:gsub(SUFFIX_PATTERN, ""))
end

--- Strip any Blizzard color escapes, not just the ones this addon adds.
--- The realm suffix on a cross-realm name is commonly dimmed with its own
--- |cff.../|r wrapper, which would otherwise break an exact-text match.
local function stripColors(text)
  if not text then return text end
  return (text:gsub("|c%x%x%x%x%x%x%x%x", ""):gsub("|r", ""))
end

--- Find the FontString in `frame` whose text is the member's name. Tries an
--- exact match against the full "Name-Realm" first, then against just the
--- short name -- the realm suffix is often its own separate FontString
--- (or its own colored segment) rather than part of the name string, so a
--- full-string match alone misses every cross-realm member.
local function findNameFontString(frame, fullName, shortName)
  if not frame or not frame.GetRegions then return nil end
  local regions = { frame:GetRegions() }
  for i = 1, #regions do
    local region = regions[i]
    if region and region.GetObjectType and region:GetObjectType() == "FontString" then
      local text = stripColors(stripSuffix(region:GetText()))
      if text and text ~= "" and (text == fullName or text == shortName) then
        return region
      end
    end
  end

  -- One level down: the name often lives inside a NameFrame child.
  if frame.GetChildren then
    local children = { frame:GetChildren() }
    for i = 1, #children do
      local found = findNameFontString(children[i], fullName, shortName)
      if found then return found end
    end
  end

  return nil
end

local function memberKey(memberInfo)
  local name = memberInfo and memberInfo.name
  if type(name) ~= "string" or name == "" then return nil, nil end
  local shortName = name:match("^([^%-]+)") or name
  return ns.Util.NormalizeKey(name, ns.playerRealmSlug), name, shortName
end

local firedOnce = false

local function annotate(entry)
  if not firedOnce then
    firedOnce = true
    ns.Debug("roster: UpdateNameFrame hook is firing")
  end

  if not ns.db or not ns.db.rosterColumn then
    ns.Debug("roster: rosterColumn disabled")
    return
  end

  local info = entry and entry.memberInfo
  if not info then
    ns.Debug("roster: entry has no memberInfo")
    return
  end

  local key, displayName, shortName = memberKey(info)
  if not key then
    ns.Debug("roster: memberInfo.name missing/blank")
    return
  end

  local ilvl = ns.Data:GetByKey(key)
  if not ilvl then
    ns.Debug("roster: no data entry for", key)
    return
  end

  local fs = findNameFontString(entry, displayName, shortName)
  if not fs then
    ns.Debug("roster: no name fontstring for", displayName)
    return
  end

  local base = stripSuffix(fs:GetText())
  if not base or base == "" then return end

  local color = ns.ColorForIlvl(ilvl)
  fs:SetText(("%s %s(%d)%s"):format(base, color, ilvl, ns.COLOR.reset))
end

local hooked = false

--- verbose: log *why* the hook didn't attach. Only pass true once
--- Blizzard_Communities has actually loaded, so we don't spam before then.
local function tryHookMixin(verbose)
  if hooked then return true end
  local mixin = _G.CommunitiesMemberListEntryMixin
  if type(mixin) ~= "table" then
    if verbose then ns.Debug("roster: CommunitiesMemberListEntryMixin does not exist") end
    return false
  end
  if type(mixin.UpdateNameFrame) ~= "function" then
    if verbose then ns.Debug("roster: CommunitiesMemberListEntryMixin.UpdateNameFrame does not exist") end
    return false
  end

  hooksecurefunc(mixin, "UpdateNameFrame", function(entry)
    local ok, err = pcall(annotate, entry)
    if not ok then ns.Debug("roster annotate failed:", err) end
  end)

  hooked = true
  ns.Debug("roster: hooked CommunitiesMemberListEntryMixin")
  return true
end

function Roster:OnEnable()
  if tryHookMixin() then return end

  -- Blizzard_Communities is a load-on-demand addon. Wait for it.
  local waiter = CreateFrame("Frame")
  waiter:RegisterEvent("ADDON_LOADED")
  waiter:SetScript("OnEvent", function(self, _, addon)
    if addon == "Blizzard_Communities" then
      ns.Debug("roster: Blizzard_Communities loaded")
      if tryHookMixin(true) then
        self:UnregisterAllEvents()
        self:SetScript("OnEvent", nil)
      end
    end
  end)
end
