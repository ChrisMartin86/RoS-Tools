# Changelog

## 2.3.1

### Fixed

- **Unit tooltips threw a Lua error in instances** — around a hundred per run.
  Inside an instance the game hands the addon a protected unit reference, and
  the check that skips non-players choked on it before it could skip anything.
  Protected references are now recognised and passed over; tooltips on
  guildmates are unaffected.

## 2.3.0

### Added

- **Your roster now keeps itself current.** Whoever in the guild is holding the
  freshest export shares it with anyone holding an older one, over the guild
  addon channel — and whoever receives it becomes a source for the next person.
  Install from CurseForge and that is the whole story: no script to run, no
  background app, no credentials, nothing to re-run when the numbers look
  stale. `/ros sync` shows where the roster you are using came from.
- **Live item levels.** While you are online with a guildmate, their equipped
  item level updates in real time instead of waiting on the next export.
  Nothing leaves the game client — it is the guild addon channel and nothing
  else.
- Tooltips, the roster browser and `/ros` all read from whichever source is
  newer, the shipped file or a snapshot picked up from a guildmate, with no
  setup either way.

Both features are on by default and can be turned off independently through
`/ros set`: `syncEnabled`, `syncShare`, `syncNotify`, `commEnabled`,
`commBroadcast`.

### Fixed

- **Unit tooltips threw a Lua error in Mt. Hyjal on 12.0 clients** — hundreds
  of them per pull. Blizzard's new "secret values" are now detected and stepped
  over everywhere they can reach the addon.

### Changed

- **There is nothing to install but the addon.** The PowerShell installer and
  both data updaters are gone; peer sync solves what they were there for.
- Coming from a `Riddled`-era build? The addon folder, the slash command
  (`/ros`) and the saved settings were all renamed — old settings revert to
  defaults once.

### For maintainers

- **The sidecar tray app** (a separate download, meant for one or two machines)
  seeds the peer sync by keeping at least one client on a current roster. Its
  new **data console** shows the installed roster in a browser and can pull
  straight from the Blizzard API with your own credentials — staged, diffed and
  validated before anything is installed, never on a schedule.
- One-line installers for the sidecar and for any dev branch:
  `scripts/Install-Sidecar.ps1` and `scripts/Install-Dev.ps1`.
- A full-codebase audit closed 10 high, 22 medium and ~25 low findings across
  the addon, sidecar, exporter and CI, each with a regression test confirmed to
  fail without its fix. Detail in `AUDIT-2026-08-31.md`.


## 2.2.0

### Fixed

- **Export age is timezone-proof.** `Tools/fetch_guild_info.py` wrote
  `generated_at` with `time.strftime()` — local wall clock, no offset
  recorded — and the addon parsed it back with `time(table)`, which reads a
  table in the *client's* zone. An export made west of the player therefore
  landed in the future and reported "exported -1 days ago". The exporter now
  emits `generated_epoch`, a plain UTC epoch, and renders `generated_at` as
  UTC; `Data:AgeInDays()` does epoch arithmetic against `time()` with no zone
  in the middle, and `Data:GeneratedAt()` formats the epoch back into the
  viewer's local time for display. Data schema is now 3. Schema 2 files still
  load and fall back to the old string parse, clamped at 0 days.
- **Roster item level no longer doubles.** The suffix pattern was anchored to
  end-of-string, but Blizzard appends its own trailing text to the name on
  alt-grouped rows, which put the suffix mid-string. The anchored strip missed
  it and the next refresh stacked a second `(ilvl)` on top. The strip is now
  unanchored and removes every occurrence, which also repairs rows a previous
  build had already doubled.
- **Roster name matching survives cosmetic differences.** `findNameFontString`
  compared displayed text to `memberInfo.name` byte for byte. It now compares
  on a collapsed key (colors stripped — including retail's newer
  `|cnCOLOR_NAME:` form — then punctuation and whitespace removed, lowercased)
  and scores candidates, preferring an exact match on the full `Name-Realm`,
  then the bare name, then a string that starts with the full name. That last
  rank is what alt-grouped rows need. A bare-name prefix is deliberately not
  accepted; it would match the note and zone columns.
- **`memberInfo.name` no longer goes through `Util.NormalizeKey`.** That
  helper strips a displayed player title by taking the last whitespace-
  delimited token, which is right for a tooltip header and wrong for a
  mixin-driven field: it turned `Helltz-Moon Guard` into `Guard`. Realms with
  spaces now resolve on the roster.
- **Unit tooltips bail on non-players before doing any work.** The
  `TooltipDataProcessor` hook fires for every unit in the game. When the GUID
  and unit lookups failed it fell through to parsing the tooltip header, where
  the title-strip turns an NPC's name into a plausible-looking key
  ("Auction House Resident" -> `Resident-<realm>`), burning a lookup and a
  debug line per frame. It now returns early unless the GUID is `Player-*` or
  `UnitIsPlayer` is true, and the header fallback only runs when there was
  neither a GUID nor a unit.

### Changed

- **Debug output is deduplicated.** `UpdateNameFrame` fires many times per
  second per visible row, and every miss logged every time. Roster results are
  now cached per entry frame in a weak-keyed table: an unchanged row returns
  without walking the widget tree, and a member with no data logs once instead
  of once per tick. Pooled rows with no `memberInfo`, the disabled-module
  notice, and repeated tooltip misses log once as well.

## 2.1.0

### Added

- **Item level on roster hover.** `CommunitiesMemberListEntryMixin`'s
  `OnEnter`/`OnLeave` are hooked alongside the existing `UpdateNameFrame`
  hook. Blizzard only builds a tooltip for a row in some states, so the
  hook appends to the tooltip when one is already up and owned by that
  row, and builds its own otherwise. Hooking the hover handlers is
  optional and separate: if Blizzard renames them, the name annotation
  keeps working. Setting: `rosterTooltip`.
- **Item level on chat name hover.** A name in guild chat is a
  `|Hplayer:Name-Realm:...|` hyperlink, and Blizzard's
  `HYPERLINKS_WITH_TOOLTIPS` deliberately excludes `player` — so no
  tooltip exists to append to and RoS-Tools builds the whole thing, header
  included. The chat frames' own `OnHyperlinkEnter`/`OnHyperlinkLeave`
  scripts are hooked rather than the global `ChatFrame_OnHyperlinkEnter`,
  since chat replacement addons reuse the frames but not always the
  global. Temporary whisper tabs are picked up via `FCF_OpenTemporaryWindow`.
  `BNplayer` links are ignored — a Battle.net link identifies an account,
  not a character. Setting: `chatTooltip`.

### Changed

- Item level rendering (tier color plus the optional delta) moved into
  `ns.IlvlText` in `Core/Config.lua`, and the tooltip block into
  `ns.AddIlvlLines`, so the four surfaces that print a number can't drift
  apart.

### Earlier unversioned notes

### Added

- Daily data pipeline. `.github/workflows/guild-data.yml` re-exports the
  roster at 09:00 UTC and publishes it to a dedicated `guild-data` branch,
  which the companion updater pulls from. Needs `BLIZZARD_CLIENT_ID` and
  `BLIZZARD_CLIENT_SECRET` as repository secrets.
- `Tools/compare_guild_data.py` — decides whether an export is worth
  publishing. The exporter rewrites `generated_at` every run, so a file diff
  is always dirty; this compares only the item level table. It also rejects
  an export whose roster shrank by more than a third, which is far more
  likely an API failure than real departures.

- `Tools/Update-RoSTools.ps1` — a companion updater that refreshes
  `Data/GuildData.lua` outside the game, since WoW's Lua sandbox has no
  network access. Two modes, auto-detected: **export** (Blizzard credentials
  present, shells out to `fetch_guild_info.py`) and **download** (no
  credentials, pulls the published file from GitHub). Guildmates need nothing
  but PowerShell.
- `Tools/Play-RoSTools.cmd` — double-clickable wrapper that updates and then
  launches WoW. A failed update never blocks the game.

Both live under `Tools/`, so neither ships in the packaged addon zip.

The updater stages downloads to a temp file and validates them before
installing — it rejects HTML error pages, truncated files, and files with no
character entries, leaving existing data untouched. It also keeps one
`.bak` rollback copy.

## 2.0.0

Restructured from two flat files into a modular project.

### Removed

- The entire embedded SHA-256 implementation, base64url decoder, XOR cipher,
  and `loadstring`/`setfenv` import path — roughly 250 lines of unreachable
  code. `importBlobIntoSaved()` had no callers once the `/riddle` import box
  was taken out, and the hardcoded `RIDDLED_SECRET` was sitting in plaintext
  protecting nothing.
- SavedVariables persistence of the item level table. `loadExternalData()`
  overwrote it from the static file on every `ADDON_LOADED`, so the data was
  written to disk on every logout and thrown away on every login.

### Fixed

- **Realm slugs with apostrophes.** The old `realmToSlug()` stripped the
  apostrophe and then CamelCase-split the result, so `Kel'Thuzad` became
  `kel-thuzad` when the actual slug is `kelthuzad`. Same for `Mal'Ganis` and
  `Aman'Thul`. Those characters silently never resolved. The apostrophe now
  suppresses the word boundary, and a `SLUG_OVERRIDES` table handles realms
  that can't be derived mechanically.
- **Tooltip hooking.** Replaced `GameTooltip:HookScript("OnShow")` plus
  `hooksecurefunc(GameTooltip, "SetUnit")` with
  `TooltipDataProcessor.AddTooltipPostCall(Enum.TooltipDataType.Unit, ...)`.
  The old hooks fired on every tooltip in the game — items, spells, action
  bar buttons — and ran a lookup each time. The legacy path is retained as a
  fallback where the modern API is absent.
- **Combat handling.** `PLAYER_REGEN_DISABLED` used to call
  `GameTooltip:Hide()` and suppress all output until combat ended. That's now
  an opt-in setting (`suppressInCombat`, default off).
- Duplicate-line detection no longer re-scans every tooltip line on each call;
  tooltips carry a stamp that's cleared on hide.

### Added

- `/riddle` command set: `list`, `who`, `find`, `top`, `stats`, `set`,
  `reload`, plus `/riddled` and `/rid` aliases.
- Standalone roster browser window with search and sort (`/riddle list`).
- Guild & Communities roster annotation, written defensively against
  Blizzard UI changes.
- Item level coloring by quality tier, and optional delta against your own
  equipped item level.
- Stale-data warning driven by the export timestamp.
- `Tools/fetch_guild_info.py` — a complete exporter using the Blizzard
  client credentials flow, with concurrency, retry on 429/5xx, and direct
  output to `Data/GuildData.lua`.
- luacheck config and a CI workflow that lints and packages a release zip.

### Changed

- Data now loads into the addon namespace (`ns.GuildData`) instead of the
  globals `RiddledTooltip_DB` / `RiddledTooltip_Meta`. Those are still read if
  present, so an old `Riddled_Data.lua` left in the folder keeps working.

## 0.1.0

Initial version. Static data file plus tooltip injection.
