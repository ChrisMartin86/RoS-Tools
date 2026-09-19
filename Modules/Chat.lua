-- RoS-Tools/Modules/Chat.lua
-- Drops the "-Realm" suffix from player names in chat, on the same
-- `hideRealm` switch the Guild & Communities roster uses.
--
-- This edits the DISPLAY TEXT of a player hyperlink and nothing else. The
-- link payload -- "|Hplayer:Gheek-Stormrage:12345:GUILD|h" -- is copied
-- through untouched, so clicking the name still whispers the right
-- character on the right realm, and Modules/Tooltip.lua's chat-link tooltip
-- still resolves the key. That is the whole reason this is an AddMessage
-- wrapper rather than a ChatFrame_AddMessageEventFilter: a filter only sees
-- the author name, and the link is built from that author name AFTER the
-- filter runs, so rewriting it there rewrites the whisper target too.
--
-- Both surfaces are covered: the chat windows (ChatFrame1..N, including
-- whisper tabs opened mid-session) and the Guild & Communities window's own
-- chat pane, which is not a ChatFrame global and is found by walking that
-- frame for anything carrying an AddMessage.
--
-- Nothing is persisted and nothing is re-rendered: lines already printed
-- keep whatever they were printed with. Turning the setting off puts the
-- realm back on the NEXT message, not on the scrollback.

local _, ns = ...

local Chat = ns:RegisterModule("Chat")

-- Whisper and temporary tabs are created on demand at indices above
-- NUM_CHAT_WINDOWS, so the scan runs past the ten docked windows. Same
-- constant, same reason, as Modules/Tooltip.lua.
local MAX_CHAT_FRAMES = 50

-- U+2019 RIGHT SINGLE QUOTATION MARK, spelled out -- Lua 5.1 has no \u{}.
local RIGHT_SQUOTE = "\226\128\153"

-- ------------------------------------------------------------------
-- Escape handling
-- ------------------------------------------------------------------
-- Deliberately a local copy of what Modules/Roster.lua does rather than a
-- shared helper: Roster's versions carry invariants that belong to the
-- roster's prefix search (see prefixKey there), and folding the two
-- together would put this file's requirements on that code's critical path.

--- Drop every colour escape, both the |cAARRGGBB and the newer
--- |cnCOLOR_NAME: form retail now uses in the same places.
local function stripColors(text)
  text = text:gsub("|c%x%x%x%x%x%x%x%x", "")
  text = text:gsub("|c[nN]%u[%u%d_]*:", "")
  return (text:gsub("|r", ""))
end

--- Collapse a displayed string to letters and digits, so a comparison
--- survives the cosmetic spellings Blizzard uses for the same realm:
--- "Moon Guard" vs "MoonGuard", either apostrophe in "Kel'Thuzad", and a
--- realm that carries its own colour wrapper.
local function collapse(text)
  text = stripColors(text):gsub(RIGHT_SQUOTE, "")
  return (text:gsub("[%s%p]", ""):lower())
end

--- True when `s` is `unit` repeated one or more times and nothing else.
---
--- One repeat is the ordinary case. More than one is the Guild & Communities
--- chat pane on a cross-realm name, where the realm arrives stuttered --
--- "Epia-Antonidas-Antonidas-Antonidas-..." -- often enough to wrap three
--- lines. Nothing in this addon can produce that (every path here only ever
--- removes characters), so it is not ours to fix at the source; what we can
--- do is recognise the shape as "this character's realm, several times" and
--- take all of it off. A tail that is the realm plus anything else still
--- fails, and the label is left alone.
local function isRepeatOf(s, unit)
  if #unit == 0 or #s == 0 or #s % #unit ~= 0 then return false end
  for i = 1, #s, #unit do
    if s:sub(i, i + #unit - 1) ~= unit then return false end
  end
  return true
end

--- The colour escape starting at byte `i` of `s`, or nil. Both forms:
--- |cAARRGGBB, and the |cnCOLOR_NAME: form retail uses in the same places.
local function escapeAt(s, i)
  return s:match("^(|c%x%x%x%x%x%x%x%x)", i)
    or s:match("^(|c[nN]%u[%u%d_]*:)", i)
    or s:match("^(|r)", i)
end

--- Drop a colour that ends up wrapping nothing once the realm is gone, and
--- one left open at the very end of the label. Neither is cosmetic: an
--- unclosed |c bleeds its colour into the rest of the chat line.
local function dropEmptyColors(text)
  local prev
  repeat
    prev = text
    text = text:gsub("|c%x%x%x%x%x%x%x%x|r", "")
    text = text:gsub("|c[nN]%u[%u%d_]*:|r", "")
    text = text:gsub("|c%x%x%x%x%x%x%x%x$", "")
    text = text:gsub("|c[nN]%u[%u%d_]*:$", "")
  until text == prev
  return text
end

--- Everything in the text after the name that is NOT the realm: every
--- colour escape, wherever it sits, and whatever follows the realm's last
--- letter -- the closing bracket, most of the time.
---
--- Walked character by character rather than pattern-matched off the end,
--- because "|r" ENDS IN A LETTER. A search for the last alphanumeric
--- character lands inside it, and everything after that -- the closing
--- bracket -- is dropped. That is what put "[Gheek" on screen.
local function keepAroundRealm(tail)
  local last, i = 0, 1
  while i <= #tail do
    local esc = escapeAt(tail, i)
    if esc then
      i = i + #esc
    else
      if tail:sub(i, i):match("%w") then last = i end
      i = i + 1
    end
  end

  local out = {}
  i = 1
  while i <= #tail do
    local esc = escapeAt(tail, i)
    if esc then
      out[#out + 1] = esc
      i = i + #esc
    else
      if i > last then out[#out + 1] = tail:sub(i, i) end
      i = i + 1
    end
  end
  return table.concat(out)
end

--- Rewrite one link's visible label so the realm is gone.
--- @param label string the text between the link's two "|h" tags
--- @param name  string the character name, from the LINK PAYLOAD
--- @param realm string the realm, from the link payload
--- @return string|nil the new label, or nil to leave it exactly as it was
---
--- One guard carries the whole safety argument: the label, reduced to its
--- letters and digits, has to be exactly this character's name and realm.
--- Brackets, colour escapes, spaces and punctuation all collapse away, so
--- every spelling the client actually produces passes --
---   [Gheek-Stormrage]
---   |cff8787ed[Gheek-Stormrage]|r     (the class colour wraps the brackets)
---   [|cff8787edGheek-Stormrage|r]     (and the other way round)
---   Gheek|cff808080-Stormrage|r       (a separately dimmed realm)
---   Gheek-Moon Guard                  (against a "MoonGuard" payload)
--- -- while a label carrying text of its own ("Raid Leader Gheek-Stormrage",
--- another addon's decoration, an icon) does not, and is left alone. Cutting
--- a label we do not understand at a hyphen would delete whatever followed.
local function stripLabelRealm(label, name, realm)
  local wantName, wantRealm = collapse(name), collapse(realm)
  if wantName == "" or wantRealm == "" then return nil end

  local body = collapse(label)
  if body:sub(1, #wantName) ~= wantName then return nil end
  if not isRepeatOf(body:sub(#wantName + 1), wantRealm) then return nil end

  local at = 0
  while true do
    at = label:find("-", at + 1, true)
    if not at then return nil end

    local tail = label:sub(at + 1)
    if isRepeatOf(collapse(tail), wantRealm) then
      local head = label:sub(1, at - 1)
      if head == "" then return nil end
      return dropEmptyColors(head .. keepAroundRealm(tail))
    end
  end
end

--- Strip the realm from every player link in a chat line.
---
--- "|HBNplayer:" is not matched and must not be: a Battle.net link
--- identifies an account, not a character on a realm.
local function stripRealms(text)
  if not (ns.db and ns.db.hideRealm) then return text end
  -- A secret reports type() == "string", so the secret test comes first --
  -- find() on one raises where a type check waves it through.
  if ns.Util.IsSecret(text) then return text end
  if type(text) ~= "string" then return text end
  if not text:find("|Hplayer:", 1, true) then return text end

  return (text:gsub("(|Hplayer:)([^|]*)(|h)(.-)(|h)", function(head, data, mid, label, tail)
    -- The payload's first field is "Name-Realm". Character names cannot
    -- contain a hyphen, so the FIRST hyphen is the split -- which is what
    -- makes "Gheek-Azjol-Nerub" come apart correctly.
    local target = data:match("^([^:]*)")
    local name, realm = (target or ""):match("^([^%-]+)%-(.+)$")
    if not name then return nil end

    local stripped = stripLabelRealm(label, name, realm)
    if not stripped then return nil end
    return head .. data .. mid .. stripped .. tail
  end))
end

-- Published on the namespace, and called through it below: it is the seam
-- Tools/module-checks.lua swaps out to prove the pcall in the wrapper
-- actually falls back to the untouched line instead of eating the message.
ns.StripChatRealms = stripRealms

-- ------------------------------------------------------------------
-- Lifecycle
-- ------------------------------------------------------------------

-- Keyed by frame, weak, and checked before wrapping: FCF_OpenTemporaryWindow
-- makes the scan run again over frames that are already wrapped, and a
-- second wrapper on the same frame is a second pcall on every line printed
-- for the rest of the session.
local wrapped = setmetatable({}, { __mode = "k" })

local function wrapMessageFrame(cf)
  if not cf or wrapped[cf] or type(cf.AddMessage) ~= "function" then return end
  wrapped[cf] = true
  local orig = cf.AddMessage
  -- pcall, and fall back to the original text: a bug in the strip must cost
  -- the realm suffix, never the message. This sits in front of every line
  -- the player reads, including other addons' output.
  cf.AddMessage = function(self, text, ...)
    local ok, out = pcall(ns.StripChatRealms, text)
    if not ok then out = text end
    return orig(self, out, ...)
  end
end

-- How deep under the Communities frame to look for its message frame. Four
-- levels covers where it has sat across recent patches with room to spare,
-- and bounds the walk on a frame that has a lot of children.
local MAX_SEARCH_DEPTH = 4

--- Wrap every message frame under `frame`, by SHAPE -- anything carrying an
--- AddMessage -- rather than by a widget path.
---
--- The same rule Modules/Roster.lua follows for the member list, and for the
--- same reason: Blizzard rearranges the Communities UI across patches, and a
--- hard-coded path turns that into a broken addon instead of a no-op. Wrapping
--- by shape is safe because the wrapper passes anything without a player link
--- straight through.
local function wrapMessageFramesUnder(frame, depth)
  depth = depth or 0
  if not frame or depth > MAX_SEARCH_DEPTH then return end
  if type(frame.AddMessage) == "function" then wrapMessageFrame(frame) end
  if type(frame.GetChildren) ~= "function" then return end

  local kids = { frame:GetChildren() }
  for i = 1, #kids do
    wrapMessageFramesUnder(kids[i], depth + 1)
  end
end

local function hookChatFrames()
  for i = 1, MAX_CHAT_FRAMES do
    wrapMessageFrame(_G["ChatFrame" .. i])
  end
end

--- The Guild & Communities window: its own chat pane, which is NOT one of the
--- ChatFrame globals, so the scan above never reaches it.
local function hookCommunityFrames()
  local frame = _G.CommunitiesFrame
  if not frame then return false end

  wrapMessageFramesUnder(frame, 0)

  -- The pane can be built after the addon loads, so re-walk when the window
  -- opens. wrapMessageFrame is idempotent, so this costs a walk and nothing
  -- else.
  if not frame.rosToolsChatWalked and type(frame.HookScript) == "function" then
    frame.rosToolsChatWalked = true
    frame:HookScript("OnShow", function(self) wrapMessageFramesUnder(self, 0) end)
  end
  return true
end

function Chat:OnEnable()
  hookChatFrames()

  if type(_G.FCF_OpenTemporaryWindow) == "function" then
    hooksecurefunc("FCF_OpenTemporaryWindow", hookChatFrames)
  end

  -- Blizzard_Communities is load-on-demand. Same waiter Modules/Roster.lua
  -- uses, and no frame is created at all when it is already loaded.
  if hookCommunityFrames() then return end

  local waiter = CreateFrame("Frame")
  waiter:RegisterEvent("ADDON_LOADED")
  waiter:SetScript("OnEvent", function(self, _, addon)
    if addon == "Blizzard_Communities" and hookCommunityFrames() then
      self:UnregisterAllEvents()
      self:SetScript("OnEvent", nil)
    end
  end)
end
