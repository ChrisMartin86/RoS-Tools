-- Offline harness for Modules/*.lua -- the UI side of the addon, which
-- Tools/sync-harness.lua does not load at all.
--
-- Roster.lua is the one module that touches Blizzard's Communities UI, and
-- the only way to exercise it outside the game is to build the widget shapes
-- it expects: a mixin with UpdateNameFrame, hooksecurefunc, and entry frames
-- whose GetRegions() hands back FontStrings. Browser.lua wants a frame
-- factory and the FauxScrollFrame helpers; Commands.lua wants a slash
-- registry. That is what this file stubs. It runs the REAL Core/Init,
-- Core/Util, Core/Config, Core/Data and Modules/* against those stubs.
--
-- Run from the repo root:   lua5.1 Tools/module-checks.lua
-- Exits non-zero on any failure.
--
-- Every scenario below is here because it once failed. Read the label as a
-- bug report, not a feature list.
--
-- Excluded from packaging and from luacheck (Tools/ is in .luacheckrc's
-- exclude_files), so it never ships to CurseForge.

local ROOT = os.getenv("ROSTOOLS_ROOT") or "./"

-- ---------- assertions ----------
local pass, fail = 0, 0
local function check(label, cond, detail)
  if cond then
    pass = pass + 1
    print("  PASS  " .. label)
  else
    fail = fail + 1
    print("  FAIL  " .. label .. (detail and ("  -- " .. detail) or ""))
  end
end
local function section(s) print("\n== " .. s .. " ==") end

--- Drop every colour escape, so an assertion can match the words a player
--- actually reads rather than the markup around them.
local function plain(text)
  return (tostring(text or ""):gsub("|c%x%x%x%x%x%x%x%x", ""):gsub("|r", ""))
end

--- Make a colour escape readable in failure output.
local function show(text)
  if text == nil then return "nil" end
  return (tostring(text):gsub("|", "!"))
end

--- How many "(NNN)" suffixes a rendered name carries -- OURS AND BLIZZARD'S.
--- Two is the bug only on a row Blizzard has not written its own onto.
local function suffixCount(text)
  local n = 0
  for _ in tostring(text or ""):gmatch("%(%d+%)") do n = n + 1 end
  return n
end

--- How many times `needle` occurs in `text`, plain-text.
local function occurrences(text, needle)
  local n, at = 0, 1
  text = tostring(text or "")
  while true do
    local s, e = text:find(needle, at, true)
    if not s then return n end
    n, at = n + 1, e + 1
  end
end

-- ---------- secret values (12.0) ----------
-- A real secret cannot be built in plain Lua, so it is modelled as a table
-- that raises on ANY access, plus a matching `issecretvalue` in the addon
-- environment. That is stricter than the client -- if the code touches one
-- at all, the harness fails loudly rather than quietly returning nil.
local SECRETS = setmetatable({}, { __mode = "k" })

--- A secret that is a REAL Lua string. `type()` reports "string" for a secret
--- in game, so every pre-existing `type(x) ~= "string"` check waves one
--- through -- and a table stand-in would be stopped by that check instead of
--- by the guard, making the assertion pass for the wrong reason. Plain Lua
--- cannot make indexing or comparing this error, so what it proves is the
--- other half: the guard, not the type check, is what returns nil.
local function markSecret(str)
  SECRETS[str] = true
  return str
end

--- A secret that raises on ANY access. Stricter than the client, and the
--- right fixture wherever the point is that the code never touches it.
local function newSecret(what)
  local boom = function() error("touched a secret value (" .. what .. ")", 2) end
  local s = setmetatable({}, {
    __index = boom, __newindex = boom, __call = boom,
    __len = boom, __concat = boom, __tostring = boom,
  })
  SECRETS[s] = true
  return s
end

-- ---------- widget stubs ----------

--- A universal no-op: callable, and indexable back into itself. Widget code
--- chains through a lot of setters this harness does not care about
--- (SetPoint, SetJustifyH, ScrollBar:SetValue), and this absorbs all of them
--- without a stub per method going stale every patch.
local NOOP = {}
setmetatable(NOOP, {
  __call  = function() return nil end,
  __index = function() return NOOP end,
})

--- A FontString: GetText / SetText / GetObjectType are the entire surface
--- findNameFontString() and annotate() touch; the layout calls Browser.lua
--- makes on its own font strings fall through to NOOP.
local function newFontString(text)
  local fs = { text = text }
  function fs:GetText() return self.text end
  function fs:SetText(t) self.text = t end
  function fs:GetObjectType() return "FontString" end
  return setmetatable(fs, { __index = function() return NOOP end })
end

--- A nested child frame: regions, and optionally children of its own.
--- findNameFontString() recurses into these, and the rank-1 short-circuit
--- above the recursion decides whether it ever gets that far.
local function newSubFrame(regions, children)
  local f = { regions = regions or {} }
  function f:GetRegions() return unpack(self.regions) end
  if children then
    f.children = children
    function f:GetChildren() return unpack(self.children) end
  end
  return f
end

--- An entry frame. `regions` is ordered on purpose: findNameFontString()
--- scores candidates and ties break on position, so the order a row hands
--- its font strings back is part of what the scoring has to survive.
---
--- opts.children -- nested frames, so the recursion in findNameFontString()
---   (and the rank-1 short-circuit that can skip it) is actually executed.
--- opts.nameFs -- the FontString Blizzard's own UpdateNameFrame owns on this
---   row; see the mixin stub. A row without one models the states where
---   Blizzard leaves the text alone and our own annotation is still on it
---   when the next pass starts.
local function newEntry(memberInfo, regions, opts)
  opts = opts or {}
  local entry = { memberInfo = memberInfo, regions = regions or {}, nameFs = opts.nameFs }
  function entry:GetRegions() return unpack(self.regions) end
  if opts.children then
    entry.children = opts.children
    function entry:GetChildren() return unpack(self.children) end
  end
  return entry
end

--- A frame. Carries real text, real shown state and real scripts, because
--- those three are what the assertions read; everything else falls through
--- to NOOP.
local function newWidget(kind)
  local w = { kind = kind, text = "", scripts = {} }
  function w:GetText() return self.text end
  function w:SetText(t) self.text = t end
  function w:GetObjectType() return kind end
  function w:IsShown() return self.shown == true end
  function w:Hide() self.shown = false end
  function w:Show()
    self.shown = true
    if self.scripts.OnShow then self.scripts.OnShow(self) end
  end
  function w:SetScript(k, fn) self.scripts[k] = fn end
  function w:GetScript(k) return self.scripts[k] end
  function w:CreateFontString() return newFontString("") end
  w.TitleText = newFontString("")
  setmetatable(w, { __index = function() return NOOP end })
  return w
end

-- ---------- addon environment ----------
local FILES = { "Core/Init.lua", "Core/Util.lua", "Core/Config.lua",
                "Core/Data.lua", "Modules/Browser.lua", "Modules/Commands.lua",
                "Modules/Roster.lua" }

--- Stand up one addon instance. Returns the namespace plus the pieces a
--- scenario drives: the mixin (whose UpdateNameFrame is the hooked entry
--- point) and the captured chat output.
local function newAddon(ilvls, opts)
  opts = opts or {}
  local env = {}
  for k, v in pairs(_G) do env[k] = v end
  local ns = {}
  local said = {}

  env._G = env
  env.RoSToolsDB = {}
  env.time, env.date = os.time, os.date
  env.wipe = function(t) for k in pairs(t) do t[k] = nil end return t end
  env.C_AddOns = { GetAddOnMetadata = function() return "test" end }
  env.DEFAULT_CHAT_FRAME = { AddMessage = function(_, m) said[#said + 1] = m end }
  env.GetAverageItemLevel = function() return 300, 295 end
  env.UnitName = function() return "Tester" end
  env.GetRealmName = function() return "Khadgar" end
  env.UnitExists = function() return false end
  env.UnitIsPlayer = function() return false end
  env.GetPlayerInfoByGUID = function() return nil end

  -- The 12.0 secret-value detector. See section 10.
  env.issecretvalue = function(v) return SECRETS[v] == true end

  -- Nothing creates a frame at load or enable time. Roster:OnEnable does,
  -- but only via the load-on-demand waiter it falls back to when the mixin
  -- hook fails -- so a non-zero count straight after startup means the hook
  -- silently never attached and every Roster scenario below is vacuous.
  local created = {}
  env.CreateFrame = function(kind)
    local w = newWidget(kind or "Frame")
    created[#created + 1] = w
    return w
  end
  env.UIParent = NOOP
  env.UISpecialFrames = {}
  env.SlashCmdList = {}
  env.tinsert = table.insert

  -- A real scroll offset, not a constant 0: refresh() reads it to decide
  -- which slice of the dataset the rows show, and pinning it to the top of
  -- the list left that arithmetic untested.
  local scrollOffset = 0
  env.FauxScrollFrame_GetOffset = function() return scrollOffset end
  env.FauxScrollFrame_Update = function() end
  env.FauxScrollFrame_SetOffset = function(_, offset) scrollOffset = offset or 0 end
  env.FauxScrollFrame_OnVerticalScroll = function() end
  env.SearchBoxTemplate_OnTextChanged = function() end

  -- The mixin Blizzard_Communities defines. UpdateNameFrame is the real
  -- hook target; OnEnter/OnLeave are present so the optional hover path is
  -- exercised rather than skipped.
  --
  -- The original SETS THE NAME on the row before any hook sees it. With a
  -- no-op original the fixtures decided by themselves whether an annotated
  -- row could even be observed, so the suite could not tell "our suffix
  -- survived" from "Blizzard rewrote the row underneath it". Rows built
  -- without a nameFs keep the old no-op behaviour on purpose -- that is the
  -- other half of the state space.
  local mixin = {
    UpdateNameFrame = function(entry)
      local info = entry and entry.memberInfo
      if entry and entry.nameFs and info and type(info.name) == "string" then
        entry.nameFs:SetText(info.name)
      end
    end,
    OnEnter = function() end,
    OnLeave = function() end,
  }
  env.CommunitiesMemberListEntryMixin = mixin

  -- A post-hook, same contract as WoW's: the original runs first, the hook
  -- runs after with the same arguments, and the original's return value is
  -- what the caller sees.
  env.hooksecurefunc = function(tbl, name, post)
    local orig = tbl[name]
    tbl[name] = function(...)
      local results = { orig(...) }
      post(...)
      return unpack(results)
    end
  end

  local guildData = {
    meta = { generated_epoch = os.time() - 600, generated_at = "x", region = "us",
             realm = "khadgar", guild = "riddle-of-steel", schema = 3 },
    ilvls = ilvls,
  }

  for _, f in ipairs(FILES) do
    local chunk = assert(loadfile(ROOT .. f))
    setfenv(chunk, env)
    if f == "Core/Data.lua" then ns.GuildData = guildData end
    chunk("RoS-Tools", ns)
  end

  ns.LoadConfig()
  ns.db.debug = opts.debug or false
  ns.playerName = "Tester"
  ns.playerRealmSlug = "khadgar"
  -- Pre-2.0 flat globals, still sitting in the addon folder. Set before the
  -- build so Data:Build() backfills from them, exactly as it does in game.
  if opts.legacy then env.RiddledTooltip_DB = opts.legacy end
  ns.Data:Build()
  ns:EnableModules()

  return {
    ns = ns, env = env, mixin = mixin, said = said,
    created = created,
    setScrollOffset = function(n) scrollOffset = n end,
    --- Every row frame the Browser built, in list order.
    rows = function()
      local out = {}
      for i = 1, #created do
        if rawget(created[i], "name") then out[#out + 1] = created[i] end
      end
      return out
    end,
    waiters = function() return #created end,
    --- Did anything printed to the chat frame contain `needle`?
    saidHas = function(needle)
      for i = 1, #said do
        if said[i]:find(needle, 1, true) then return true end
      end
      return false
    end,
    --- The first created widget of a given type, e.g. the Browser's search box.
    widget = function(kind)
      for i = 1, #created do
        if created[i].kind == kind then return created[i] end
      end
      return nil
    end,
  }
end

-- Every scenario uses one member with a realm suffix on the row, which is
-- the shape that made the SUFFIX_PATTERN bug visible.
local MEMBER = { name = "Peidae-Khadgar" }
local KEY    = "Peidae-khadgar"

-- ==================================================================
section("1. the hook attaches to the stub mixin")
-- If this fails nothing below tests anything, so it is asserted first.
-- ==================================================================
local a = newAddon({ [KEY] = 620 })
check("hooked without falling back to the ADDON_LOADED waiter", a.waiters() == 0,
      a.waiters() .. " waiter frames created")

local plainEntry = newEntry(MEMBER, { newFontString("Peidae-Khadgar") })
a.mixin.UpdateNameFrame(plainEntry)
check("an un-annotated row gets its item level appended",
      plainEntry.regions[1]:GetText():find("(620)", 1, true) ~= nil,
      show(plainEntry.regions[1]:GetText()))
check("exactly one suffix on the first pass",
      suffixCount(plainEntry.regions[1]:GetText()) == 1,
      show(plainEntry.regions[1]:GetText()))

-- ==================================================================
section("2. REGRESSION: the suffix pattern must match our own suffix")
-- The pattern spelled the escape as "|cff" plus eight hex classes, so it
-- demanded TEN hex digits after "|c" and matched nothing at all. The strip
-- was therefore a no-op, and annotate() appended a second "(620)" onto text
-- that still carried the first. A row the previous pass had already
-- annotated is the case the pattern exists for -- see its own comment.
--
-- Section 7 is the other side of this: matching too much is its own bug.
-- ==================================================================
local b = newAddon({ [KEY] = 620 })

-- A row carrying a suffix from an earlier pass, with no memo entry for it:
-- exactly what a pooled frame handed back to the same member looks like,
-- and what a row a previous build already doubled looks like.
local staleFs = newFontString("Peidae-Khadgar |cffff8000(620)|r")
local staleEntry = newEntry(MEMBER, { staleFs })
b.mixin.UpdateNameFrame(staleEntry)

-- These three used to be satisfied by a complete no-op: the fixture already
-- reads "...(620)|r", so "one suffix, the right number, name intact" is true
-- before annotate() runs at all. What a no-op cannot fake is the RE-RENDER:
-- the fixture's suffix is legendary orange, and 620 renders uncommon green,
-- so the row is only correct if the old suffix was stripped and a new one
-- written. Assert the color, and the assertions above it stop being free.
check("RE-RENDERED: the stale orange suffix was replaced with 620's own color",
      occurrences(staleFs:GetText(), "|cff1eff00(620)|r") == 1
        and staleFs:GetText():find("ff8000", 1, true) == nil,
      show(staleFs:GetText()))
check("NOT DOUBLED: an already-annotated row still carries one suffix",
      suffixCount(staleFs:GetText()) == 1, show(staleFs:GetText()))
check("and the member's name survived the strip",
      staleFs:GetText():find("Peidae-Khadgar", 1, true) == 1, show(staleFs:GetText()))

-- A row doubled by an older build must be cleaned up, not tripled -- the
-- pattern is unanchored specifically so it strips every occurrence.
local doubledFs = newFontString("Peidae-Khadgar |cffff8000(620)|r |cffff8000(620)|r")
b.mixin.UpdateNameFrame(newEntry(MEMBER, { doubledFs }))
check("a row a previous build doubled is repaired to one suffix",
      suffixCount(doubledFs:GetText()) == 1, show(doubledFs:GetText()))
check("and the repaired row is the freshly rendered suffix, not a leftover",
      doubledFs:GetText() == "Peidae-Khadgar |cff1eff00(620)|r", show(doubledFs:GetText()))

-- The other half of the same bug: a broken pattern left the item level
-- digits in the comparison key, so the annotated name row scored no better
-- than a note column that merely starts with the name -- and the note won on
-- position and got the item level appended to it instead. The name row is
-- ranked above a plain prefix match now (rank 4 here: the fixture's orange
-- is a palette color, but not the one 620 renders in today), and matchKey()
-- no longer strips anything -- see section 7 for why that mattered.
local noteFs = newFontString("Peidae-Khadgar raid lead")
local nameFs = newFontString("Peidae-Khadgar |cffff8000(620)|r")
local ambiguous = newEntry(MEMBER, { noteFs, nameFs })   -- note deliberately first
b.mixin.UpdateNameFrame(ambiguous)
check("RANK: the annotated name still outranks a note that starts with the name",
      suffixCount(nameFs:GetText()) == 1 and suffixCount(noteFs:GetText()) == 0,
      ("name=[%s] note=[%s]"):format(show(nameFs:GetText()), show(noteFs:GetText())))

-- ==================================================================
section("3. REGRESSION: a changed item level must reach an open roster")
-- The memo compared the widget's text against the string built from the OLD
-- item level, and they still matched -- so a live Comm update, or a snapshot
-- adopted mid-session, never reached a roster that was already open. The
-- number only moved if the user closed and reopened the window.
-- ==================================================================
local c = newAddon({ [KEY] = 620 })
local liveFs = newFontString("Peidae-Khadgar")
local liveEntry = newEntry(MEMBER, { liveFs })

c.mixin.UpdateNameFrame(liveEntry)
check("baseline annotation is on the row (test is not vacuous)",
      liveFs:GetText():find("(620)", 1, true) ~= nil, show(liveFs:GetText()))

-- Blizzard calls UpdateNameFrame many times a second for a visible row; the
-- memo is what makes that cheap, and it must still short-circuit.
local beforeRepeat = liveFs:GetText()
for _ = 1, 20 do c.mixin.UpdateNameFrame(liveEntry) end
check("repeat calls at an unchanged value leave the row alone",
      liveFs:GetText() == beforeRepeat and suffixCount(liveFs:GetText()) == 1,
      show(liveFs:GetText()))

-- A guildmate broadcasts an upgrade over addon comm, exactly as
-- Core/Comm.lua's receive path does.
c.ns.Data:ApplyLiveUpdate(KEY, 640)
c.mixin.UpdateNameFrame(liveEntry)

check("LIVE UPDATE: the row shows the new item level",
      liveFs:GetText():find("(640)", 1, true) ~= nil, show(liveFs:GetText()))
check("and the old item level is gone, not appended to",
      liveFs:GetText():find("(620)", 1, true) == nil, show(liveFs:GetText()))
check("and there is still exactly one suffix",
      suffixCount(liveFs:GetText()) == 1, show(liveFs:GetText()))

-- Down as well as up: an item level that drops has to move too.
c.ns.Data:ApplyLiveUpdate(KEY, 605)
c.mixin.UpdateNameFrame(liveEntry)
check("a value that moves back down also lands",
      liveFs:GetText():find("(605)", 1, true) ~= nil
        and suffixCount(liveFs:GetText()) == 1,
      show(liveFs:GetText()))

-- ==================================================================
section("4. REGRESSION: /ros set must report trailing garbage, and list in order")
-- "^(%S+)%s*(%S*)$" simply does not match three or more tokens, so
-- "/ros set staleDays 14 x" produced a nil key, fell into the no-argument
-- branch, and printed the entire options table -- which reads exactly like
-- the command having worked. Separately, the listing iterated pairs(), so
-- the same command produced a different order every session.
-- ==================================================================
local cmd = newAddon({ [KEY] = 620 })
local ros = cmd.env.SlashCmdList["ROSTOOLS"]
check("the slash handler registered (test is not vacuous)", type(ros) == "function")

local function run(line)
  local n = #cmd.said
  ros(line)
  local out = {}
  for i = n + 1, #cmd.said do out[#out + 1] = cmd.said[i] end
  return table.concat(out, "\n")
end

local garbage = run("set staleDays 14 x")
check("TRAILING GARBAGE: reported as an error, not silently accepted",
      garbage:find("too many arguments", 1, true) ~= nil, show(garbage))
check("and it did NOT print the options list instead",
      garbage:find("options:", 1, true) == nil, show(garbage))
check("and the value was not applied", cmd.ns.db.staleDays == 14)

check("an unknown option is still rejected",
      run("set nosuchoption"):find("unknown option", 1, true) ~= nil)
check("a valid set still works", run("set staleDays 30"):find("staleDays = 30", 1, true) ~= nil
      and cmd.ns.db.staleDays == 30)
check("a bare boolean toggle still works",
      run("set showDelta"):find("showDelta = true", 1, true) ~= nil)

-- The listing: every option present, in sorted order, both times.
local listed = run("set")
local seenOrder = {}
for name in listed:gmatch("|cff00ff88(%a+)%s") do seenOrder[#seenOrder + 1] = name end
local expected = {}
for name in pairs(cmd.ns.DEFAULTS) do expected[#expected + 1] = name end
table.sort(expected)
check("the listing names every option (test is not vacuous)",
      #seenOrder == #expected, ("listed %d of %d"):format(#seenOrder, #expected))
local sorted = #seenOrder == #expected
for i = 1, #seenOrder do
  if seenOrder[i] ~= expected[i] then sorted = false end
end
check("SORTED: the options list is in a stable, alphabetical order", sorted,
      table.concat(seenOrder, ", "))

-- "Two independent instances agree on the order" was a test of nothing.
-- Lua 5.1 -- the interpreter WoW actually ships -- does not randomize string
-- hashes, so two identical tables always iterate identically, with or
-- without the sort. (Seed randomization arrived in 5.2; the code comment
-- claiming the order is "reseeded per session" was simply false, and is
-- fixed in Commands.lua alongside this.)
--
-- What makes the sort load-bearing is the thing that IS observable here:
-- pairs() order is not alphabetical. Assert that first -- if it ever were,
-- the SORTED check above would be free and this whole section would need a
-- different design -- and then that the listing matches sorted order rather
-- than raw pairs() order.
local rawOrder = {}
for name in pairs(cmd.ns.DEFAULTS) do rawOrder[#rawOrder + 1] = name end
local pairsIsSorted = #rawOrder == #expected
for i = 1, #rawOrder do
  if rawOrder[i] ~= expected[i] then pairsIsSorted = false end
end
check("PREMISE: pairs() order is not already alphabetical (the sort does work)",
      not pairsIsSorted, table.concat(rawOrder, ", "))
local matchesRawOrder = #seenOrder == #rawOrder
for i = 1, #seenOrder do
  if seenOrder[i] ~= rawOrder[i] then matchesRawOrder = false end
end
check("and the listing follows sorted order, not the table's own iteration order",
      sorted and not matchesRawOrder, table.concat(seenOrder, ", "))

-- ==================================================================
section("5. REGRESSION: the browser's status line must describe the rows shown")
-- "%d shown" came from the filtered dataset while median and max came from
-- ns.Data:Stats() over the WHOLE roster, so a filtered list routinely
-- advertised a maximum that was not among the rows underneath it.
-- ==================================================================
local browserData = {}
for i = 1, 20 do browserData[("Toon%03d-khadgar"):format(i)] = 299 + i end  -- 300..319
browserData["Bigshot-khadgar"] = 700                                       -- excluded by the filter
local br = newAddon(browserData)
br.ns:GetModule("Browser"):Show()

local status = br.created[1].status   -- created[1] is the browser's own frame
check("the browser built and rendered (test is not vacuous)",
      status ~= nil and status:GetText():find("21 shown", 1, true) ~= nil,
      show(status and status:GetText()))

-- Now filter down to the 20 toons, leaving the 700 out of the list.
local search = br.widget("EditBox")
check("found the search box (test is not vacuous)", search ~= nil)
search:SetText("toon")
search.scripts.OnTextChanged(search)

local filtered = status:GetText()
check("the shown count is the filtered count",
      filtered:find("20 shown", 1, true) == 1, show(filtered))
check("FILTERED MAX: describes the rows on screen, not the whole roster",
      filtered:find("max 319", 1, true) ~= nil and filtered:find("700", 1, true) == nil,
      show(filtered))
check("FILTERED MEDIAN: likewise",
      filtered:find("median 309", 1, true) ~= nil, show(filtered))

-- A filter that matches nothing must say so, not pair "0 shown" with the
-- roster's median and max.
search:SetText("zzzznothing")
search.scripts.OnTextChanged(search)
local empty = status:GetText()
check("an empty result set says so instead of quoting roster-wide numbers",
      empty:find("No match", 1, true) ~= nil and empty:find("median", 1, true) == nil,
      show(empty))

-- ==================================================================
section("6. degenerate rows are a no-op, and are SEEN to be one")
-- Asserting `ok` from a pcall around these calls could not fail: the
-- production hook already wraps annotate() in its own pcall (Roster.lua),
-- so a throw is swallowed before it reaches here. Proof: error("boom") as
-- annotate()'s first line left that assertion passing while eight others
-- failed.
--
-- The hook does say something when it swallows one -- ns.Debug("roster
-- annotate failed: ...") -- so run with debug on and assert on the output:
-- no failure line, and the lines each degenerate path is supposed to print.
-- ==================================================================
local d = newAddon({ [KEY] = 620 }, { debug = true })
local ok, err = pcall(function()
  d.mixin.UpdateNameFrame(newEntry(nil, {}))                        -- pooled row
  d.mixin.UpdateNameFrame(newEntry({ name = "" }, {}))              -- blank name
  d.mixin.UpdateNameFrame(newEntry({ name = "Nobody-Khadgar" }, {}))-- no data
  d.mixin.UpdateNameFrame(newEntry(MEMBER, {}))                     -- no font strings
end)
check("no error escaped the hook's own pcall either", ok, tostring(err))
check("NOT SWALLOWED: the hook did not report an annotate failure",
      not d.saidHas("annotate failed"), table.concat(d.said, " | "))
check("the pooled row was recognized as one (test is not vacuous)",
      d.saidHas("no memberInfo"), table.concat(d.said, " | "))
check("the blank name was recognized as one",
      d.saidHas("missing/blank"), table.concat(d.said, " | "))
check("the member with no data was reported by key",
      d.saidHas("no data entry for Nobody-khadgar"), table.concat(d.said, " | "))
check("and the row with no font strings was reported as unmatchable",
      d.saidHas("no name fontstring for Peidae-Khadgar"), table.concat(d.said, " | "))

-- ==================================================================
section("7. REGRESSION: the strip must not steal a row, or eat Blizzard's text")
-- Two halves of the same mistake. The strip was unanchored, global, and
-- matched ANY " |c<8 hex>(digits)|r" -- not only the ones this addon wrote.
--
--   A. matchKey() ran it first, so a note column reading
--      "<member> |cffffff00(2)|r" collapsed to exactly the member's name,
--      scored rank 1, short-circuited the search and took the annotation
--      away from the real name row -- permanently, because the memo then
--      pinned st.fs/st.key/st.ilvl to the wrong widget.
--   B. annotate() ran it on the row it had chosen, so Blizzard's own
--      trailing "(3)" on an alt-grouped row -- the case the pattern is
--      unanchored FOR -- was deleted on the pass that annotated it.
-- ==================================================================
local imp = newAddon({ [KEY] = 620 })
local noteCol  = newFontString("Peidae-Khadgar |cffffff00(2)|r")   -- Blizzard's note count
local realName = newFontString("Peidae-Khadgar")
-- Note column first: ties break on position, so this is the order that
-- exposes a rank the impostor should never have had.
imp.mixin.UpdateNameFrame(newEntry(MEMBER, { noteCol, realName }, { nameFs = realName }))
check("IMPOSTOR: a note ending in a colored number is left alone",
      noteCol:GetText() == "Peidae-Khadgar |cffffff00(2)|r", show(noteCol:GetText()))
check("and the item level went onto the row actually showing the name",
      occurrences(realName:GetText(), "|cff1eff00(620)|r") == 1, show(realName:GetText()))

-- Same shape, but Blizzard does NOT rewrite the name this pass, so the row
-- arrives already annotated and both candidates carry a colored number.
local imp2 = newAddon({ [KEY] = 620 })
local noteCol2 = newFontString("Peidae-Khadgar |cffffff00(2)|r")
local annotated = newFontString("Peidae-Khadgar |cff1eff00(620)|r")
imp2.ns.Data:ApplyLiveUpdate(KEY, 640)   -- forces a re-render, so the memo can't hide the choice
imp2.mixin.UpdateNameFrame(newEntry(MEMBER, { noteCol2, annotated }))
check("IMPOSTOR (row already annotated, Blizzard silent): the note is still left alone",
      noteCol2:GetText() == "Peidae-Khadgar |cffffff00(2)|r", show(noteCol2:GetText()))
check("and the annotated name row is the one that got the new number",
      occurrences(annotated:GetText(), "|cff1eff00(640)|r") == 1
        and annotated:GetText():find("620", 1, true) == nil, show(annotated:GetText()))

-- B. Blizzard's own trailing text, on the alt-grouped rows the unanchoring
-- exists for.
local alt = newAddon({ [KEY] = 620 })
local altFs = newFontString("Peidae-Khadgar |cff808080(3)|r")
local altEntry = newEntry(MEMBER, { altFs })
alt.mixin.UpdateNameFrame(altEntry)
check("BLIZZARD TEXT: its own trailing (3) survives the annotation",
      occurrences(altFs:GetText(), "|cff808080(3)|r") == 1, show(altFs:GetText()))
check("and ours is appended after it, once",
      occurrences(altFs:GetText(), "|cff1eff00(620)|r") == 1
        and suffixCount(altFs:GetText()) == 2, show(altFs:GetText()))

local settled = altFs:GetText()
for _ = 1, 20 do alt.mixin.UpdateNameFrame(altEntry) end
check("and refreshing twenty times changes nothing at all",
      altFs:GetText() == settled, show(altFs:GetText()))

-- The documented worst case: Blizzard appends AFTER us, putting our suffix
-- mid-string, and then the item level moves.
altFs:SetText(altFs:GetText() .. " |cff808080(4)|r")
alt.ns.Data:ApplyLiveUpdate(KEY, 640)
alt.mixin.UpdateNameFrame(altEntry)
check("MID-STRING: our stale suffix is replaced, not stacked",
      occurrences(altFs:GetText(), "|cff1eff00(640)|r") == 1
        and altFs:GetText():find("620", 1, true) == nil, show(altFs:GetText()))
check("and BOTH pieces of Blizzard's text are still there",
      occurrences(altFs:GetText(), "|cff808080(3)|r") == 1
        and occurrences(altFs:GetText(), "|cff808080(4)|r") == 1, show(altFs:GetText()))

-- The recursion, and the rank-1 short-circuit that can skip it: the real
-- name lives in a nested child frame, and the parent's only candidate is the
-- impostor note. A rank-1 score on the note returns before the children are
-- ever walked.
local nest = newAddon({ [KEY] = 620 })
local parentNote = newFontString("Peidae-Khadgar |cffffff00(2)|r")
local deepName   = newFontString("Peidae-Khadgar")
local grandchild = newSubFrame({ deepName })
local child      = newSubFrame({ newFontString("Rank: Officer") }, { grandchild })
nest.mixin.UpdateNameFrame(newEntry(MEMBER, { parentNote }, { children = { child } }))
check("RECURSION: a name two frames down is found and annotated",
      occurrences(deepName:GetText(), "|cff1eff00(620)|r") == 1, show(deepName:GetText()))
check("and the parent's note did not short-circuit the walk",
      parentNote:GetText() == "Peidae-Khadgar |cffffff00(2)|r", show(parentNote:GetText()))

-- ==================================================================
section("8. REGRESSION: /ros reload and /ros sync count the LOCAL table")
-- Count() became the WIRE number (what Export() serializes, legacy backfill
-- excluded) and four surfaces that describe the local lookup table were
-- changed with it. On a client whose entries are all pre-2.0 leftovers the
-- login line read "loaded -- 0 entries" while every tooltip worked.
-- ==================================================================
local lg = newAddon({ [KEY] = 620 },
                    { legacy = { ["Ghost-khadgar"] = 404, ["Ghost2-khadgar"] = 405 } })
check("the legacy entries really are in the lookup table (test is not vacuous)",
      lg.ns.Data:GetByKey("Ghost-khadgar") == 404 and lg.ns.Data:GetByKey(KEY) == 620)

local rosLg = lg.env.SlashCmdList["ROSTOOLS"]
local function runLg(line)
  local n = #lg.said
  rosLg(line)
  local out = {}
  for i = n + 1, #lg.said do out[#out + 1] = lg.said[i] end
  return table.concat(out, "\n")
end

check("RELOAD COUNT: /ros reload counts every entry the client answers from",
      plain(runLg("reload")):find("rebuilt -- 3 entries", 1, true) ~= nil,
      show(runLg("reload")))
check("SYNC SOURCE LINE: so does /ros sync",
      plain(runLg("sync")):find("source: shipped file -- 3 entries", 1, true) ~= nil,
      show(runLg("sync")))
check("and the wire count is still the smaller, legacy-free number",
      lg.ns.Data:Count() == 3 and lg.ns.Data:ShareableCount() == 1,
      ("Count()=%d ShareableCount()=%d"):format(lg.ns.Data:Count(), lg.ns.Data:ShareableCount()))

-- ==================================================================
section("9. the browser below the top of the list, and with nothing in it")
-- FauxScrollFrame_GetOffset() was pinned to 0, so refresh()'s row/offset
-- arithmetic was only ever tested against the first screenful.
-- ==================================================================
local sc = newAddon(browserData)
sc.ns:GetModule("Browser"):Show()
local scRows = sc.rows()
check("the browser built its rows (test is not vacuous)", #scRows == 20, #scRows .. " rows")
check("at the top of the list, row 1 is the highest item level",
      rawget(scRows[1], "name"):GetText() == "Bigshot",
      show(rawget(scRows[1], "name"):GetText()))

sc.setScrollOffset(5)
sc.created[1]:Show()   -- OnShow -> rebuildDataset + refresh, now with the offset
check("SCROLLED: row 1 shows the entry five rows down, not the first",
      rawget(scRows[1], "name"):GetText() == "Toon016",
      show(rawget(scRows[1], "name"):GetText()))
check("and its item level came from the same entry",
      rawget(scRows[1], "ilvl"):GetText():find("315", 1, true) ~= nil,
      show(rawget(scRows[1], "ilvl"):GetText()))
-- 21 entries, offset 5: row 16 is the last one with an entry behind it, and
-- the four after it must be hidden rather than left showing stale text.
check("the last row with data shows the last entry",
      rawget(scRows[16], "name"):GetText() == "Toon001",
      show(rawget(scRows[16], "name"):GetText()))
check("and the rows past the end of the data are hidden",
      scRows[17]:IsShown() == false and scRows[20]:IsShown() == false)

local none = newAddon({})
none.ns:GetModule("Browser"):Show()
local noneStatus = none.created[1].status
check("EMPTY ROSTER: the status line says so instead of quoting statistics",
      noneStatus:GetText() == "No data loaded.", show(noneStatus:GetText()))
check("and no row is left showing", none.rows()[1]:IsShown() == false)

-- ==================================================================
section("10. secret values (12.0) must not reach the string helpers")
-- Blizzard hands tainted code secret strings that may be stored and passed
-- but not indexed, compared, or used as a table key. `type()` still reports
-- "string", so every pre-existing type check is NOT a guard. The tooltip
-- crash of 2026-09-01 is covered in Tools/tooltip-checks.lua; these are the
-- other paths a secret can enter by.
-- ==================================================================
-- Roster.lua pcall-wraps its own annotate(), so a secret that reaches the
-- string helpers there does NOT surface as a Lua error -- it is swallowed and
-- logged. Debug is therefore on for this section, and "annotate failed" in the
-- log is the failure signal that a thrown error would otherwise hide.
local OTHER_KEY = "Zulgar-khadgar"
local sec = newAddon({ [KEY] = 620, [OTHER_KEY] = 615 }, { debug = true })
local U = sec.ns.Util
local function noSwallowedError(label)
  check(label, not sec.saidHas("annotate failed"))
end

check("Util.IsSecret sees through the type check",
      U.IsSecret(markSecret("Peidae-Khadgar!secret")) == true
      and U.IsSecret("Peidae") == false)

-- These all sit one line above a comparison against "", which is itself an
-- error on a secret. The fixture is a real string, so ONLY the IsSecret guard
-- can produce nil here -- the type check cannot.
check("MakeKey refuses a secret name",
      U.MakeKey(markSecret("Nameish!secret"), "khadgar") == nil)
check("MakeKey ignores a secret realm rather than keying on it",
      U.MakeKey("Peidae", markSecret("realmish!secret")) == "Peidae")
check("NormalizeKey refuses a secret",
      U.NormalizeKey(markSecret("Peidae-Moon Guard!secret"), "khadgar") == nil)
check("RealmToSlug refuses a secret",
      U.RealmToSlug(markSecret("Moon Guard!secret")) == nil)

-- Guild roster names (Core/Sync.lua) and addon-message senders (Core/Comm.lua)
-- both reach NormalizeKey directly; the guard above is what covers them.
check("a secret sender name yields no key rather than a bogus one",
      U.NormalizeKey(markSecret("Impostor-Khadgar!secret"), "khadgar") == nil)

-- memberInfo.name off a Communities row. A real string that resolves to a
-- member we DO have data for, so an unguarded build annotates the row from a
-- value it is not allowed to read -- which is what this catches.
local zulgarFs = newFontString("Zulgar-Khadgar")
local secretRow = newEntry({ name = markSecret("Zulgar-Khadgar") }, { zulgarFs })
sec.mixin.UpdateNameFrame(secretRow)
check("a secret memberInfo.name annotates nothing",
      suffixCount(zulgarFs:GetText()) == 0, show(zulgarFs:GetText()))

-- From here the fixture is the strict one: values the code must never touch.
--
-- findNameFontString reads EVERY region on the row, so one region another
-- addon has put a secret on must not take the row down with it.
local poisoned = newFontString("Peidae-Khadgar")
local decoy = newFontString(newSecret("someone else's fontstring"))
sec.mixin.UpdateNameFrame(newEntry(MEMBER, { decoy, poisoned }))
check("a secret region does not stop the walk: the name row is annotated",
      suffixCount(poisoned:GetText()) == 1, show(poisoned:GetText()))
noSwallowedError("and the walk did not throw")

-- The name row itself holding a secret: no readable base to append to, so the
-- only correct move is to leave it alone. Annotate it normally first, so the
-- memo's cheap-exit comparison (st.fs:GetText() == st.text) is the code that
-- meets the secret rather than the fresh path.
local nameFs = newFontString("Peidae-Khadgar")
local nameRow = newEntry(MEMBER, { nameFs })
sec.mixin.UpdateNameFrame(nameRow)
check("baseline: the row was annotated", suffixCount(nameFs:GetText()) == 1,
      show(nameFs:GetText()))
nameFs:SetText(newSecret("name row, rewritten"))
sec.mixin.UpdateNameFrame(nameRow)
noSwallowedError("a name row rewritten with a secret does not throw")
check("and the secret was left on the widget untouched",
      U.IsSecret(nameFs:GetText()) == true)

print(("\n== %d passed, %d failed ==\n"):format(pass, fail))
os.exit(fail == 0 and 0 or 1)
