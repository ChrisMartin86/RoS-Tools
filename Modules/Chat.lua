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
  if collapse(label) ~= wantName .. wantRealm then return nil end

  local at = 0
  while true do
    at = label:find("-", at + 1, true)
    if not at then return nil end

    local tail = label:sub(at + 1)
    if collapse(tail) == wantRealm then
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

local function hookChatFrames()
  for i = 1, MAX_CHAT_FRAMES do
    local cf = _G["ChatFrame" .. i]
    if cf and not wrapped[cf] and type(cf.AddMessage) == "function" then
      wrapped[cf] = true
      local orig = cf.AddMessage
      -- pcall, and fall back to the original text: a bug in the strip must
      -- cost the realm suffix, never the message. This sits in front of
      -- every line the player reads, including other addons' output.
      cf.AddMessage = function(self, text, ...)
        local ok, out = pcall(ns.StripChatRealms, text)
        if not ok then out = text end
        return orig(self, out, ...)
      end
    end
  end
end

function Chat:OnEnable()
  hookChatFrames()

  if type(_G.FCF_OpenTemporaryWindow) == "function" then
    hooksecurefunc("FCF_OpenTemporaryWindow", hookChatFrames)
  end
end
