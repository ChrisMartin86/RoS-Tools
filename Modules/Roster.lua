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

-- Deliberately NOT anchored to the end of the string. Blizzard appends its
-- own trailing text to the name on some rows (alt-grouped members), which
-- put our suffix mid-string -- an anchored strip then missed it and the next
-- refresh stacked a second "(ilvl)" on top. Stripping every occurrence also
-- cleans up any row a previous build already doubled.
local SUFFIX_PATTERN = " |cff%x%x%x%x%x%x%x%x%(%d+%)|r"

local function stripSuffix(text)
  if not text then return text end
  return (text:gsub(SUFFIX_PATTERN, ""))
end

--- Strip any Blizzard color escapes, not just the ones this addon adds.
--- The realm suffix on a cross-realm name is commonly dimmed with its own
--- |cff.../|r wrapper, which would otherwise break an exact-text match.
--- The newer |cnCOLOR_NAME: form is stripped too -- retail uses it in
--- places the old |cffRRGGBB form used to appear.
local function stripColors(text)
  if not text then return text end
  text = text:gsub("|c%x%x%x%x%x%x%x%x", "")
  text = text:gsub("|c[nN]%u[%u%d_]*:", "")
  return (text:gsub("|r", ""))
end

-- U+2019 RIGHT SINGLE QUOTATION MARK, spelled out -- Lua 5.1 has no \u{}.
local RIGHT_SQUOTE = "\226\128\153"

--- Collapse a displayed string down to just its letters and digits, so the
--- comparison survives the cosmetic differences Blizzard sprinkles through
--- the roster: a spaced vs space-stripped realm ("Moon Guard" /
--- "MoonGuard"), either apostrophe form in "Kel'Thuzad", and the hyphen
--- before the realm being present or not.
local function matchKey(text)
  if not text then return nil end
  text = stripColors(stripSuffix(text))
  text = text:gsub(RIGHT_SQUOTE, "")
  text = text:gsub("[%s%p]", "")
  return text:lower()
end

--- Find the FontString in `frame` whose text is the member's name.
---
--- Scores candidates rather than taking the first hit: an exact match on the
--- full "Name-Realm" wins, then an exact match on the bare name (the realm
--- suffix is often its own FontString or its own colored segment), then a
--- string that merely starts with the full name -- which is what an
--- alt-grouped row looks like, where Blizzard appends its own trailing text
--- after the name. Nothing weaker is accepted; a prefix match on the bare
--- name alone would happily latch onto a note or a zone column.
local function findNameFontString(frame, fullName, shortName, seen)
  if not frame or not frame.GetRegions then return nil end

  local wantFull, wantShort = matchKey(fullName), matchKey(shortName)
  local best, bestRank

  local function consider(region)
    local raw = region:GetText()
    local key = matchKey(raw)
    if not key or key == "" then return end

    local rank
    if key == wantFull then rank = 1
    elseif wantShort and key == wantShort then rank = 2
    elseif wantFull and wantFull ~= "" and key:sub(1, #wantFull) == wantFull then rank = 3
    end

    if seen then seen[#seen + 1] = raw end
    if rank and (not bestRank or rank < bestRank) then
      best, bestRank = region, rank
    end
  end

  local regions = { frame:GetRegions() }
  for i = 1, #regions do
    local region = regions[i]
    if region and region.GetObjectType and region:GetObjectType() == "FontString" then
      consider(region)
    end
  end
  if best and bestRank == 1 then return best end

  if frame.GetChildren then
    local children = { frame:GetChildren() }
    for i = 1, #children do
      local found = findNameFontString(children[i], fullName, shortName, seen)
      if found then return found end
    end
  end

  return best
end

--- memberInfo.name is a mixin-driven field: it is always "Name" or
--- "Name-Realm" and never carries a displayed title, so it must NOT go
--- through Util.NormalizeKey -- that helper strips a leading title by
--- taking the last whitespace-delimited token, which turns
--- "Helltz-Moon Guard" into "Guard".
local function memberKey(memberInfo)
  local name = memberInfo and memberInfo.name
  if type(name) ~= "string" or name == "" then return nil, nil end
  name = stripColors(name):gsub("^%s+", ""):gsub("%s+$", "")
  if name == "" then return nil, nil end

  local shortName, realm = name:match("^([^%-]+)%-(.+)$")
  if not shortName then
    return ns.Util.MakeKey(name, ns.playerRealmSlug), name, name
  end
  local slug = ns.Util.RealmToSlug(realm) or ns.playerRealmSlug
  return ns.Util.MakeKey(shortName, slug), name, shortName
end

local firedOnce = false
local warnedDisabled = false
local warnedNoInfo = false
local warnedNoName = false

-- Blizzard calls UpdateNameFrame far more often than a row actually changes:
-- scrolling, presence ticks and column refreshes all fire it, many times per
-- second for a visible member. Remember the last outcome per entry frame so a
-- repeat call for the same member costs a table lookup instead of a walk of
-- the whole widget tree -- and so a member with no data logs once rather than
-- once per tick. Weak keys, so pooled frames are still collectable.
local state = setmetatable({}, { __mode = "k" })

local function annotate(entry)
  if not firedOnce then
    firedOnce = true
    ns.Debug("roster: UpdateNameFrame hook is firing")
  end

  if not ns.db or not ns.db.rosterColumn then
    if not warnedDisabled then
      warnedDisabled = true
      ns.Debug("roster: rosterColumn disabled")
    end
    return
  end

  -- Pooled and placeholder rows have no memberInfo at all, and the list
  -- churns through plenty of them while scrolling. Worth knowing once that
  -- it happens; worth nothing to be told every frame.
  local info = entry and entry.memberInfo
  if not info then
    if not warnedNoInfo then
      warnedNoInfo = true
      ns.Debug("roster: entry has no memberInfo (pooled row) -- logged once")
    end
    return
  end

  local key, displayName, shortName = memberKey(info)
  if not key then
    if not warnedNoName then
      warnedNoName = true
      ns.Debug("roster: memberInfo.name missing/blank -- logged once")
    end
    return
  end

  local st = state[entry]
  if not st then
    st = {}
    state[entry] = st
  end

  local ilvl = ns.Data:GetByKey(key)
  if not ilvl then
    if st.miss ~= key then
      st.miss = key
      ns.Debug("roster: no data entry for", key)
    end
    return
  end
  st.miss = nil

  -- Same member, and the string we wrote last time is still the one on the
  -- widget. Nothing has changed, so don't walk the tree again.
  if st.key == key and st.fs and st.text and st.fs:GetText() == st.text then
    return
  end

  local seen = ns.db.debug and {} or nil
  local fs = findNameFontString(entry, displayName, shortName, seen)
  if not fs then
    ns.Debug("roster: no name fontstring for", displayName)
    if seen then
      for i = 1, #seen do
        ns.Debug(("  candidate %d: [%s]"):format(i, (tostring(seen[i]):gsub("|", "!"))))
      end
    end
    return
  end

  local current = fs:GetText()
  local base = stripSuffix(current)
  if not base or base == "" then return end

  local text = ("%s %s(%d)%s"):format(base, ns.ColorForIlvl(ilvl), ilvl, ns.COLOR.reset)
  st.key, st.fs, st.text = key, fs, text
  if current ~= text then fs:SetText(text) end
end

-- ------------------------------------------------------------------
-- Hover tooltip
--
-- Blizzard's own OnEnter builds a GameTooltip for a row only in some
-- states (a truncated name, a note worth expanding), so we handle both:
-- append to the tooltip when one is already up and owned by this row,
-- otherwise build our own. Either way OnLeave puts it away.
-- ------------------------------------------------------------------
local ownsTooltip = false

local function decorateTooltip(entry)
  if not ns.db or not ns.db.rosterTooltip then return end
  if ns.TooltipSuppressed and ns.TooltipSuppressed() then return end

  local info = entry and entry.memberInfo
  if not info then return end

  local key, displayName = memberKey(info)
  if not key then return end

  local ilvl = ns.Data:GetByKey(key)
  if not ilvl then
    ns.Debug("roster tooltip: no entry for", key)
    return
  end

  if GameTooltip:IsShown() and GameTooltip:GetOwner() == entry then
    ns.AddIlvlLines(GameTooltip, ilvl, true)
    return
  end

  GameTooltip:SetOwner(entry, "ANCHOR_RIGHT")
  GameTooltip:ClearLines()
  GameTooltip:AddLine(displayName, 1, 1, 1)
  ns.AddIlvlLines(GameTooltip, ilvl, false)
  ownsTooltip = true
end

local function releaseTooltip()
  if not ownsTooltip then return end
  ownsTooltip = false
  GameTooltip:Hide()
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

  -- The hover handlers are a separate, optional concern -- if Blizzard
  -- renames them the name annotation above still works.
  if type(mixin.OnEnter) == "function" and type(mixin.OnLeave) == "function" then
    hooksecurefunc(mixin, "OnEnter", function(entry)
      local ok, err = pcall(decorateTooltip, entry)
      if not ok then ns.Debug("roster tooltip failed:", err) end
    end)
    hooksecurefunc(mixin, "OnLeave", releaseTooltip)
    ns.Debug("roster: hooked entry OnEnter/OnLeave")
  else
    ns.Debug("roster: entry OnEnter/OnLeave missing, hover tooltip disabled")
  end

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
