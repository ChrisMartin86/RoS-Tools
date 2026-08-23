-- Riddled/Modules/Browser.lua
-- A self-contained window listing the whole exported roster.
-- Opened with /riddle list. Deliberately does not touch any Blizzard
-- frame, so it cannot break when the Communities UI changes.

local _, ns = ...

local Browser = ns:RegisterModule("Browser")

local ROW_HEIGHT = 16
local VISIBLE_ROWS = 20
local WIDTH, HEIGHT = 360, 420

local frame, scrollFrame, rows
local dataset = {}
local sortDescending = true
local filterText = ""

-- ------------------------------------------------------------------
-- Data shaping
-- ------------------------------------------------------------------
local function rebuildDataset()
  dataset = (filterText ~= "")
    and ns.Data:Find(filterText)
    or ns.Data:Top(math.huge)

  if not sortDescending then
    local reversed = {}
    for i = #dataset, 1, -1 do reversed[#reversed + 1] = dataset[i] end
    dataset = reversed
  end
end

local function prettyName(key)
  local name, realm = key:match("^([^%-]+)%-(.+)$")
  if not name then return key end
  if realm == ns.playerRealmSlug then return name end
  return ("%s |cff888888-%s|r"):format(name, realm)
end

-- ------------------------------------------------------------------
-- Rendering
-- ------------------------------------------------------------------
local function refresh()
  if not frame or not frame:IsShown() then return end

  local offset = FauxScrollFrame_GetOffset(scrollFrame) or 0

  for i = 1, VISIBLE_ROWS do
    local row = rows[i]
    local entry = dataset[i + offset]
    if entry then
      row.name:SetText(prettyName(entry.key))
      row.ilvl:SetText(ns.ColorForIlvl(entry.ilvl) .. entry.ilvl .. ns.COLOR.reset)
      row:Show()
    else
      row:Hide()
    end
  end

  FauxScrollFrame_Update(scrollFrame, #dataset, VISIBLE_ROWS, ROW_HEIGHT)

  local stats = ns.Data:Stats()
  if stats then
    frame.status:SetText(("%d shown  |  median %d  |  max %d"):format(
      #dataset, stats.median, stats.max))
  else
    frame.status:SetText("No data loaded.")
  end
end

local function build()
  frame = CreateFrame("Frame", "RiddledBrowserFrame", UIParent, "BasicFrameTemplateWithInset")
  frame:SetSize(WIDTH, HEIGHT)
  frame:SetPoint("CENTER")
  frame:SetMovable(true)
  frame:EnableMouse(true)
  frame:RegisterForDrag("LeftButton")
  frame:SetScript("OnDragStart", frame.StartMoving)
  frame:SetScript("OnDragStop", frame.StopMovingOrSizing)
  frame:SetClampedToScreen(true)
  frame:Hide()

  frame.TitleText:SetText("Riddled -- Guild Item Levels")

  tinsert(UISpecialFrames, "RiddledBrowserFrame") -- Escape closes it

  -- Search box
  local search = CreateFrame("EditBox", nil, frame, "SearchBoxTemplate")
  search:SetSize(WIDTH - 130, 20)
  search:SetPoint("TOPLEFT", 14, -32)
  search:SetAutoFocus(false)
  search:SetScript("OnTextChanged", function(self)
    SearchBoxTemplate_OnTextChanged(self)
    filterText = self:GetText() or ""
    rebuildDataset()
    FauxScrollFrame_SetOffset(scrollFrame, 0)
    if scrollFrame.ScrollBar then scrollFrame.ScrollBar:SetValue(0) end
    refresh()
  end)
  frame.search = search

  -- Sort toggle
  local sortButton = CreateFrame("Button", nil, frame, "UIPanelButtonTemplate")
  sortButton:SetSize(90, 20)
  sortButton:SetPoint("LEFT", search, "RIGHT", 6, 0)
  sortButton:SetText("ilvl desc")
  sortButton:SetScript("OnClick", function(self)
    sortDescending = not sortDescending
    self:SetText(sortDescending and "ilvl desc" or "ilvl asc")
    rebuildDataset()
    refresh()
  end)

  -- Scroll frame
  scrollFrame = CreateFrame("ScrollFrame", "RiddledBrowserScroll", frame, "FauxScrollFrameTemplate")
  scrollFrame:SetPoint("TOPLEFT", 12, -60)
  scrollFrame:SetSize(WIDTH - 48, VISIBLE_ROWS * ROW_HEIGHT)
  scrollFrame:SetScript("OnVerticalScroll", function(self, offset)
    FauxScrollFrame_OnVerticalScroll(self, offset, ROW_HEIGHT, refresh)
  end)

  rows = {}
  for i = 1, VISIBLE_ROWS do
    local row = CreateFrame("Frame", nil, frame)
    row:SetSize(WIDTH - 52, ROW_HEIGHT)
    if i == 1 then
      row:SetPoint("TOPLEFT", scrollFrame, "TOPLEFT", 0, 0)
    else
      row:SetPoint("TOPLEFT", rows[i - 1], "BOTTOMLEFT", 0, 0)
    end

    row.name = row:CreateFontString(nil, "ARTWORK", "GameFontHighlightSmall")
    row.name:SetPoint("LEFT", 4, 0)
    row.name:SetJustifyH("LEFT")

    row.ilvl = row:CreateFontString(nil, "ARTWORK", "GameFontHighlightSmall")
    row.ilvl:SetPoint("RIGHT", -6, 0)
    row.ilvl:SetJustifyH("RIGHT")

    rows[i] = row
  end

  frame.status = frame:CreateFontString(nil, "ARTWORK", "GameFontDisableSmall")
  frame.status:SetPoint("BOTTOMLEFT", 16, 14)
  frame.status:SetJustifyH("LEFT")

  frame:SetScript("OnShow", function()
    rebuildDataset()
    refresh()
  end)
end

-- ------------------------------------------------------------------
-- Public
-- ------------------------------------------------------------------
function Browser:Toggle()
  if not frame then build() end
  if frame:IsShown() then frame:Hide() else frame:Show() end
end

function Browser:Show()
  if not frame then build() end
  frame:Show()
end
