-- RoS-Tools/Core/Data.lua
-- Read side of the guild item level table.

local _, ns = ...

local Data = {}
ns.Data = Data

local ilvls   = {}   -- ["Name-realm-slug"] = ilvl
local lowered = {}   -- lowercased key -> canonical key (for /ros search)
local meta    = {}
local count   = 0
local built   = false

-- Live overlay, fed by Core/Comm.lua over the addon-message channel.
-- In-memory only, session-scoped -- gone on /reload, never written to
-- RoSToolsDB or any file. Mirrors ilvls/lowered so lookups can treat it
-- the same way.
local liveOverlay       = {}   -- ["Name-realm-slug"] = ilvl
local liveOverlayLowered = {}  -- lowercased key -> canonical key

-- ------------------------------------------------------------------
-- Build
-- ------------------------------------------------------------------
function Data:Build()
  wipe(ilvls)
  wipe(lowered)
  wipe(meta)
  count = 0
  -- liveOverlay is NOT wiped here. Build() only ever fires once, at
  -- ADDON_LOADED, before Comm.lua has received anything, so this is moot
  -- today -- but if Build() is ever called again mid-session (e.g. a
  -- future "/ros reload"), wiping live data an online guildmate just sent
  -- would be actively wrong.

  local source = ns.GuildData
  if type(source) == "table" then
    if type(source.meta) == "table" then
      for k, v in pairs(source.meta) do meta[k] = v end
    end
    if type(source.ilvls) == "table" then
      for k, v in pairs(source.ilvls) do
        local n = tonumber(v)
        if type(k) == "string" and n then
          ilvls[k] = math.floor(n)
          lowered[k:lower()] = k
          count = count + 1
        end
      end
    end
  end

  -- Backwards compatibility with the pre-2.0 flat globals, in case an
  -- old Riddled_Data.lua is still sitting in the addon folder.
  if type(RiddledTooltip_DB) == "table" then
    for k, v in pairs(RiddledTooltip_DB) do
      local n = tonumber(v)
      if type(k) == "string" and n and ilvls[k] == nil then
        ilvls[k] = math.floor(n)
        lowered[k:lower()] = k
        count = count + 1
      end
    end
    if type(RiddledTooltip_Meta) == "table" and not meta.generated_at then
      for k, v in pairs(RiddledTooltip_Meta) do meta[k] = v end
    end
  end

  built = true
  ns.Debug(("data built: %d entries"):format(count))
  return count
end

-- ------------------------------------------------------------------
-- Live overlay -- fed by Core/Comm.lua, read by every query below.
-- ------------------------------------------------------------------

--- Record a live ilvl update received over addon comm. In-memory only --
--- never touches RoSToolsDB or the static ilvls table. The next
--- ADDON_LOADED rebuild is what "resets" it, same as everything else here.
function Data:ApplyLiveUpdate(key, ilvl)
  if type(key) ~= "string" or key == "" then return end
  local n = tonumber(ilvl)
  if not n then return end
  n = math.floor(n)
  liveOverlay[key] = n
  liveOverlayLowered[key:lower()] = key
end

--- True if `key`'s current value comes from the live overlay rather than
--- the static export. Not surfaced in the UI yet -- follow-up for
--- Tooltip.lua/Roster.lua to flag a number as "live".
function Data:IsLive(key)
  if type(key) ~= "string" then return false end
  local canonical = liveOverlay[key] ~= nil and key or liveOverlayLowered[key:lower()]
  return canonical ~= nil and liveOverlay[canonical] ~= nil
end

--- Merge the live overlay over the static table into one flat array of
--- {key=, ilvl=} entries. Overlay entries win on key collision and are
--- included even when they have no static counterpart (there won't be any
--- in practice, since Comm only ever hears from existing guild members,
--- but this doesn't assume that).
local function mergedEntries()
  local seen = {}
  local results = {}
  for key, ilvl in pairs(liveOverlay) do
    results[#results + 1] = { key = key, ilvl = ilvl }
    seen[key] = true
  end
  for key, ilvl in pairs(ilvls) do
    if not seen[key] then
      results[#results + 1] = { key = key, ilvl = ilvl }
    end
  end
  return results
end

local function ensureBuilt()
  if not built then Data:Build() end
end

-- ------------------------------------------------------------------
-- Queries
-- ------------------------------------------------------------------
function Data:Count()
  ensureBuilt()
  return count
end

function Data:Meta()
  ensureBuilt()
  return meta
end

--- The export instant as a UTC epoch, or nil on a schema 2 file that
--- predates it.
function Data:GeneratedEpoch()
  ensureBuilt()
  return tonumber(meta.generated_epoch)
end

--- The export time formatted for display, in the *viewer's* local zone.
--- Schema 3 carries a UTC epoch, so this is exact no matter where the
--- exporter ran. Schema 2 only has a bare wall-clock string with no offset
--- recorded; there is nothing to convert it from, so it is shown verbatim.
function Data:GeneratedAt()
  ensureBuilt()
  local epoch = self:GeneratedEpoch()
  if epoch then
    return date("%Y-%m-%d %H:%M:%S", epoch)
  end
  return meta.generated_at
end

--- Whole days since the export. Epoch arithmetic when we have an epoch --
--- both sides are UTC and no zone enters into it.
function Data:AgeInDays()
  local epoch = self:GeneratedEpoch()
  if epoch then
    return ns.Util.DaysSinceEpoch(epoch)
  end
  return ns.Util.DaysSince(meta.generated_at)
end

function Data:IsStale()
  local days = self:AgeInDays()
  if not days or not ns.db then return false end
  return days >= (ns.db.staleDays or 14)
end

--- Look up by exact key ("Name-realm-slug"). The live overlay -- if
--- Comm.lua has heard from this key this session -- wins over the static
--- export.
function Data:GetByKey(key)
  ensureBuilt()
  if type(key) ~= "string" then return nil end
  local overlayKey = liveOverlay[key] ~= nil and key or liveOverlayLowered[key:lower()]
  if overlayKey and liveOverlay[overlayKey] ~= nil then
    return liveOverlay[overlayKey]
  end
  return ilvls[key] or ilvls[lowered[key:lower()] or ""]
end

--- Look up by name plus realm display name (realm may be nil/"").
function Data:Get(name, realm)
  local slug = ns.Util.RealmToSlug(realm) or ns.playerRealmSlug
  return self:GetByKey(ns.Util.MakeKey(name, slug))
end

--- Look up by unit token. Returns ilvl, key.
function Data:GetForUnit(unit)
  if not unit or not UnitExists(unit) or not UnitIsPlayer(unit) then return nil end
  local name, realm = UnitName(unit)
  if not name or name == "" then return nil end
  local slug = (realm and realm ~= "" and ns.Util.RealmToSlug(realm)) or ns.playerRealmSlug
  local key = ns.Util.MakeKey(name, slug)
  return self:GetByKey(key), key
end

--- Look up by GUID, which is what the modern tooltip API hands us.
function Data:GetForGUID(guid)
  if type(guid) ~= "string" then return nil end
  local _, _, _, _, _, name, realm = GetPlayerInfoByGUID(guid)
  if not name or name == "" then return nil end
  local slug = (realm and realm ~= "" and ns.Util.RealmToSlug(realm)) or ns.playerRealmSlug
  local key = ns.Util.MakeKey(name, slug)
  return self:GetByKey(key), key
end

--- Substring search over keys (live overlay merged over the static
--- table). Returns a sorted array of {key, ilvl}.
function Data:Find(needle, limit)
  ensureBuilt()
  needle = tostring(needle or ""):lower()
  local results = {}
  local entries = mergedEntries()
  for i = 1, #entries do
    local entry = entries[i]
    if entry.key:lower():find(needle, 1, true) then
      results[#results + 1] = entry
    end
  end
  table.sort(results, function(a, b)
    if a.ilvl ~= b.ilvl then return a.ilvl > b.ilvl end
    return a.key < b.key
  end)
  if limit and #results > limit then
    for i = #results, limit + 1, -1 do results[i] = nil end
  end
  return results
end

--- Top N by item level (live overlay merged over the static table).
function Data:Top(n)
  ensureBuilt()
  local results = mergedEntries()
  table.sort(results, function(a, b)
    if a.ilvl ~= b.ilvl then return a.ilvl > b.ilvl end
    return a.key < b.key
  end)
  n = n or 10
  for i = #results, n + 1, -1 do results[i] = nil end
  return results
end

--- Mean / median / min / max across the roster (live overlay merged
--- over the static table).
function Data:Stats()
  ensureBuilt()
  local entries = mergedEntries()
  if #entries == 0 then return nil end
  local values, sum = {}, 0
  for i = 1, #entries do
    values[i] = entries[i].ilvl
    sum = sum + entries[i].ilvl
  end
  table.sort(values)
  local mid = math.floor(#values / 2)
  local median = (#values % 2 == 1) and values[mid + 1]
                 or ((values[mid] + values[mid + 1]) / 2)
  return {
    count  = #values,
    mean   = sum / #values,
    median = median,
    min    = values[1],
    max    = values[#values],
  }
end

--- Your own equipped item level, for delta display.
function Data:PlayerIlvl()
  local _, equipped = GetAverageItemLevel()
  return equipped and math.floor(equipped) or nil
end
