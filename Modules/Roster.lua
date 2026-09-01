-- RoS-Tools/Modules/Roster.lua
-- Appends the item level to names in the Guild & Communities roster.
--
-- Blizzard rearranges the Communities frame fairly often and the entry
-- widget layout is not a stable API, so this module never assumes a
-- specific child name. It walks the entry's font strings, finds the one
-- currently showing the member's name, and appends to it. If the shape
-- changes out from under us, it degrades to a no-op instead of erroring.

local _, ns = ...

local Roster = ns:RegisterModule("Roster")

-- The shape of a suffix this addon writes: a space, a color escape ("|c"
-- followed by EIGHT hex digits, AARRGGBB -- spelling the literal "cff" here
-- left only six digit classes after it, so the pattern demanded ten and
-- matched nothing at all), a parenthesised number, and a reset.
--
-- Deliberately NOT anchored to the end of the string. Blizzard appends its
-- own trailing text to the name on some rows (alt-grouped members), which
-- puts our suffix mid-string -- an anchored strip then misses it and the next
-- refresh stacks a second "(ilvl)" on top. Stripping every occurrence also
-- cleans up any row a previous build already doubled.
--
-- The shape alone is NOT enough to identify our own work: Blizzard writes
-- exactly this shape too (a grey "(3)" on an alt-grouped row, a yellow note
-- count), and stripping those destroyed the very rows the unanchoring exists
-- for. So the color has to be one this addon can actually render -- see
-- isOwnColor below.
local SUFFIX_PATTERN = " (|c(%x%x%x%x%x%x%x%x)%((%d+)%)|r)"

-- Built on first use, not at file scope: Core/Config.lua owns ILVL_COLORS and
-- the load order that guarantees it is present is the .toc's, not this file's.
local ownColors

--- Is `hex` (eight AARRGGBB digits, no "|c") a color this addon writes a
--- suffix in? Every ILVL_COLORS tier, plus ns.COLOR.value, which
--- ns.ColorForIlvl falls back to when colorByIlvl is off. Anything else on
--- the row belongs to Blizzard and is left alone.
local function isOwnColor(hex)
  if not ownColors then
    ownColors = {}
    local tiers = ns.ILVL_COLORS or {}
    for i = 1, #tiers do
      if type(tiers[i].hex) == "string" then ownColors[tiers[i].hex:lower()] = true end
    end
    if ns.COLOR and ns.COLOR.value then ownColors[ns.COLOR.value:lower()] = true end
  end
  return ownColors["|c" .. hex:lower()] == true
end

--- Remove every suffix this addon could have written, and nothing else.
--- Blizzard's own trailing "(n)" survives, whatever color it carries.
---
--- The loose test (any color in the palette) rather than the exact one
--- below, on purpose: a suffix written before the user toggled colorByIlvl
--- is still ours, and a suffix we fail to remove is a suffix we double.
local function stripOwnSuffix(text)
  if not text then return text end
  return (text:gsub(SUFFIX_PATTERN, function(whole, hex, _)
    if isOwnColor(hex) then return "" end
    return " " .. whole
  end))
end

--- Remove only what this addon would render for that exact number right now:
--- same shape, and the color ns.ColorForIlvl gives the number in it.
---
--- Used for scoring candidates, not for editing text. There a tie against a
--- note column costs the row its annotation, so the tighter test earns its
--- keep: Blizzard's "|cffffff00(2)|r" note count is a palette color but not
--- the color an item level of 2 would ever be rendered in, and it drops back
--- to a plain prefix match. The cost of being wrong here is only that an
--- already-annotated row scores rank 4 instead of rank 3 -- it is still
--- found, and stripOwnSuffix still cleans it up.
local function stripCurrentSuffix(text)
  if not text then return text end
  return (text:gsub(SUFFIX_PATTERN, function(whole, hex, digits)
    local want = ns.ColorForIlvl(tonumber(digits) or -1)
    if type(want) == "string" and want:lower() == ("|c" .. hex):lower() then return "" end
    return " " .. whole
  end))
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
--- NOTE: this deliberately does NOT strip our own suffix first. It used to,
--- and that is what let an impostor win: any region whose text was the
--- member's name followed by a colored parenthesised number -- a note column
--- reading "Peidae-Khadgar |cffffff00(2)|r", say -- collapsed to exactly the
--- name, scored an exact match (rank 1), short-circuited the search and took
--- the annotation away from the real name row for the life of that frame.
--- An already-annotated name row is covered by rank 3 below instead, which
--- ranks it under a bare exact match rather than tied with one.
local function matchKey(text)
  if not text then return nil end
  text = stripColors(text)
  text = text:gsub(RIGHT_SQUOTE, "")
  text = text:gsub("[%s%p]", "")
  return text:lower()
end

--- Find the FontString in `frame` whose text is the member's name.
---
--- Scores candidates rather than taking the first hit:
---   1. an exact match on the full "Name-Realm";
---   2. an exact match on the bare name (the realm suffix is often its own
---      FontString or its own colored segment);
---   3. the name plus exactly the suffix this addon would render for that
---      number right now -- a row we annotated and Blizzard has not
---      rewritten since;
---   4. the name plus a suffix in one of our colors but not the one that
---      number would get today, which is what a row annotated before the
---      user toggled colorByIlvl (or by an older palette) looks like;
---   5. a string that merely starts with the full name, which is what an
---      alt-grouped row looks like, where Blizzard appends its own trailing
---      text after the name.
--- Nothing weaker is accepted; a prefix match on the bare name alone would
--- happily latch onto a note or a zone column.
---
--- Ranks 3 and 4 sit below the exact matches on purpose: a note column that
--- happens to carry a colored number must never outrank -- or tie with --
--- the row actually showing the name. Splitting them is what keeps a
--- Blizzard note count ("|cffffff00(2)|r": a palette color, but not the
--- color an item level of 2 renders in) under a genuinely annotated row
--- when both are present and Blizzard has not rewritten the name.
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
    else
      local function isName(text)
        local bare = matchKey(text)
        return bare and (bare == wantFull or (wantShort and bare == wantShort))
      end
      if isName(stripCurrentSuffix(raw)) then rank = 3
      elseif isName(stripOwnSuffix(raw)) then rank = 4
      elseif wantFull and wantFull ~= "" and key:sub(1, #wantFull) == wantFull then rank = 5
      end
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

  -- Same member, same number, and the string we wrote last time is still the
  -- one on the widget. Nothing has changed, so don't walk the tree again.
  --
  -- The ilvl has to be part of this. st.text was built from the *old* number
  -- and still matches the widget after the value moves underneath us, so
  -- keying on text alone made a live Comm update -- or a snapshot adopted
  -- mid-session -- invisible on an already-open roster until it was closed
  -- and reopened.
  if st.key == key and st.ilvl == ilvl and st.fs and st.text
     and st.fs:GetText() == st.text then
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

  -- Provenance beats pattern matching. If this is still the widget we wrote
  -- to and the string we wrote is still on it -- the case a changed item
  -- level lands in -- we know byte-for-byte what the row said before we
  -- touched it, so nothing has to be guessed back out of the text.
  --
  -- Otherwise (a pooled frame, a row a previous build doubled, a row
  -- Blizzard has appended to since) fall back to removing every suffix this
  -- addon could have written. Blizzard's own trailing text stays put: it is
  -- part of the base and gets re-appended with ours after it.
  local base
  if st.fs == fs and st.base and st.text and current == st.text then
    base = st.base
  else
    base = stripOwnSuffix(current)
  end
  if not base or base == "" then return end

  local text = ("%s %s(%d)%s"):format(base, ns.ColorForIlvl(ilvl), ilvl, ns.COLOR.reset)
  st.key, st.ilvl, st.fs, st.base, st.text = key, ilvl, fs, base, text
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
