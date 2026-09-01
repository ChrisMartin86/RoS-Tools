-- RoS-Tools/Core/Comm.lua
-- Real-time-ish ilvl propagation between online guildmates over WoW's
-- addon-message channel. Complements, does not replace, the static
-- Data/GuildData.lua export -- this only makes currently-online players'
-- numbers live; full-roster freshness stays the Sidecar's job.
--
-- Not a ns:RegisterModule() citizen. Comm is infrastructure Data and the
-- UI modules read from, parallel to how Data.lua itself isn't a registered
-- module -- so it's wired directly into Core/Events.lua instead, the same
-- way Events.lua calls ns.LoadConfig() and ns.Data:Build() directly.
--
-- See ADDON-COMM-DESIGN.md at the repo root for the full spec (wire
-- protocol, trust model, why not Ace3).

local _, ns = ...

local Comm = {}
ns.Comm = Comm

-- Wire-format version, not the addon version -- bump this if the payload
-- shape ever changes incompatibly, so a mismatched old client just ignores
-- messages it doesn't recognize instead of misparsing them.
local COMM_PREFIX = "RoSTools1"

local BROADCAST_COOLDOWN = 60   -- seconds between this client's own broadcasts
local EQUIP_DEBOUNCE     = 2    -- seconds to coalesce PLAYER_EQUIPMENT_CHANGED bursts

-- Session-only state. Never SavedVariables -- "last broadcast" resets on
-- every login/reload same as the live overlay it feeds.
local lastBroadcastIlvl = nil
local lastBroadcastAt   = 0
local debouncePending   = false
local cooldownPending   = false

-- ------------------------------------------------------------------
-- Lifecycle -- called explicitly from Core/Events.lua, each call wrapped
-- in pcall there the same way Init.lua's dispatch() wraps module methods,
-- so a bug here degrades to "no live updates" and never breaks the rest
-- of the addon's ADDON_LOADED/PLAYER_LOGIN handling.
-- ------------------------------------------------------------------

--- ADDON_LOADED, after ns.LoadConfig() and ns.Data:Build() have run.
function Comm:OnInitialize()
  C_ChatInfo.RegisterAddonMessagePrefix(COMM_PREFIX)
  ns.frame:RegisterEvent("CHAT_MSG_ADDON")
end

--- PLAYER_LOGIN. ns.playerName / ns.playerRealmSlug are set by now.
--- Deliberately does NOT broadcast here -- that would be a guild-wide
--- message burst every login, exactly the spam this design avoids.
--- Broadcasting only ever happens on an actual ilvl change, below.
function Comm:OnEnable()
  ns.frame:RegisterEvent("PLAYER_EQUIPMENT_CHANGED")
end

-- ------------------------------------------------------------------
-- Sending
-- ------------------------------------------------------------------

local function broadcastIfChanged()
  debouncePending = false

  if not (ns.db and ns.db.commEnabled and ns.db.commBroadcast) then return end
  if not IsInGuild() then return end

  local ilvl = ns.Data:PlayerIlvl()
  if not ilvl then return end

  if lastBroadcastIlvl and math.abs(ilvl - lastBroadcastIlvl) < 1 then
    return -- no real change since our last broadcast this session
  end

  local now = GetTime()
  if lastBroadcastAt > 0 and (now - lastBroadcastAt) < BROADCAST_COOLDOWN then
    -- DEFER, never drop. PLAYER_EQUIPMENT_CHANGED is the only thing that
    -- brings us back here, so a real upgrade that lands inside the cooldown
    -- would otherwise never be broadcast at all -- the guild would keep
    -- seeing the pre-upgrade number for the rest of the session.
    local remaining = BROADCAST_COOLDOWN - (now - lastBroadcastAt)
    ns.Debug(("comm: deferred broadcast, %ds left on cooldown"):format(math.ceil(remaining)))
    -- C_Timer.After cannot be cancelled, so one retry at a time: a full gear
    -- swap inside the window would otherwise arm a timer per slot and fire a
    -- burst of identical sends the instant the cooldown lifts.
    if not cooldownPending then
      cooldownPending = true
      C_Timer.After(remaining + 0.1, function()
        cooldownPending = false
        broadcastIfChanged()
      end)
    end
    return
  end

  C_ChatInfo.SendAddonMessage(COMM_PREFIX, tostring(ilvl), "GUILD")
  lastBroadcastIlvl = ilvl
  lastBroadcastAt   = now
  ns.Debug(("comm: broadcast own ilvl %d"):format(ilvl))
end

--- PLAYER_EQUIPMENT_CHANGED fires once per slot; a full-set change would
--- otherwise fire this N times in a row. Coalesce into a single send.
function Comm:HandleEquipmentChanged()
  if not (ns.db and ns.db.commEnabled and ns.db.commBroadcast) then return end
  if debouncePending then return end
  debouncePending = true
  C_Timer.After(EQUIP_DEBOUNCE, broadcastIfChanged)
end

-- ------------------------------------------------------------------
-- Receiving
-- ------------------------------------------------------------------

--- CHAT_MSG_ADDON. The trust boundary lives here: the update key comes
--- from `sender`, which the client supplies and the message body cannot
--- influence -- never from anything the payload claims. That's what stops
--- a client from broadcasting fake numbers for someone else's name.
function Comm:HandleAddonMessage(prefix, message, channel, sender)
  if prefix ~= COMM_PREFIX or channel ~= "GUILD" then return end
  if not (ns.db and ns.db.commEnabled) then return end -- gate receive too, not just send

  local selfKey   = ns.Util.MakeKey(ns.playerName, ns.playerRealmSlug)
  local senderKey = ns.Util.NormalizeKey(sender, ns.playerRealmSlug)
  if not senderKey or senderKey == selfKey then return end -- ignore our own echo

  local ilvl = tonumber(message)
  if not ilvl or ilvl < 1 or ilvl > 999 then
    ns.Debug(("comm: rejected message from %s: %s"):format(tostring(sender), tostring(message)))
    return
  end

  ns.Data:ApplyLiveUpdate(senderKey, math.floor(ilvl))
  ns.Debug(("comm: accepted %s = %d"):format(senderKey, math.floor(ilvl)))
end
