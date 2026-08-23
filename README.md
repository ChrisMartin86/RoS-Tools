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
└── Sidecar/                    Desktop tray updater (.NET 10)
```

`Tools/` is development-only and is excluded from the packaged addon. `Sidecar/`
ships separately as its own exe — see [Sidecar/README.md](Sidecar/README.md).

---

## Installing

In PowerShell — Windows PowerShell 5.1 or PowerShell 7, either works:

```powershell
irm https://raw.githubusercontent.com/ChrisMartin86/RoS-Tools/main/scripts/Install-RoSTools.ps1 | iex
```

Use `irm`, not `iwr`. On 5.1, `Invoke-WebRequest` hands the response to the
Internet Explorer parsing engine, which Windows 11 no longer ships — the call
fails before the script ever runs. `Invoke-RestMethod` returns the text
directly and has no such dependency.

That finds your WoW install, downloads the current `main`, installs
`Core\`, `Modules\`, `Data\` and `RoS-Tools.toc` into
`_retail_\Interface\AddOns\RoS-Tools`, and pulls the freshest roster from the
`guild-data` branch. Run it again any time to upgrade — settings live under
`WTF\` and are never touched.

If WoW sits under `Program Files`, Windows may refuse the write; the script
says so and you re-run it in an elevated window. Two environment variables
override the defaults, since `iex` leaves no way to pass parameters:

| Variable | Effect |
| --- | --- |
| `ROSTOOLS_ADDONS_PATH` | Full path to `_retail_\Interface\AddOns`, if auto-detection fails |
| `ROSTOOLS_BRANCH` | Install from a branch other than `main` |

Manual install works too: copy the `RoS-Tools` folder into
`World of Warcraft\_retail_\Interface\AddOns\RoS-Tools`. `RoS-Tools.toc` must sit
directly inside that folder — not one level deeper.

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
| `debug` | off | Verbose logging |

---

## Refreshing the data

The addon reads a static table; it does not call out to the internet. Someone
runs the exporter periodically and shares the regenerated `Data/GuildData.lua`.

1. Create an application at <https://develop.battle.net/access/clients>. The
   client credentials flow needs no redirect URI.
2. Export the credentials and run the script:

```powershell
$env:BLIZZARD_CLIENT_ID     = '<client id>'
$env:BLIZZARD_CLIENT_SECRET = '<client secret>'

python .\Tools\fetch_guild_info.py --realm khadgar --guild 'Riddle of Steel'
```

That overwrites `Data/GuildData.lua` in place. Commit it, or hand the file to
guildmates directly.

Useful flags: `--region`, `--min-level`, `--workers`, `--out`.

Characters with private profiles or who have never logged in return no item
level and are skipped; the script reports the count at the end.

---

## How the data gets refreshed

WoW addons cannot reach the internet — the Lua sandbox has no sockets. So the
refresh happens in three hops, and only the last one is inside the game:

```
GitHub Action (daily)  ──►  guild-data branch  ──►  sidecar   ──►  addon
  hits the Blizzard API      holds GuildData.lua     writes it      reads it
                                                     into AddOns    at load
```

Nobody has to do anything by hand. The sidecar sits in the system tray and keeps
the file current; the PowerShell updaters below do the same thing on demand for
anyone who would rather not run a background app.

Note that only the first hop touches Blizzard. One export a day serves the whole
guild — nothing on a guildmate's PC ever calls the API, which is why no client
secret is ever handed out.

### The daily job

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

### The sidecar (recommended)

A small tray app that polls the `guild-data` branch every few hours and drops the
new roster into your addon folder. Download `RoSToolsSidecar.exe` from the
[latest release](https://github.com/ChrisMartin86/RoS-Tools/releases), run it once,
and let it start with Windows. Nothing else to do — the numbers just stay current.

Full details, including what it writes and why it doesn't break any rules:
**[Sidecar/README.md](Sidecar/README.md)**.

It never contacts Blizzard, never touches the game process, and refuses to install
anything that doesn't validate as a real export — same guarantees as the scripts
below, on a timer.

### Refreshing on a PC

If you'd rather not run a background app, one line in PowerShell:

```powershell
irm https://raw.githubusercontent.com/ChrisMartin86/RoS-Tools/main/scripts/Update-RoSToolsData.ps1 | iex
```

`scripts/Update-RoSToolsData.ps1` touches nothing but
`Data\GuildData.lua` in the installed addon — no repo checkout, no Python, no
credentials. Set `ROSTOOLS_ADDON_PATH` if the addon folder isn't found
automatically. Re-run it whenever the numbers look old; `/reload` picks up the
new file without restarting the game.

### Companion updater

`Tools/Update-RoSTools.ps1` is the fuller local version, and what actually
moves the file onto a maintainer's PC.

It picks one of two modes automatically:

| Mode | When | What it does |
| --- | --- | --- |
| Download | Normal case | Pulls `GuildData.lua` from the `guild-data` branch. Needs nothing but PowerShell. This is what everyone including you should use day to day. |
| Export | `BLIZZARD_CLIENT_ID` and `BLIZZARD_CLIENT_SECRET` are set and `fetch_guild_info.py` is present | Skips GitHub and hits the Blizzard API directly. Useful for testing the exporter, or getting data before the next scheduled run. |

Force one with `-Mode Export` or `-Mode Download`.

```powershell
.\Tools\Update-RoSTools.ps1              # refresh in place
.\Tools\Update-RoSTools.ps1 -Launch      # refresh, then start the game
.\Tools\Update-RoSTools.ps1 -Force       # ignore the cached ETag
```

The addon folder is found automatically — via the uninstall registry key,
then the usual install locations. Override with `-AddOnPath`. Point it at a
different fork or branch with `-RepoUrl`.

`Tools\Play-RoSTools.cmd` is a double-clickable wrapper that runs the updater
with `-Launch`. Put a shortcut to it on the desktop and use it instead of the
Battle.net launcher. If the update fails the game still starts — you just get
slightly older numbers.

**On safety.** Downloads are staged to a temp file and validated before
anything is replaced: HTML error pages, truncated files, unbalanced braces,
and files with no character entries are all rejected, and the existing data is
left alone. One `.bak` copy is kept beside the installed file.

Guildmates don't need it — the one-liner above covers them. `Play-RoSTools.cmd`
is still worth handing to anyone who'd rather launch the game through a
desktop shortcut than run the updater by hand.

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
```

CI runs the same lint and packages a release zip on every push.

Notes for anyone extending this:

- WoW runs **Lua 5.1**. No `\u{}` escapes, no integer division, no goto.
- Modules register through `ns:RegisterModule(name, table)` and receive
  `OnInitialize` at `ADDON_LOADED` and `OnEnable` at `PLAYER_LOGIN`.
- SavedVariables hold settings only. The item level table ships in the addon
  and is rebuilt from `Data/GuildData.lua` on every load.
- `Modules/Roster.lua` touches Blizzard's Communities UI, which is unstable
  across patches. It searches for the name font string rather than assuming a
  widget path, and no-ops if it can't find one.

---

## License

MIT.
