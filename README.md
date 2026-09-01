# RoS-Tools

A World of Warcraft addon that shows guild members' item levels — including
offline members and alts on other realms — sourced from a periodic Blizzard
Community API export rather than from inspection.

Item level appears in unit tooltips, in the Guild & Communities roster, and in
a standalone browser window.

---

## Layout

```
RoS-Tools/
├── RoS-Tools.toc                 Addon manifest and load order
├── Core/
│   ├── Init.lua                Namespace, module registry, logging
│   ├── Util.lua                Realm slugging and key normalization
│   ├── Config.lua              SavedVariables defaults, ilvl color tiers
│   ├── Data.lua                Lookup index and query API
│   ├── Comm.lua                Live per-player ilvl over the guild channel
│   ├── Sync.lua                Whole-roster snapshot sync between guildmates
│   └── Events.lua              Event frame, module lifecycle
├── Data/
│   └── GuildData.lua           Generated export (do not hand-edit)
├── Modules/
│   ├── Tooltip.lua             Unit tooltip injection
│   ├── Roster.lua              Guild & Communities roster annotation
│   ├── Browser.lua             Standalone roster window
│   └── Commands.lua            /ros slash commands
├── Tools/
│   └── fetch_guild_info.py     Regenerates Data/GuildData.lua
├── scripts/
│   └── Deploy-RoSTools.ps1     Dev-loop copy into the installed AddOns folder
└── Sidecar/                    Maintainer tray updater (.NET 10)
```

`Tools/` and `scripts/` are development-only and are excluded from the packaged
addon. `Sidecar/` ships separately as its own exe and is not something a general
user needs — see [Sidecar/README.md](Sidecar/README.md).

---

## Installing

Install from CurseForge — search for **RoS-Tools** in the CurseForge app. It
keeps the addon itself up to date, and the roster inside it keeps itself up to
date (see below).

Manual install works too: take the zip from the addon's CurseForge files page
and copy the `RoS-Tools` folder into
`World of Warcraft\_retail_\Interface\AddOns\RoS-Tools`. `RoS-Tools.toc` must
sit directly inside that folder — not one level deeper.

That is the whole install. No PowerShell, no background app, no credentials,
nothing to re-run. Settings live under `WTF\` and survive every upgrade.

---

## Installing the sidecar

Maintainers only — see [The sidecar](#the-sidecar--maintainers-only) below for
who actually needs this. Guildmates get current data through the addon alone
and never need to touch this section.

One line in PowerShell — Windows PowerShell 5.1 or PowerShell 7, either works:

```powershell
irm https://raw.githubusercontent.com/ChrisMartin86/RoS-Tools/main/scripts/Install-Sidecar.ps1 | iex
```

The same line installs it and updates it later: it picks the newest release,
stops if you're already current, verifies the download against the release's
SHA-256 checksum, then swaps the exe in and restarts it. Run it once and let it
start with Windows from then on.

Requirements: Windows, and the addon already installed via CurseForge — the
sidecar refreshes the roster inside RoS-Tools, it doesn't install the addon
itself. No .NET runtime needed; the exe is self-contained.

**Windows will warn you the first time** — the build is unsigned, so SmartScreen
shows *"Windows protected your PC"*. Click **More info**, then **Run anyway**.

Prefer to install by hand? Download `RoSToolsSidecar-<version>-win-x64.exe` from
the [latest sidecar release](https://github.com/ChrisMartin86/RoS-Tools/releases),
put it somewhere it can live, and run it — it finds your WoW install on its own.

Knobs for the installer script (`iex` leaves no way to pass parameters, so these
are environment variables):

| Variable | Effect |
| --- | --- |
| `ROSTOOLS_SIDECAR_PATH` | Where to install. Default: wherever it already runs from, else the start-with-Windows entry, else `%LOCALAPPDATA%\RoS-Tools` |
| `ROSTOOLS_SIDECAR_VERSION` | Pin a version, e.g. `1.2.0`. A pin may move you backwards |
| `ROSTOOLS_SIDECAR_FORCE` | Reinstall even if the version already matches |
| `ROSTOOLS_SIDECAR_NOSTART` | Do not launch it afterward |

Tray menu, the data console, what it writes to disk, and how to remove it:
**[Sidecar/README.md](Sidecar/README.md)**.

---

## Commands

| Command | Effect |
| --- | --- |
| `/ros` | Command help |
| `/ros list` | Open the roster browser window |
| `/ros who <name>` | Look up one character, with fuzzy fallback |
| `/ros find <text>` | Search names and realms |
| `/ros top [n]` | Highest item levels, default 10 |
| `/ros stats` | Median, mean, range, export age |
| `/ros sync` | Where the roster came from and how old it is |
| `/ros set` | List all options and current values |
| `/ros set <option> [on\|off]` | Toggle or set an option |
| `/ros reload` | Rebuild the lookup table in place |

`/ros` is the only slash command. A bare `/ros Somename` is treated as a
lookup.

### Options

| Option | Default | Effect |
| --- | --- | --- |
| `enabled` | on | Master switch for tooltip injection |
| `showTimestamp` | on | Show the export date under the ilvl line |
| `showStaleWarn` | on | Flag old data in the tooltip |
| `staleDays` | 14 | Days before data counts as stale |
| `colorByIlvl` | on | Color the number by quality tier |
| `showDelta` | off | Show +/- against your own equipped ilvl |
| `rosterColumn` | on | Annotate the Guild & Communities roster |
| `rosterTooltip` | on | Add the ilvl line to the roster row hover tooltip |
| `chatTooltip` | on | Show a tooltip when hovering a player name in chat |
| `suppressInCombat` | off | Hide the line while in combat |
| `commEnabled` | on | Live ilvl updates from online guildmates via addon comm (`GUILD` channel) |
| `commBroadcast` | on | Announce your own ilvl on change (off = receive only) |
| `syncEnabled` | on | Sync the whole roster with guildmates who have a newer export |
| `syncShare` | on | Serve your roster when a guildmate asks (off = receive only) |
| `syncNotify` | on | Print a line when a newer roster is adopted |
| `debug` | off | Verbose logging |

---

## How the roster stays current

WoW addons cannot reach the internet — the Lua sandbox has no sockets. So the
data arrives in hops, and only the last one is inside the game:

```
GitHub Action (daily) ──► guild-data branch ──► sidecar ──► addon ──► guildmates
 hits the Blizzard API     holds GuildData.lua   one or two   reads it   in-game
                                                 maintainers  at load    sync
```

Only the first hop touches Blizzard on a schedule. One export a day serves the
whole guild, so no client secret is ever handed out and call volume does not grow
as the guild does. (A maintainer running the sidecar can pull on demand from its
data console, with credentials they created themselves — that is a human clicking
a button, not anything automatic.)

**If you are not a maintainer there is nothing on this page to run.** The next
section is the only one that applies to you, and it applies by itself.

### In-game sync — everyone

Every export carries a `generated_epoch`. Clients announce theirs on the guild
addon channel; a client holding an older one asks a peer for the newer snapshot,
validates it, and adopts it. The adopted roster is saved to `RoSToolsDB`, so it
survives logout and is already in place at the next login.

That is the whole general-user story: install the addon, and the numbers follow
whoever in the guild has the freshest data. No background app, no script, no
reinstall. `Core/Comm.lua` does the same for a single live item level when a
guildmate re-gears.

| Command | Effect |
| --- | --- |
| `/ros sync` | Where the current roster came from, and how old it is |
| `/ros sync now` | Ask the guild for a newer snapshot right now |
| `/ros sync forget` | Drop the adopted roster and fall back to the packaged one |

Turn it off entirely with `/ros set syncEnabled off`, or keep receiving while
never serving with `/ros set syncShare off`.

Only guild members can send on the guild addon channel, and an adopted snapshot
is checked for guild identity, epoch bounds, key shape and entry count before it
replaces anything. The damage ceiling is wrong tooltip numbers, which is what
justifies cheap checks over anything heavier. Protocol and full design:
[DATA-SYNC-DESIGN.md](DATA-SYNC-DESIGN.md).

### The daily job — automatic

`.github/workflows/guild-data.yml` runs at 08:00 UTC, re-exports the roster,
and pushes to the **`guild-data`** branch. That branch holds only
`GuildData.lua` — no source, no history of yours to pollute.

08:00 UTC is 4am Eastern in summer and 3am in winter. GitHub's cron is UTC
only and honours no timezone, so the local hour drifts an hour twice a year;
both sides land well before raid time. GitHub also **disables scheduled
workflows in a repo with no pushes for 60 days** and emails you about it — if
the roster quietly goes stale, check the Actions tab first.

Two repository secrets are required:

| Secret | From |
| --- | --- |
| `BLIZZARD_CLIENT_ID` | <https://develop.battle.net/access/clients> |
| `BLIZZARD_CLIENT_SECRET` | same |

The job commits **only when item levels actually change**. The exporter
stamps a new `generated_at` on every run, so a plain file diff would be dirty
every day; `Tools/compare_guild_data.py` compares just the item level table.

It also refuses to publish an export whose roster shrank by more than a third
(and fails the run loudly instead). A revoked key or a Blizzard outage can
produce a valid-looking file with most of the guild missing — publishing that
would wipe everyone's data.

Run it by hand from the Actions tab; `force` publishes even with no changes,
and realm/guild/region can be overridden per run.

A CurseForge release also packages the freshest export at build time, so a
brand-new install never starts from nothing.

### The sidecar — maintainers only

For the peer sync to have anything to spread, somebody in the guild has to be
carrying the fresh file. That is what the sidecar is for: a tray app that polls
the `guild-data` branch every few hours and drops the new roster into the addon
folder, plus a local web console for pulling straight from Blizzard between
scheduled runs.

**It is meant for one or two people — the maintainer and the guild leader.**
Handing it out guild-wide buys nothing; everyone else gets the same data through
sync, one hop later. Install instructions:
[Installing the sidecar](#installing-the-sidecar).

It never touches the game process, and refuses to install anything that does not
validate as a real export. Full details:
**[Sidecar/README.md](Sidecar/README.md)**.

### Running the exporter by hand

For a one-off export without the Action or the sidecar:

1. Create an application at <https://develop.battle.net/access/clients>. The
   client credentials flow needs no redirect URI.
2. Export the credentials and run the script:

```powershell
$env:BLIZZARD_CLIENT_ID     = '<client id>'
$env:BLIZZARD_CLIENT_SECRET = '<client secret>'

python .\Tools\fetch_guild_info.py --realm khadgar --guild 'Riddle of Steel'
```

That overwrites `Data/GuildData.lua` in place. Commit it, or push it to the
`guild-data` branch for the sidecar to pick up. Useful flags: `--region`,
`--min-level`, `--workers`, `--out`.

Characters with private profiles or who have never logged in return no item
level and are skipped; the script reports the count at the end.

---

## Realm slugs

Every key is `Name-realm-slug`, matching Blizzard's API slug. In game we only
get a display name (`Moon Guard`) or a space-stripped tooltip form
(`MoonGuard`), so `Core/Util.lua` reconstructs the slug locally.

Two rules cover almost everything:

- Apostrophes are dropped **and** suppress the word boundary.
  `Kel'Thuzad` → `kelthuzad`, not `kel-thuzad`.
- CamelCase and letter/digit transitions become hyphens.
  `WyrmrestAccord` → `wyrmrest-accord`, `Area52` → `area-52`.

Realms with lowercase joining words (`Sisters of Elune`) can't be derived
mechanically and live in the `SLUG_OVERRIDES` table at the top of `Util.lua`.
If a guildmate's ilvl never resolves, that table is the first place to look —
turn on `/ros set debug` and the failing key gets logged.

---

## Development

```powershell
luarocks install luacheck
luacheck .
lua5.1 Tools/sync-harness.lua
```

`luacheck` is the lint. `Tools/sync-harness.lua` runs the real `Core/*.lua`
against a stubbed WoW API and must pass before any change to `Core/Sync.lua`
counts as done. CI runs the lint on every push and packages a release zip on
demand.

### Installing a branch for testing

Not the way to install the addon — that is CurseForge, above. This is for
putting an arbitrary branch, tag or commit onto a machine to test it, including
a machine with no checkout:

```powershell
irm https://raw.githubusercontent.com/ChrisMartin86/RoS-Tools/main/scripts/Install-Dev.ps1 | iex
```

It finds WoW at the default location (falling back to the uninstall registry key
and the usual places), downloads the ref's archive, and wipes and rewrites
`AddOns\RoS-Tools` — so a file you deleted in source does not linger. Settings
live under `WTF\` and are never touched. If WoW is under `Program Files`,
Windows may refuse the write and the script tells you to re-run elevated.

`iex` leaves no way to pass parameters, so the knobs are environment variables:

| Variable | Effect |
| --- | --- |
| `ROSTOOLS_REF` | Branch, tag or commit SHA to install. Default `main` |
| `ROSTOOLS_ADDONS_PATH` | Full path to `_retail_\Interface\AddOns`, if auto-detection misses |
| `ROSTOOLS_KEEP_DATA` | Keep the roster already installed instead of the ref's — for holding one client deliberately stale and watching `Core/Sync.lua` adopt a newer one from a peer |

It installs whatever `Data/GuildData.lua` the ref carries and never touches the
`guild-data` branch. `scripts/Deploy-RoSTools.ps1` does the same job from a local
checkout, which is what you want in an edit-test loop.

Notes for anyone extending this:

- WoW runs **Lua 5.1**. No `\u{}` escapes, no integer division, no goto.
- Modules register through `ns:RegisterModule(name, table)` and receive
  `OnInitialize` at `ADDON_LOADED` and `OnEnable` at `PLAYER_LOGIN`.
- SavedVariables hold settings, plus at most one roster snapshot adopted from a
  guildmate (`syncedData`). The item level table is rebuilt on every load from
  whichever of the two carries the newer `generated_epoch`.
- `Modules/Roster.lua` touches Blizzard's Communities UI, which is unstable
  across patches. It searches for the name font string rather than assuming a
  widget path, and no-ops if it can't find one.

---

## Releasing

Two independent release pipelines, both manual — nothing ships from a push to
`main`.

### Addon — CurseForge

`.github/workflows/release.yml` ("Publish to CurseForge") is dispatch-only:

1. Bump `## Version:` in `RoS-Tools.toc`.
2. Add a matching `## <version>` section to `CHANGELOG.md` -- the workflow reads
   that section as the CurseForge changelog and fails if it's missing.
3. Commit and push those two changes to `main`.
4. Run the workflow: Actions tab → **Publish to CurseForge** → **Run workflow**,
   or from the CLI:

```powershell
gh workflow run release.yml -f release_type=release
```

`release_type` is `release`, `beta`, or `alpha`. `game_versions` overrides the
CurseForge game versions that `## Interface:` would otherwise derive — leave it
blank normally. `dry_run` builds and validates the zip without uploading.

It pulls the freshest `Data/GuildData.lua` from the `guild-data` branch at build
time (falling back to whatever's committed on `main` if that copy is actually
newer), packages the zip, and uploads it. Requires the `CURSEFORGE_TOKEN` secret
and the `CURSEFORGE_PROJECT_ID` repository variable.

The addon carries no version tag — `RoS-Tools.toc` is the source of truth, and a
release is just this workflow run.

### Sidecar — GitHub Release

`.github/workflows/sidecar.yml` builds and tests on every push to `main` and every
PR touching `Sidecar/**`, but only **publishes** on a `sidecar-vX.Y.Z` tag:

```powershell
git tag sidecar-v1.1.0
git push origin sidecar-v1.1.0
```

That builds a self-contained, single-file `win-x64` exe, checksums it, and
attaches both to a new GitHub Release named after the tag — the release body
includes the install one-liner. The sidecar's version is independent of the
addon's; don't try to keep them in sync.

The build is unsigned, so every release triggers a SmartScreen warning on first
run. Signing is wired up but commented out in the workflow — add the Azure
Trusted Signing secrets and uncomment the `Sign` step to turn it on.

---

## License

MIT.
