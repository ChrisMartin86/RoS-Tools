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
  if ns.Util.IsSecret(text) then return nil end
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
  if ns.Util.IsSecret(text) then return nil end
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
  if ns.Util.IsSecret(text) then return nil end
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
  if ns.Util.IsSecret(text) then return nil end
  if not text then return nil end
  text = stripColors(text)
  if not text then return nil end
  text = text:gsub(RIGHT_SQUOTE, "")
  text = text:gsub("[%s%p]", "")
  return text:lower()
end

--- matchKey, but on a PREFIX of `text` -- which is not the same thing.
---
--- matchKey only strips COMPLETE colour escapes, so a prefix cut through the
--- middle of one leaves its letters in the key: "|c" collapses to "c", "|cff"
--- to "cff", "|cnGREEN" to "cngreen". That makes the keys non-monotone as the
--- prefix grows, and non-monotone keys break the "the shortest match is the
--- right one" premise both loops in stripRealm rest on. It is not theoretical:
--- a member named "Cff" on a class-coloured row -- "|cff00ff00Cff-Khadgar" --
--- matched wantShort at the four bytes "|cff" and had their entire name
--- spliced away, leaving a bare colour fragment where the name should be. The
--- "|c[nN]NAME:" form has the same family ("Cn", "Cnn", "Cnno"...).
---
--- So chop a trailing escape the cut ran through before collapsing. Each
--- pattern is anchored at the end and demands an INCOMPLETE token -- at most
--- seven hex digits, or a colour name with no closing colon -- so a complete
--- escape is left for matchKey to strip properly.
local function prefixKey(text, n)
  local prefix = text:sub(1, n)
  prefix = prefix:gsub("|$", "")
  prefix = prefix:gsub("|c%x?%x?%x?%x?%x?%x?%x?$", "")
  prefix = prefix:gsub("|c[nN]%u?[%u%d_]*$", "")
  return matchKey(prefix)
end

--- Rewrite `text` so the "-Realm" suffix is gone and everything else survives.
---
--- Deliberately not a pattern strip. "cut at the first hyphen" is wrong three
--- ways at once: the displayed realm is not reliably spelled the way
--- memberInfo spells it (spaced vs space-stripped), it often carries its own
--- color wrapper, and on an alt-grouped row Blizzard has appended its own
--- trailing text after the name -- which a cut would eat along with the realm.
---
--- So it works by collapse instead, the same way findNameFontString does.
--- Find the shortest prefix that matchKeys to the full "Name-Realm", the
--- shortest prefix of *that* which matchKeys to the bare name, and splice:
--- keep the bare-name prefix (so a leading class-color escape stays put),
--- drop the realm, and carry through whatever followed it -- a stray "|r"
--- from the realm's own wrapper, Blizzard's "(3)". No prefix shorter than the
--- whole name can collapse to it, so the first hit is the right one.
---
--- Returns `text` untouched when there is nothing to do: a same-realm member,
--- or a font string holding only the bare name because Blizzard put the realm
--- in a sibling region. Hiding what is already hidden is not this function's
--- problem, and guessing is worse than a no-op.
local function stripRealm(text, fullName, shortName)
  if type(text) ~= "string" or type(fullName) ~= "string" or type(shortName) ~= "string" then
    return text
  end
  if fullName == shortName then return text end

  local wantFull, wantShort = matchKey(fullName), matchKey(shortName)
  if not wantFull or wantFull == "" or not wantShort or wantShort == "" then return text end

  -- Where to stop looking. The realm sits immediately after the name; every
  -- byte past "name + realm + the escapes wrapping them" is Blizzard's own
  -- trailing text, which this function exists to preserve, not to search. The
  -- bound also caps the cost: both loops are O(n) in matchKey calls and
  -- matchKey is O(n) in gsubs, so an unbounded scan over a long region that
  -- never matches is quadratic -- measured at 30ms on a 1KB string, which is
  -- a visible hitch on a hook that fires many times a second.
  local last = #fullName + 128
  if last > #text then last = #text end

  for i = #shortName, last do
    if prefixKey(text, i) == wantFull then
      for j = #shortName, i do
        if prefixKey(text, j) == wantShort then
          return text:sub(1, j) .. text:sub(i + 1)
        end
      end
      return text
    end
  end
  return text
end

--- Find the FontString in `frame` whose text is the member's name.
---
--- Scores candidates rather than taking the first hit:
---   1. an exact match on the full "Name-Realm";
---   2. an exact match on the bare name (the realm suffix is often its own
---      FontString or its own colored segment);
---   4. the name plus exactly the suffix this addon would render for that
---      number right now -- a row we annotated and Blizzard has not
---      rewritten since;
---   5. the name plus a suffix in one of our colors but not the one that
---      number would get today, which is what a row annotated before the
---      user toggled colorByIlvl (or by an older palette) looks like;
---   6. a string that merely starts with the full name, which is what an
---      alt-grouped row looks like, where Blizzard appends its own trailing
---      text after the name.
--- Nothing weaker is accepted; a prefix match on the bare name alone would
--- happily latch onto a note or a zone column.
---
--- Rank 3 is not a text match at all: `prev` is the region this addon wrote to
--- last time for this same member, and the string it wrote there. Provenance,
--- not pattern.
---
--- It exists because hideRealm broke every text rank at once. Once the row
--- reads "Peidae", ranks 1-2 want "Peidae-Khadgar" and rank 6 tests a prefix
--- of the FULL name, so a realm-stripped row with Blizzard's own trailing
--- text on it ("Peidae (620) (3)") scored nothing whatsoever -- and the
--- annotation then migrated to whatever note column still spelled the realm
--- out, which is the impostor bug all over again. Loosening rank 6 to the bare
--- name would have reopened it directly; remembering which region we wrote to
--- does not.
---
--- It sits BELOW the two exact matches, and does not short-circuit the search.
--- Provenance is evidence, not proof: the region we wrote to last time is only
--- the right one until Blizzard moves the name, and a row we once latched onto
--- wrongly would otherwise re-latch onto itself forever, with nothing left
--- that could ever pull the annotation back to the real name column. Ranked
--- above the text ranks and below the exact ones, it rescues the rows it was
--- added for and yields the moment a better answer exists.
---
--- Ranks 4 and 5 sit below the exact matches on purpose: a note column that
--- happens to carry a colored number must never outrank -- or tie with --
--- the row actually showing the name. Splitting them is what keeps a
--- Blizzard note count ("|cffffff00(2)|r": a palette color, but not the
--- color an item level of 2 renders in) under a genuinely annotated row
--- when both are present and Blizzard has not rewritten the name.
local function findNameFontString(frame, fullName, shortName, seen, prev)
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
      -- Our own last rendering on our own last region, plus anything Blizzard
      -- has appended after it.
      local ours = prev and prev.fs == region and prev.display and prev.display ~= ""
      local mine = ours and stripOwnSuffix(raw) or nil
      if mine and mine:sub(1, #prev.display) == prev.display then rank = 3
      elseif isName(stripCurrentSuffix(raw)) then rank = 4
      elseif isName(stripOwnSuffix(raw)) then rank = 5
      elseif wantFull and wantFull ~= "" and key:sub(1, #wantFull) == wantFull then rank = 6
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
  if best and bestRank == 1 then return best, bestRank end

  -- The child's answer used to win outright, whatever it scored -- so a
  -- rank-6 prefix hit on a note column one frame down beat a rank-2 exact
  -- match on the parent. Merge on rank instead.
  if frame.GetChildren then
    local children = { frame:GetChildren() }
    for i = 1, #children do
      local found, rank = findNameFontString(children[i], fullName, shortName, seen, prev)
      if found and rank and (not bestRank or rank < bestRank) then
        best, bestRank = found, rank
        if rank == 1 then return best, bestRank end
      end
    end
  end

  return best, bestRank
end

--- memberInfo.name is a mixin-driven field: it is always "Name" or
--- "Name-Realm" and never carries a displayed title, so it must NOT go
--- through Util.NormalizeKey -- that helper strips a leading title by
--- taking the last whitespace-delimited token, which turns
--- "Helltz-Moon Guard" into "Guard".
local function memberKey(memberInfo)
  local name = memberInfo and memberInfo.name
  if ns.Util.IsSecret(name) then return nil, nil end
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

--- Un-write this row, if it is still carrying exactly what we wrote, and
--- forget it either way.
---
--- Turning rosterColumn or hideRealm off has to undo its own work. Nothing
--- else will: the paths below return before they ever reach a widget, and on
--- a row Blizzard is not currently rewriting the old rendering would simply
--- stand. That is merely untidy for a stale item level and actively bad for a
--- hidden realm -- the player switched the option off to see the realm, and
--- there is no other way for them to get it back.
---
--- Only ever restores the exact string we replaced (st.base), and only when
--- the widget still holds the exact string we wrote (st.text). Anything else
--- has been rewritten since and is not ours to touch.
--- Put `text` back to `base` on `fs`, if `text` is still what `fs` starts
--- with. A prefix, not equality: Blizzard appends its own trailing text to
--- some rows after our hook runs, and an equality test then finds nothing to
--- undo on exactly the rows that most need undoing -- the ones Blizzard is
--- not rewriting, where nothing else will ever repair them.
local function unwrite(fs, base, text)
  if not fs or not base or not text then return end
  local shown = fs:GetText()
  if ns.Util.IsSecret(shown) then return end
  if shown:sub(1, #text) ~= text then return end
  fs:SetText(base .. shown:sub(#text + 1))
end

local function revert(st)
  if not st then return end
  local fs, base, text = st.fs, st.base, st.text
  st.key, st.ilvl, st.hide, st.color, st.fs, st.base, st.display, st.text,
    st.miss, st.lost = nil
  unwrite(fs, base, text)
end

local function annotate(entry)
  if not firedOnce then
    firedOnce = true
    ns.Debug("roster: UpdateNameFrame hook is firing")
  end

  -- Two independent jobs share this hook: the ilvl suffix (rosterColumn) and
  -- the realm strip (hideRealm). Either one alone is reason enough to walk the
  -- row; only both being off is reason to leave.
  if not ns.db then return end
  local wantColumn, wantHide = ns.db.rosterColumn, ns.db.hideRealm
  if not wantColumn and not wantHide then
    revert(state[entry])
    if not warnedDisabled then
      warnedDisabled = true
      ns.Debug("roster: rosterColumn and hideRealm both disabled")
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

  local ilvl = wantColumn and ns.Data:GetByKey(key) or nil
  if wantColumn and not ilvl then
    if st.miss ~= key then
      st.miss = key
      ns.Debug("roster: no data entry for", key)
    end
  else
    st.miss = nil
  end

  -- No number to append and no realm to remove (a same-realm member, or
  -- hideRealm off) -- there is nothing this row needs. Bail before the widget
  -- walk, which is the expensive part.
  local hideThis = wantHide and displayName ~= shortName
  if not ilvl and not hideThis then
    -- Nothing to write -- but we may have written here before, under a
    -- setting that has since been turned off.
    revert(st)
    return
  end

  -- Same member, same number, and the string we wrote last time is still the
  -- one on the widget. Nothing has changed, so don't walk the tree again.
  --
  -- The ilvl has to be part of this. st.text was built from the *old* number
  -- and still matches the widget after the value moves underneath us, so
  -- keying on text alone made a live Comm update -- or a snapshot adopted
  -- mid-session -- invisible on an already-open roster until it was closed
  -- and reopened.
  -- Two cheap exits, both meaning "same member, same number, same options":
  --
  --   * the string we wrote is still on the widget -- nothing to do;
  --   * Blizzard has rewritten the widget to exactly the base it wrote last
  --     time, so the answer is the string already computed. Skipping the walk
  --     here matters: UpdateNameFrame re-sets the name on nearly every call,
  --     which is the common case, not the rare one -- and hideRealm gives a
  --     member with no exported item level a reason to walk the tree at all,
  --     where before it returned before ever looking at a widget.
  --
  -- st.hide and st.color are in the key for the same reason st.ilvl is: each
  -- one changes the string we owe this row while the row itself has not
  -- changed, and without them the early-outs pin the old rendering. st.color
  -- is the one the second exit made load-bearing -- before it, a toggled
  -- colorByIlvl was repaired by the walk this exit now skips.
  local colorBy = ns.db.colorByIlvl and true or false
  if st.key == key and st.ilvl == ilvl and st.hide == hideThis and st.color == colorBy
     and st.fs and st.text then
    local shown = st.fs:GetText()
    if not ns.Util.IsSecret(shown) then
      if shown == st.text then return end
      if st.base and shown == st.base then
        st.fs:SetText(st.text)
        return
      end
    end
  end

  local seen = ns.db.debug and {} or nil
  -- Scoped to this member: on a pooled frame st.fs is the previous occupant's
  -- region, and its display string is not evidence about this one.
  local prev = (st.key == key and st.fs) and { fs = st.fs, display = st.display } or nil
  local fs = findNameFontString(entry, displayName, shortName, seen, prev)
  if not fs then
    -- Once per member, not once per tick: a row that cannot be found is found
    -- again on every presence update, and this used to flood the debug log.
    if st.lost ~= key then
      st.lost = key
      ns.Debug("roster: no name fontstring for", displayName)
    end
    if seen then
      for i = 1, #seen do
        ns.Debug(("  candidate %d: [%s]"):format(i, (tostring(seen[i]):gsub("|", "!"))))
      end
    end
    return
  end
  st.lost = nil

  -- The name moved to a different region -- Blizzard rearranged the row, or a
  -- better-ranked candidate appeared and pulled the annotation off a region we
  -- had latched onto by mistake. Clean up after ourselves before writing to
  -- the new one, or the abandoned region keeps a stale item level (and, with
  -- hideRealm on, a name whose realm we removed) for the life of the frame.
  if st.key == key and st.fs and st.fs ~= fs then
    unwrite(st.fs, st.base, st.text)
  end

  local current = fs:GetText()
  if ns.Util.IsSecret(current) then
    ns.Debug("roster: name fontstring holds a secret value, leaving it alone")
    return
  end

  -- Provenance beats pattern matching. If this is still the widget we wrote
  -- to and the string we wrote is still on it -- the case a changed item
  -- level lands in -- we know byte-for-byte what the row said before we
  -- touched it, so nothing has to be guessed back out of the text.
  --
  -- Otherwise (a pooled frame, a row a previous build doubled, a row
  -- Blizzard has appended to since) fall back to removing every suffix this
  -- addon could have written. Blizzard's own trailing text stays put: it is
  -- part of the base and gets re-appended with ours after it.
  --
  -- The prefix test rather than `current == st.text`: Blizzard appends its own
  -- trailing text to some rows AFTER our hook runs, so the widget routinely
  -- holds our rendering plus a tail we have never seen. Splicing st.base back
  -- under that tail is what makes hideRealm reversible -- an equality test
  -- falls through to the else branch, where `current` is already stripped and
  -- st.base becomes the stripped name permanently.
  local bare = stripOwnSuffix(current)
  local base = bare

  -- Reconstruct the pristine base only when the widget is carrying OUR
  -- rendering. Two guards, both of which were missing and both of which
  -- corrupted names:
  --
  --   * st.key == key. `state` is keyed by entry FRAME, and frames are pooled:
  --     scroll the list and this frame is another member. Splicing the
  --     previous occupant's base under the new one's text produced
  --     "Peidae-Khadgarholic-Khadgar" for any successor whose name merely
  --     started with the predecessor's.
  --   * the realm is not already on the widget. st.display is a prefix of
  --     st.base whenever a realm was stripped, so the prefix test alone also
  --     fires on Blizzard's own pristine text and spliced the realm in twice.
  --     Asking whether `bare` still collapses to "Name-Realm" settles it
  --     regardless of how Blizzard spelled the realm this time -- spaced,
  --     space-stripped, or freshly wrapped in a dim color.
  local wantFull = matchKey(displayName)
  local bareKey = matchKey(bare)
  local realmShown = wantFull and wantFull ~= "" and bareKey
    and bareKey:sub(1, #wantFull) == wantFull
  if not realmShown and st.key == key and st.fs == fs and st.base and st.display
     and bare and bare:sub(1, #st.display) == st.display then
    base = st.base .. bare:sub(#st.display + 1)
  end
  if not base or base == "" then return end

  -- `base` stays pristine in st: realm intact, our suffix off. The realm strip
  -- is applied to the rendering, never folded back into the base -- otherwise
  -- the provenance branch above hands back an already-stripped string and
  -- turning hideRealm off could never restore the realm.
  local display = hideThis and stripRealm(base, displayName, shortName) or base

  local text = ilvl
    and ("%s %s(%d)%s"):format(display, ns.ColorForIlvl(ilvl), ilvl, ns.COLOR.reset)
    or display
  st.key, st.ilvl, st.hide, st.color, st.fs, st.base, st.display, st.text =
    key, ilvl, hideThis, colorBy, fs, base, display, text
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

  local key, displayName, shortName = memberKey(info)
  if not key then return end

  local ilvl = ns.Data:GetByKey(key)
  if not ilvl then
    ns.Debug("roster tooltip: no entry for", key)
    return
  end

  -- Match what the row itself now shows. A tooltip that spells out the realm
  -- the row just hid reads as a bug, not a courtesy.
  --
  -- Only on the tooltip we build ourselves. When Blizzard already owns one for
  -- this row we append to it and its header is Blizzard's, spelled out in
  -- full; rewriting someone else's line 1 is not worth the blast radius.
  local title = ns.db.hideRealm and shortName or displayName

  if GameTooltip:IsShown() and GameTooltip:GetOwner() == entry then
    ns.AddIlvlLines(GameTooltip, ilvl, true)
    return
  end

  GameTooltip:SetOwner(entry, "ANCHOR_RIGHT")
  GameTooltip:ClearLines()
  GameTooltip:AddLine(title, 1, 1, 1)
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
