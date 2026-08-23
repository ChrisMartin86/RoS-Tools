-- luacheck configuration for World of Warcraft addon development.
-- Install: luarocks install luacheck   |   Run: luacheck .

std = "lua51"
max_line_length = 120
codes = true

exclude_files = {
  ".luacheckrc",
  "Tools/",
}

ignore = {
  "212/self",   -- unused argument self
  "212/_.*",    -- unused arguments prefixed with underscore
  "431",        -- shadowing an upvalue
}

globals = {
  -- Our own SavedVariables and legacy data globals
  "RiddledDB",
  "RiddledTooltip_DB",
  "RiddledTooltip_Meta",
  -- Slash command globals
  "SLASH_RIDDLED1", "SLASH_RIDDLED2", "SLASH_RIDDLED3",
  "SlashCmdList",
  "UISpecialFrames",
}

read_globals = {
  -- Core WoW API
  "CreateFrame", "UIParent", "GameTooltip", "DEFAULT_CHAT_FRAME",
  "hooksecurefunc", "wipe", "tinsert", "time", "date", "strsplit",
  "UnitName", "UnitExists", "UnitIsPlayer", "GetRealmName",
  "GetPlayerInfoByGUID", "GetAverageItemLevel",
  "C_AddOns", "Enum", "TooltipDataProcessor",
  -- Widget templates / helpers
  "FauxScrollFrame_GetOffset", "FauxScrollFrame_Update",
  "FauxScrollFrame_OnVerticalScroll", "FauxScrollFrame_SetOffset",
  "SearchBoxTemplate_OnTextChanged",
  -- LOD frames we probe defensively
  "CommunitiesMemberListEntryMixin",
}
