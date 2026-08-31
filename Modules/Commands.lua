-- RoS-Tools/Modules/Commands.lua
-- /ros -- lookup, search, stats, options.

local _, ns = ...

local Commands = ns:RegisterModule("Commands")

local handlers = {}
local order    = {}

local function register(name, usage, description, fn)
  handlers[name] = { usage = usage, description = description, fn = fn }
  order[#order + 1] = name
end

local function formatEntry(entry)
  return ("  %s  %s%d%s"):format(
    entry.key, ns.ColorForIlvl(entry.ilvl), entry.ilvl, ns.COLOR.reset)
end

-- ------------------------------------------------------------------
-- Commands
-- ------------------------------------------------------------------
register("help", "", "Show this help", function()
  ns.Print(("v%s -- commands:"):format(ns.VERSION))
  for i = 1, #order do
    local name = order[i]
    local h = handlers[name]
    local cmd = ("/ros %s %s"):format(name, h.usage)
    DEFAULT_CHAT_FRAME:AddMessage(("  %s%-28s%s %s"):format(
      ns.COLOR.brand, cmd:gsub("%s+$", ""), ns.COLOR.reset, h.description))
  end
end)

register("list", "", "Open the roster browser window", function()
  ns:GetModule("Browser"):Toggle()
end)

register("who", "<name>", "Look up one character", function(args)
  if not args or args == "" then
    ns.Warn("usage: /ros who <name>")
    return
  end

  local key  = ns.Util.NormalizeKey(args, ns.playerRealmSlug)
  local ilvl = key and ns.Data:GetByKey(key)

  if ilvl then
    ns.Print(("%s is %s%d%s"):format(key, ns.ColorForIlvl(ilvl), ilvl, ns.COLOR.reset))
    return
  end

  local matches = ns.Data:Find(args, 8)
  if #matches == 0 then
    ns.Warn(("no entry for '%s'"):format(args))
    return
  end

  ns.Print(("no exact match for '%s' -- did you mean:"):format(args))
  for i = 1, #matches do
    DEFAULT_CHAT_FRAME:AddMessage(formatEntry(matches[i]))
  end
end)

register("find", "<text>", "Search names and realms", function(args)
  if not args or args == "" then
    ns.Warn("usage: /ros find <text>")
    return
  end
  local matches = ns.Data:Find(args, 20)
  if #matches == 0 then
    ns.Warn(("nothing matching '%s'"):format(args))
    return
  end
  ns.Print(("%d match%s for '%s':"):format(#matches, #matches == 1 and "" or "es", args))
  for i = 1, #matches do
    DEFAULT_CHAT_FRAME:AddMessage(formatEntry(matches[i]))
  end
end)

register("top", "[n]", "Highest item levels (default 10)", function(args)
  local n = tonumber(args) or 10
  n = math.max(1, math.min(n, 50))
  local list = ns.Data:Top(n)
  if #list == 0 then
    ns.Warn("no data loaded")
    return
  end
  ns.Print(("top %d:"):format(#list))
  for i = 1, #list do
    DEFAULT_CHAT_FRAME:AddMessage(("  %2d."):format(i) .. formatEntry(list[i]))
  end
end)

register("stats", "", "Roster item level summary", function()
  local s = ns.Data:Stats()
  if not s then
    ns.Warn("no data loaded")
    return
  end
  local meta = ns.Data:Meta()
  ns.Print(("%s-%s (%s)"):format(
    meta.guild or "?", meta.realm or "?", (meta.region or "?"):upper()))
  ns.Print(("%d characters -- median %d, mean %.1f, range %d-%d"):format(
    s.count, s.median, s.mean, s.min, s.max))

  local age = ns.Data:AgeInDays()
  if age then
    local line = ("exported %s (%d day%s ago)"):format(
      ns.Data:GeneratedAt(), age, age == 1 and "" or "s")
    if ns.Data:IsStale() then
      ns.Warn(line .. " -- time to re-run the exporter")
    else
      ns.Print(ns.Colorize("dim", line))
    end
  end
end)

register("set", "<option> [on|off]", "Toggle or set an option", function(args)
  local key, value = args:match("^(%S+)%s*(%S*)$")
  if not key or key == "" then
    ns.Print("options:")
    for name, default in pairs(ns.DEFAULTS) do
      local current = ns.db[name]
      local shown = (type(current) == "boolean")
        and (current and "|cff00ff00on|r" or "|cffff4444off|r")
        or tostring(current)
      DEFAULT_CHAT_FRAME:AddMessage(("  %s%-18s%s %s  %s"):format(
        ns.COLOR.brand, name, ns.COLOR.reset, shown,
        ns.Colorize("dim", "(default " .. tostring(default) .. ")")))
    end
    return
  end

  if ns.DEFAULTS[key] == nil then
    ns.Error(("unknown option '%s'"):format(key))
    return
  end

  local newValue, err
  if value == "" then
    newValue, err = ns.SetOption(key)
  elseif value == "on" or value == "true" or value == "1" then
    newValue, err = ns.SetOption(key, true)
  elseif value == "off" or value == "false" or value == "0" then
    newValue, err = ns.SetOption(key, false)
  else
    newValue, err = ns.SetOption(key, tonumber(value) or value)
  end

  if newValue == nil then
    ns.Error(("%s: %s"):format(key, err or "could not set"))
    return
  end

  ns.Print(("%s = %s"):format(key, tostring(newValue)))
end)

register("sync", "[now|forget]", "Roster snapshot sync status and controls", function(args)
  args = (args or ""):lower():gsub("^%s+", ""):gsub("%s+$", "")

  if args == "now" then
    if not ns.Sync then
      ns.Error("sync is unavailable")
      return
    end
    ns.Sync:ForceSync()
    ns.Print("announcing and looking for a newer roster...")
    return
  end

  if args == "forget" then
    if ns.Data:ForgetSnapshot() then
      ns.Print(("dropped the adopted snapshot -- back to the shipped export, %s entries"):format(
        ns.Colorize("value", ns.Data:Count())))
    else
      ns.Print("no adopted snapshot to drop")
    end
    return
  end

  local kind, info = ns.Data:SourceInfo()
  local age = ns.Data:AgeInDays()
  ns.Print(("source: %s -- %s entries, exported %s%s"):format(
    ns.Colorize("value", kind == "sync" and "guildmate" or "shipped file"),
    ns.Colorize("value", ns.Data:Count()),
    ns.Data:GeneratedAt() or "?",
    age and (" (%d day%s ago)"):format(age, age == 1 and "" or "s") or ""))

  if kind == "sync" and info.from then
    ns.Print(ns.Colorize("dim", ("received from %s%s"):format(
      info.from,
      info.receivedAt and (" on " .. date("%Y-%m-%d %H:%M", info.receivedAt)) or "")))
  end

  if not ns.db.syncEnabled then
    ns.Warn("syncEnabled is off -- this client neither sends nor receives")
    return
  end

  local status = ns.Sync and ns.Sync:Status()
  if status then
    if status.pending then
      ns.Print(ns.Colorize("dim", "waiting on a snapshot from " .. status.pending))
    elseif status.serving then
      ns.Print(ns.Colorize("dim", "currently serving " .. status.serving))
    end
    ns.Print(ns.Colorize("dim", ("%d request%s made, %d dump%s served this session"):format(
      status.attempts, status.attempts == 1 and "" or "s",
      status.serveCount, status.serveCount == 1 and "" or "s")))
  end
end)

register("reload", "", "Rebuild the lookup table from Data/GuildData.lua", function()
  local count = ns.Data:Build()
  ns.Print(("rebuilt -- %d entries"):format(count))
end)

-- ------------------------------------------------------------------
-- Dispatch
-- ------------------------------------------------------------------
local function dispatch(input)
  input = (input or ""):gsub("^%s+", ""):gsub("%s+$", "")

  local command, rest = input:match("^(%S+)%s*(.*)$")
  if not command then
    handlers.help.fn("")
    return
  end

  command = command:lower()

  local handler = handlers[command]
  if handler then
    handler.fn(rest or "")
    return
  end

  -- Bare "/ros Somename" is treated as a lookup.
  handlers.who.fn(input)
end

function Commands:OnEnable()
  SLASH_ROSTOOLS1 = "/ros"
  SlashCmdList["ROSTOOLS"] = dispatch
end
