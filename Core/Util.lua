-- RoS-Tools/Core/Util.lua
-- Realm slugging and key normalization.
--
-- The exporter keys every character as "Name-realm-slug", matching the
-- slug Blizzard's Community API uses. In game we only ever get the
-- display name ("Moon Guard", "Kel'Thuzad", "Area 52") or the tooltip's
-- space-stripped form ("MoonGuard", "Kel'Thuzad", "Area52"), so we have
-- to reconstruct the slug locally.

local _, ns = ...

local Util = {}
ns.Util = Util

-- U+2019 RIGHT SINGLE QUOTATION MARK. WoW runs Lua 5.1, which has no
-- \u{} escape, so the raw UTF-8 bytes are spelled out.
local RIGHT_SQUOTE = "\226\128\153"

-- ------------------------------------------------------------------
-- Realms whose slug cannot be derived mechanically. Keys are the
-- lowercased, punctuation-stripped, space-stripped display name.
-- ------------------------------------------------------------------
-- Only realms the CamelCase splitter below gets wrong belong here --
-- everything else derives cleanly. Add entries as you find them.
local SLUG_OVERRIDES = {
  ["sistersofelune"]  = "sisters-of-elune",   -- lowercase "of" breaks the split
  ["theventureco"]    = "the-venture-co",
  ["shatteredhand"]   = "shattered-hand",
  ["altarofstorms"]   = "altar-of-storms",
  ["heraldsofthenew"] = "heralds-of-the-new",
}

--- Convert a realm display name into its API slug.
--- @param realm string|nil
--- @return string|nil
function Util.RealmToSlug(realm)
  if type(realm) ~= "string" or realm == "" then return nil end

  -- Apostrophes are dropped in the slug AND suppress the word boundary:
  --   Kel'Thuzad -> kelthuzad   (NOT kel-thuzad)
  --   Mal'Ganis  -> malganis
  -- So lowercase whatever follows the apostrophe before stripping it,
  -- which stops the CamelCase splitter below from firing there.
  realm = realm:gsub("'(%a)", string.lower)
  realm = realm:gsub(RIGHT_SQUOTE .. "(%a)", string.lower)
  realm = realm:gsub("'", ""):gsub(RIGHT_SQUOTE, ""):gsub("`", "")

  local flat = realm:gsub("[%s%-]", ""):lower()
  if SLUG_OVERRIDES[flat] then
    return SLUG_OVERRIDES[flat]
  end

  -- Already spaced or hyphenated ("Moon Guard", "Azjol-Nerub").
  if realm:find("[%s%-]") then
    realm = realm:gsub("%s+", "-")
    realm = realm:gsub("%-+", "-")
    return realm:lower()
  end

  -- Space-stripped form from a tooltip ("MoonGuard", "Area52").
  realm = realm:gsub("(%l)(%u)", "%1-%2")
  realm = realm:gsub("(%a)(%d)", "%1-%2")
  realm = realm:gsub("(%d)(%a)", "%1-%2")
  realm = realm:gsub("%-+", "-")
  return realm:lower()
end

--- Build a lookup key from a character name and realm slug.
function Util.MakeKey(name, realmSlug)
  if type(name) ~= "string" or name == "" then return nil end
  if type(realmSlug) ~= "string" or realmSlug == "" then return name end
  return name .. "-" .. realmSlug
end

--- Split "Name-Realm" (or bare "Name") into a normalized key.
--- Falls back to the player's own realm when none is present.
function Util.NormalizeKey(text, fallbackRealmSlug)
  if type(text) ~= "string" or text == "" then return nil end

  -- Strip any leading color escape and trailing whitespace.
  text = text:gsub("|c%x%x%x%x%x%x%x%x", ""):gsub("|r", ""):gsub("^%s+", ""):gsub("%s+$", "")
  if text == "" then return nil end

  -- Split on the FIRST "-" *before* touching the title, because the title
  -- heuristic below is only safe on the name half. A realm display name can
  -- contain whitespace ("Moon Guard"), so applying it to the whole string
  -- took "Peidae-Moon Guard" apart as "Guard" and produced "Guard-khadgar".
  local name, realm = text:match("^([^%-]+)%-(.+)$")
  if not name then name = text end

  -- Drop a leading player title ("Brewmaster Peidae" -> "Peidae"). Character
  -- names never contain whitespace, so if the name half has a space in it
  -- (a displayed title prefix), the name is its last whitespace-delimited
  -- token. Tooltip headers are the caller that hits this; a mixin-driven
  -- name field never has a title baked in.
  --
  -- Trailing whitespace on the name half is tolerated ("Peidae -Moon Guard"
  -- splits into "Peidae " and "Moon Guard"), so the token is taken with
  -- "%s*$" after it. A bare "%S+$" simply failed to match such a half and
  -- fell through to the untrimmed string, producing a key with a space in
  -- it -- which no exported key ever has, so the lookup silently missed.
  --
  -- No empty check after this: `text` is non-empty and trimmed by the time
  -- we get here, so the name half always starts with a non-space character
  -- and neither the match nor the fallback can produce "". The guard that
  -- used to sit here could not fire.
  name = name:match("(%S+)%s*$") or name

  if not realm then
    return Util.MakeKey(name, fallbackRealmSlug)
  end

  return Util.MakeKey(name, Util.RealmToSlug(realm) or fallbackRealmSlug)
end

-- ------------------------------------------------------------------
-- Misc
-- ------------------------------------------------------------------

--- Parse "YYYY-MM-DD HH:MM:SS" into an epoch timestamp.
function Util.ParseTimestamp(stamp)
  if type(stamp) ~= "string" then return nil end
  local y, mo, d, h, mi, s = stamp:match("^(%d+)-(%d+)-(%d+)%s+(%d+):(%d+):(%d+)$")
  if not y then
    y, mo, d = stamp:match("^(%d+)-(%d+)-(%d+)$")
    h, mi, s = 0, 0, 0
  end
  if not y then return nil end
  return time({
    year = tonumber(y), month = tonumber(mo), day = tonumber(d),
    hour = tonumber(h), min = tonumber(mi), sec = tonumber(s),
  })
end

--- Whole days elapsed since a UTC epoch. Both sides are epochs, so this is
--- the timezone-proof path and the one schema 3 data uses.
function Util.DaysSinceEpoch(epoch)
  epoch = tonumber(epoch)
  if not epoch then return nil end
  local days = math.floor((time() - epoch) / 86400)
  if days < 0 then return 0 end
  return days
end

--- Whole days elapsed since a "YYYY-MM-DD HH:MM:SS" stamp.
---
--- Legacy path, for schema 2 data only. Such a stamp is bare wall clock with
--- no offset recorded, and `time(table)` reads it in the *client's* zone --
--- so an export made west of the player reads as up to a day in the future.
--- Clamped at 0, because there is no honest sub-day answer to recover and
--- "exported -1 days ago" is worse than "today". Schema 3 carries
--- `generated_epoch` and avoids the guesswork entirely.
function Util.DaysSince(stamp)
  local t = Util.ParseTimestamp(stamp)
  if not t then return nil end
  local days = math.floor((time() - t) / 86400)
  if days < 0 then return 0 end
  return days
end

--- Case-insensitive, accent-tolerant-ish comparison key for searching.
function Util.SearchKey(s)
  return (tostring(s or ""):lower())
end
