-- Riddled/Core/Data.lua
-- Read side of the guild item level table.

local _, ns = ...

local Data = {}
ns.Data = Data

local ilvls   = {}   -- ["Name-realm-slug"] = ilvl
local lowered = {}   -- lowercased key -> canonical key (for /riddle search)
local meta    = {}
local count   = 0
local built   = false

-- ------------------------------------------------------------------
-- Build
-- ------------------------------------------------------------------
function Data:Build()
  wipe(ilvls)
  wipe(lowered)
  wipe(meta)
  count = 0

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

function Data:GeneratedAt()
  ensureBuilt()
  return meta.generated_at
end

function Data:AgeInDays()
  return ns.Util.DaysSince(self:GeneratedAt())
end

function Data:IsStale()
  local days = self:AgeInDays()
  if not days or not ns.db then return false end
  return days >= (ns.db.staleDays or 14)
end

--- Look up by exact key ("Name-realm-slug").
function Data:GetByKey(key)
  ensureBuilt()
  if type(key) ~= "string" then return nil end
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

--- Substring search over keys. Returns a sorted array of {key, ilvl}.
function Data:Find(needle, limit)
  ensureBuilt()
  needle = tostring(needle or ""):lower()
  local results = {}
  for key, ilvl in pairs(ilvls) do
    if key:lower():find(needle, 1, true) then
      results[#results + 1] = { key = key, ilvl = ilvl }
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

--- Top N by item level.
function Data:Top(n)
  ensureBuilt()
  local results = {}
  for key, ilvl in pairs(ilvls) do
    results[#results + 1] = { key = key, ilvl = ilvl }
  end
  table.sort(results, function(a, b)
    if a.ilvl ~= b.ilvl then return a.ilvl > b.ilvl end
    return a.key < b.key
  end)
  n = n or 10
  for i = #results, n + 1, -1 do results[i] = nil end
  return results
end

--- Mean / median / min / max across the roster.
function Data:Stats()
  ensureBuilt()
  local values, sum = {}, 0
  for _, ilvl in pairs(ilvls) do
    values[#values + 1] = ilvl
    sum = sum + ilvl
  end
  if #values == 0 then return nil end
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
