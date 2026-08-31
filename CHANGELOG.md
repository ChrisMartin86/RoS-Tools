# Changelog

## Unreleased

### Added

- **A data console, and optional direct pulls from the Blizzard API.** The
  tray's new **Data console…** opens a local web page showing the roster that
  is installed -- every character, item level, export date, and how close it is
  to the size ceiling for sharing. Supply your own Blizzard client ID and
  secret and it can also pull the roster straight from Blizzard, show you what
  changed against what you have, and install it. The secret is encrypted with
  Windows DPAPI under your user account; it is never returned to the page, and
  `BLIZZARD_CLIENT_ID` / `BLIZZARD_CLIENT_SECRET` are honoured instead if set.

  This reverses a rule the sidecar shipped with, so the reasoning behind that
  rule is worth restating: the **background poller still reads only the
  `guild-data` branch**, whether or not credentials exist. CI remains the
  guild's one *scheduled* API consumer, which is what keeps call volume flat as
  installs grow. A pull happens only when a person clicks Pull.

  Because `Core/Sync.lua` means whoever installs a roster announces it and
  serves it to the whole guild, a pull is never installed on its own: it is
  staged, shown, and installed by a second explicit click. It goes through the
  same validator a download does -- including the guild-identity check, which
  now falls back to the installed file's own identity, so a machine that has
  not yet learned a guild is still protected from a typo in the realm box. On
  top of that, **a pull more than 20% smaller than the installed roster is
  refused without an explicit override**, because a handful of throttled
  requests or private profiles produces a perfectly valid file that is simply
  missing people -- and nothing else in the system can tell that apart from a
  guild that genuinely shrank.

  The console listens on `localhost` on a random port, is off until opened, and
  authenticates every request with a session token minted at startup. The link
  the tray hands the browser carries a separate single-use token, burned on
  first load, so the URL is worthless afterwards -- a browser URL is a process
  command line, readable by anything running as the same user.

- **The roster file is now checked against what guildmates will accept, not
  just against what loads.** Before `Core/Sync.lua`, a bad `Data/GuildData.lua`
  cost one person wrong tooltip numbers. Now that clients share rosters with
  each other, whoever installs a file announces it to the guild and serves it
  to everyone who asks -- so the installer is a guild-wide gate, and it had to
  be raised to match. It now refuses a roster for a different guild (the
  failure that let one mistyped data URL stall every stale client in the
  guild, silently, forever), a `generated_epoch` in the future or missing
  entirely, item levels and character keys that peers would drop, duplicate
  keys, a roster too large or too numerous to transfer, and -- the important
  one -- a file that is brace-balanced but not actually loadable Lua. It also
  warns, rather than failing, when an export is old enough that guildmates
  will no longer accept it, because that is when roster sharing stops
  guild-wide with nothing else to say so. These rules now live in exactly one
  place, `GuildDataValidator.cs`, since the PowerShell copies went with the
  scripts that carried them.
- **`sidecar.yml` workflow.** Builds and tests the sidecar on every push
  touching `Sidecar/`, and publishes a self-contained exe on a
  `sidecar-vX.Y.Z` tag.
- **`scripts/Install-Dev.ps1` — a one-liner install of any branch, tag or commit,
  for debugging.** Finds WoW at the default location (then the uninstall registry
  key, then the usual places), downloads that ref's archive from GitHub, and
  wipes and rewrites `AddOns\RoS-Tools` so a file deleted in source does not
  linger. `WTF\` is never touched. Knobs are environment variables, since `iex`
  leaves no way to pass parameters: `ROSTOOLS_REF`, `ROSTOOLS_ADDONS_PATH`, and
  `ROSTOOLS_KEEP_DATA` — the last keeps the roster already installed instead of
  the ref's, which is how you hold one client deliberately stale and watch
  `Core/Sync.lua` adopt a newer snapshot from a peer.

  It is documented only under Development and is not how guildmates install the
  addon. Unlike the scripts it replaces, it **never touches the `guild-data`
  branch** — it installs whatever roster the ref happens to carry, so
  `GuildDataValidator.cs` remains the single copy of the validation rules
  outside the addon.

### Fixed

- **The poller could quietly undo a hand-installed roster.** `UpdateService`
  compared nothing but ETags, so after installing a pull it would fetch the
  `guild-data` branch and reinstall it even when that file was *older* --
  exactly the situation a manual pull exists for, CI having failed. Since
  `Core/Sync.lua` orders the guild by `generated_epoch`, that dropped the
  client below data its own peers had already adopted from it. It now refuses
  to move the installed roster backwards in time.

- **The sidecar could report "Already up to date" over a roster it had not
  installed.** Its conditional-request cache was a single ETag gated on "the
  destination file exists", so pointing it at a second addon folder -- or
  anything overwriting or truncating the installed file -- earned a 304 and a
  healthy tray icon over stale or broken data, indefinitely, since the data
  branch only publishes when item levels actually change. The cache is now
  keyed by destination and carries the `generated_epoch` of the file the
  sidecar actually wrote there; anything else refetches unconditionally. Same
  bug and same fix as `Tools/Update-RoSTools.ps1` had earlier.
- **The sidecar's tray icon never aged.** A poll loop that died on day one went
  on rendering "180 characters - updated 9 days ago" under a healthy icon for
  the life of the process. It now warns when checks have stopped, when the loop
  dies, and when the installed export is getting old.
- **Installing across drives was not atomic.** Staging always landed in
  `%TEMP%` on the system drive, so a WoW install anywhere else got a
  copy-over-the-destination rather than a rename -- a drive that drops or a
  disk that fills partway through left truncated Lua where the roster was, and
  the `.bak` written moments earlier was never restored. Installs now stage on
  the destination's own volume and roll back.
- **A snapshot that transferred and was then rejected stalled the requester.**
  A refusal correctly fell through to the next holder, but a completed transfer
  that failed validation did not, so one client carrying an unusable roster
  could hold up every stale client in the guild for a whole anti-entropy
  window, every window. (`Core/Sync.lua`)
- **The per-entry item level ceiling was a dated time bomb.** It sat at 999, a
  few content patches above the guild's real numbers; the season retail passed
  it, every export would have become 100% invalid entries and roster sharing
  would have stopped guild-wide with nothing to point at. Raised well clear of
  anything the game issues. (`Core/Sync.lua`)
- **The "roster is too large to share" warning repeated on every request.** The
  size check sits above the serve throttles and those only arm on success, so
  an oversized roster put one line in the chat frame per peer request. Once a
  session now. (`Core/Sync.lua`)
- **The sidecar backed off into polling more often than when healthy.** A
  refused file counted as a failure, and the backoff path ignored both the
  configured interval and its jitter -- a 6-hourly client became a flat hourly
  one, in lockstep with every other failing client.
- **Opening the sidecar's Settings window silently pinned the addon folder.**
  The box is pre-filled with whatever auto-detect found, and saving wrote that
  back as an explicit path, so anyone who opened Settings to change something
  else stopped auto-detecting -- and only found out after moving their WoW
  install.


- **Roster snapshot sync over addon comm (`Core/Sync.lua`).** A guildmate
  holding a newer `Data/GuildData.lua` export now passes it to guildmates
  holding an older one, so people who never update the addon still end up on
  current data. Peer-symmetric: every client announces its export's
  `generated_epoch` over the `GUILD` channel (prefix `RoSToolsD1`), a client
  with older data whispers a holder for a copy, and a client that just
  adopted one immediately becomes a source for the next -- one person running
  the Sidecar keeps the whole guild current without being a bottleneck or a
  single point of failure. Snapshots are pull-only and never pushed, and are
  validated on arrival (guild identity, epoch sanity, plausible roster size)
  before being adopted. A received snapshot is stored in SavedVariables and
  dropped automatically as soon as the shipped file is newer. New options
  `syncEnabled`, `syncShare` and `syncNotify` (all default on), and a new
  `/ros sync` command showing where the current roster came from, with
  `/ros sync now` and `/ros sync forget`.
- **Offline protocol harness (`Tools/sync-harness.lua`).** Runs the real
  `Core/*.lua` against a stubbed WoW API, a fake addon-message bus and a
  virtual clock, with several simulated clients. `lua5.1 Tools/sync-harness.lua`
  from the repo root; 40 assertions, every one of them a bug that actually
  happened. Excluded from packaging and from luacheck, so it never ships.
- **Live ilvl sync over addon comm (`Core/Comm.lua`).** While you're online
  with a guildmate, their equipped item level now updates in real time
  instead of waiting on the next static export or Sidecar poll. Each client
  broadcasts only its own ilvl, only on change, only to the `GUILD` channel
  (prefix `RoSTools1`) -- no roster sync, no server, nothing that leaves the
  game client. New options `commEnabled` and `commBroadcast` (both default
  on) gate receiving and sending independently.
- **Desktop sidecar (`Sidecar/`).** A .NET 10 Windows tray app that polls the
  `guild-data` branch every few hours and installs `Data/GuildData.lua` into the
  addon folder, so nobody has to remember to run the updater. Same validation
  and `.bak` rollback as `Tools/Update-RoSTools.ps1`, just resident. It never
  calls the Blizzard API — CI stays the single API consumer — and never touches
  the WoW process. Ships as its own signed-later exe off a `sidecar-vX.Y.Z` tag;
  the addon zip is unaffected and the addon itself is unchanged.

### Changed

- **`/ros set` now rejects a value of the wrong type.** `/ros set staleDays soon`
  used to persist the string, after which every comparison against it threw --
  at login and on every player tooltip -- until the SavedVariables file was
  edited by hand.
- **Renamed from Riddled to RoS-Tools.** Repository is now
  `ChrisMartin86/RoS-Tools`; the addon folder and manifest are `RoS-Tools` /
  `RoS-Tools.toc`.
- **Slash command is `/ros`.** `/riddle`, `/riddled` and `/rid` are gone.
- **SavedVariables moved to `RoSToolsDB`.** Clean break — settings saved under
  the old `RiddledDB` are not migrated and revert to defaults on first login.
- **Scripts renamed:** `Install-RoSTools.ps1`, `Update-RoSToolsData.ps1`,
  `Deploy-RoSTools.ps1`, `Tools/Update-RoSTools.ps1`, `Tools/Play-RoSTools.cmd`.
  The install and data-update one-liners now point at the new repo URL.
- Global frame names, the tooltip marker and the `ROSTOOLS_*` environment
  variables were renamed to match. Legacy `RiddledTooltip_DB` /
  `RiddledTooltip_Meta` import globals keep their old names on purpose, so an
  old `Riddled_Data.lua` left in the folder still loads.

### Fixed

- **Updater no longer reports "Already up to date" over a stale roster.**
  `Tools/Update-RoSTools.ps1` cached one global ETag in
  `%LOCALAPPDATA%\RoS-Tools\updater-state.json` with no record of which file
  it described, and only checked that the destination *existed* before sending
  `If-None-Match`. Updating a second location (say the installed addon after
  refreshing a checkout) reused the first location's ETag, took the 304, and
  left the old data in place while reporting success. The cache is now keyed by
  destination and stores the `generated_epoch` actually installed there; a
  conditional request goes out only when that stamp still matches the file on
  disk. Old version-1 state is discarded rather than migrated -- one extra
  download beats a wrong cache.
- **`-AddOnPath`-less runs from a checkout say so.** Resolving to the repo
  instead of the installed addon now prints a warning, since the copy WoW loads
  is deployed separately by `scripts/Deploy-RoSTools.ps1` and is not touched.

### Removed

- **The PowerShell installer and both data updaters are gone**, along with the
  `irm … | iex` one-liners that fronted them: `scripts/Install-RoSTools.ps1`,
  `scripts/Update-RoSToolsData.ps1`, `Tools/Update-RoSTools.ps1` and
  `Tools/Play-RoSTools.cmd`.

  They existed because a static export had no way to reach a guildmate's disk
  on its own. `Core/Sync.lua` removes that constraint: clients announce the
  `generated_epoch` they hold, and anyone older pulls the newer snapshot from a
  peer and keeps it across logouts. So the general-user story is now **install
  from CurseForge and do nothing else** — no script, no background app, no
  credentials, nothing to re-run when the numbers look old.

  The sidecar stays, with a narrower job: it *seeds* the peer sync, so it wants
  to run on one or two machines (the maintainer's and the guild leader's) rather
  than guild-wide. Everyone else gets the same data one hop later.

  Two things go away with the scripts. The GuildData validator no longer has to
  be hand-mirrored across four copies — `GuildDataValidator.cs` is the only one
  left, and it only has to agree with `Core/Sync.lua`. And nothing is served raw
  from `main` into a `iex` any more, so there is no longer an install path with
  no release gate in front of it.

  `scripts/Deploy-RoSTools.ps1` is unaffected — it is a local dev-loop copy and
  was never handed out. See `scripts/Install-Dev.ps1` under Added for the one
  one-liner that survives, and why it is not a walk-back of this.

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
