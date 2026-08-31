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

-- Forward declaration: the query helpers defined alongside Build() below
-- call this before it is defined further down.
local ensureBuilt

-- Live overlay, fed by Core/Comm.lua over the addon-message channel.
-- In-memory only, session-scoped -- gone on /reload, never written to
-- RoSToolsDB or any file. Mirrors ilvls/lowered so lookups can treat it
-- the same way.
local liveOverlay       = {}   -- ["Name-realm-slug"] = ilvl
local liveOverlayLowered = {}  -- lowercased key -> canonical key

-- Keys that came from the pre-2.0 flat globals rather than from a real
-- export. Tracked so Data:Export() can leave them out: they are local
-- archaeology, and sharing them would push members who left years ago onto
-- every client in the guild -- or, if they are realm-less pre-2.0 keys,
-- fail enough of the receiver's per-entry validation to make it reject the
-- whole snapshot.
local legacyKeys = {}

-- Which source Build() last chose: "file" (the shipped Data/GuildData.lua)
-- or "sync" (a snapshot adopted from a guildmate via Core/Sync.lua).
local sourceKind = "file"
local sourceInfo = {}

-- ------------------------------------------------------------------
-- Source selection
--
-- The shipped export and an adopted snapshot are both complete statements
-- of the roster at an instant, and generated_epoch orders them. The higher
-- epoch wins outright -- there is no entry-by-entry merge, because a newer
-- export is authoritative about *absence* too. Merging would resurrect
-- every departed member forever.
-- ------------------------------------------------------------------

--- The roster this client is expected to carry, from the shipped export's
--- meta. A snapshot claiming a different guild is discarded, never merged.
function Data:IdentityKey()
  local m = ns.GuildData and ns.GuildData.meta
  if type(m) ~= "table" then return nil end
  if not (m.region and m.realm and m.guild) then return nil end
  return ("%s/%s/%s"):format(m.region, m.realm, m.guild)
end

--- Pick the adopted snapshot if it beats the shipped file, and drop every
--- stored snapshot that doesn't. Keyed by identity so carrying more than
--- one roster later is additive rather than a SavedVariables migration --
--- but exactly one entry can ever survive this.
local function chooseSnapshot(shippedEpoch)
  local store = ns.db and ns.db.syncedData
  if type(store) ~= "table" then return nil end

  local id = Data:IdentityKey()
  -- Unknown identity means "we can't judge", not "throw it away". Without
  -- this, a Data/GuildData.lua that failed to load -- a syntax error, a
  -- missing .toc line -- would erase the only copy of an adopted roster on
  -- the next login, and nothing could put it back.
  if not id then return nil end

  local keep = nil

  for key, entry in pairs(store) do
    local ok = key == id
      and type(entry) == "table"
      and type(entry.ilvls) == "table"
      and (tonumber(entry.epoch) or 0) > shippedEpoch
    if ok then
      keep = entry
    else
      -- The file has caught up, or this is a roster we no longer carry.
      -- Either way it is dead weight in the SavedVariables file.
      store[key] = nil
    end
  end

  return keep
end

-- ------------------------------------------------------------------
-- Build
-- ------------------------------------------------------------------
function Data:Build()
  wipe(ilvls)
  wipe(lowered)
  wipe(meta)
  wipe(legacyKeys)
  count = 0
  -- liveOverlay is NOT wiped here. Build() only ever fires once, at
  -- ADDON_LOADED, before Comm.lua has received anything, so this is moot
  -- today -- but if Build() is ever called again mid-session (e.g. a
  -- future "/ros reload"), wiping live data an online guildmate just sent
  -- would be actively wrong.

  local shipped = ns.GuildData
  local shippedMeta = (type(shipped) == "table" and type(shipped.meta) == "table")
                      and shipped.meta or {}
  local shippedEpoch = tonumber(shippedMeta.generated_epoch) or 0

  local snapshot = chooseSnapshot(shippedEpoch)

  local entries
  if snapshot then
    sourceKind = "sync"
    sourceInfo = snapshot
    meta.generated_epoch = tonumber(snapshot.epoch)
    meta.generated_at    = date("%Y-%m-%d %H:%M:%S", tonumber(snapshot.epoch))
    meta.schema          = tonumber(snapshot.schema)
    meta.region          = shippedMeta.region
    meta.realm           = shippedMeta.realm
    meta.guild           = shippedMeta.guild
    entries = snapshot.ilvls
  else
    sourceKind = "file"
    sourceInfo = {}
    for k, v in pairs(shippedMeta) do meta[k] = v end
    entries = (type(shipped) == "table" and type(shipped.ilvls) == "table")
              and shipped.ilvls or nil
  end

  if type(entries) == "table" then
    for k, v in pairs(entries) do
      local n = tonumber(v)
      if type(k) == "string" and n then
        ilvls[k] = math.floor(n)
        lowered[k:lower()] = k
        count = count + 1
      end
    end
  end

  -- Backwards compatibility with the pre-2.0 flat globals, in case an
  -- old Riddled_Data.lua is still sitting in the addon folder.
  --
  -- Only when the shipped file is the source. Backfilling legacy keys into
  -- an adopted snapshot would resurrect departed members through the back
  -- door -- the same reason a snapshot replaces rather than merges.
  if sourceKind == "file" and type(RiddledTooltip_DB) == "table" then
    for k, v in pairs(RiddledTooltip_DB) do
      local n = tonumber(v)
      if type(k) == "string" and n and ilvls[k] == nil then
        ilvls[k] = math.floor(n)
        lowered[k:lower()] = k
        legacyKeys[k] = true
        count = count + 1
      end
    end
    -- Only the timestamp, never the identity fields. A legacy meta carrying
    -- its own region/realm/guild would otherwise win over the shipped
    -- export's, leaving Meta() describing one guild while IdentityKey()
    -- (which reads ns.GuildData directly) describes another -- so every
    -- snapshot this client served would fail the receiver's identity check,
    -- silently.
    if type(RiddledTooltip_Meta) == "table" and not meta.generated_at then
      meta.generated_at    = RiddledTooltip_Meta.generated_at
      meta.generated_epoch = meta.generated_epoch or RiddledTooltip_Meta.generated_epoch
    end
  end

  built = true
  ns.Debug(("data built: %d entries from %s"):format(count, sourceKind))
  return count
end

--- Where the current table came from: "file" or "sync", plus the
--- provenance fields on an adopted snapshot (from, receivedAt).
function Data:SourceInfo()
  ensureBuilt()
  return sourceKind, sourceInfo
end

--- Store a validated snapshot and rebuild from it. Validation lives in
--- Core/Sync.lua -- by the time it gets here the payload has already been
--- parsed, bounds-checked and identity-matched.
function Data:AdoptSnapshot(snapshot)
  if type(snapshot) ~= "table" or type(snapshot.ilvls) ~= "table" then return false end
  local id = self:IdentityKey()
  if not id or not ns.db then return false end

  if type(ns.db.syncedData) ~= "table" then ns.db.syncedData = {} end
  ns.db.syncedData[id] = snapshot
  self:Build()

  -- Build() re-runs the source selection, which drops any snapshot that
  -- doesn't actually beat the shipped file. Report what happened rather
  -- than what was attempted.
  return sourceKind == "sync"
end

--- Drop any adopted snapshot and fall back to the shipped file.
function Data:ForgetSnapshot()
  if not ns.db then return false end
  local had = type(ns.db.syncedData) == "table" and next(ns.db.syncedData) ~= nil
  ns.db.syncedData = nil
  if had then self:Build() end
  return had
end

--- Serialize the current authoritative table for Core/Sync.lua.
---
--- Deliberately excludes the live overlay. A snapshot is a statement about
--- one export instant; folding in per-session live values would produce a
--- payload claiming to be epoch N while containing numbers that were never
--- in export N, and that lie would then propagate.
function Data:Export()
  ensureBuilt()
  local epoch = tonumber(meta.generated_epoch)
  if not epoch or count == 0 then return nil end

  local parts = {
    ("H:%d:%s:%s:%s:%s;"):format(epoch, meta.region or "", meta.realm or "",
                                 meta.guild or "", tostring(meta.schema or "")),
  }
  local n = 0
  for key, ilvl in pairs(ilvls) do
    if not legacyKeys[key] then
      parts[#parts + 1] = ("%s=%d;"):format(key, ilvl)
      n = n + 1
    end
  end
  if n == 0 then return nil end
  return table.concat(parts), n
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

function ensureBuilt()
  if not built then Data:Build() end
end

-- ------------------------------------------------------------------
-- Queries
-- ------------------------------------------------------------------
--- Size of the built table -- the static/adopted source only, with the live
--- overlay excluded. That is deliberate and load-bearing: this number is
--- what Core/Sync.lua announces alongside its epoch, so it has to describe
--- the same thing Export() would serialize. Stats()/Top()/Find() merge the
--- overlay in and can therefore report one more than this.
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
