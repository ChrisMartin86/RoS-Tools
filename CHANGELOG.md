# Changelog

## Unreleased

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

- `Tools/Update-Riddled.ps1` — a companion updater that refreshes
  `Data/GuildData.lua` outside the game, since WoW's Lua sandbox has no
  network access. Two modes, auto-detected: **export** (Blizzard credentials
  present, shells out to `fetch_guild_info.py`) and **download** (no
  credentials, pulls the published file from GitHub). Guildmates need nothing
  but PowerShell.
- `Tools/Play-Riddled.cmd` — double-clickable wrapper that updates and then
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
