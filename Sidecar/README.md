# RoS-Tools Sidecar

A small Windows tray app that keeps the addon's guild roster current on **one or
two machines** — the addon maintainer's and the guild leader's.

It polls the `guild-data` branch every few hours, checks that what came back is
actually a generated `GuildData.lua`, and drops it into the installed addon. If
you are already logged in, `/reload` picks it up; otherwise your next launch does.

It also has a [data console](#the-data-console) — a local web page for looking at
the roster you have, and, if you supply your own Blizzard API credentials, for
pulling a fresh one on demand.

## Who needs this

Almost nobody. RoS-Tools shares rosters between guildmates in-game: whoever holds
the newest export announces it, and every other client asks for it and adopts it.
A guildmate who installs the addon from CurseForge and never runs anything else
stays current on their own.

What that mechanism needs is a **seed** — at least one client in the guild
actually holding the fresh file. This app is the seed. Run it on the maintainer's
PC, maybe the guild leader's as a second, and stop there; handing it out
guild-wide buys nothing but redundant polling of the same branch.

## Install

1. Download `RoSToolsSidecar.exe` from the
   [latest sidecar release](https://github.com/ChrisMartin86/RoS-Tools/releases).
2. Put it somewhere it can live — `%LOCALAPPDATA%\RoS-Tools` is a fine home.
3. Run it. It finds your WoW install, shows you where, and offers to start with
   Windows.

Install the addon first — the sidecar refreshes the roster inside RoS-Tools, it
does not install the addon. CurseForge does that.

**Windows will warn you the first time.** The build is unsigned, so SmartScreen
shows *"Windows protected your PC"*. Click **More info**, then **Run anyway**. If
you would rather verify than trust, each release ships a
`RoSToolsSidecar.exe.sha256` alongside the exe.

## Using it

Everything lives in the tray icon:

| | |
|---|---|
| **Check now** | Force a check instead of waiting for the timer |
| **Open addon folder** | Jump to the RoS-Tools folder it is writing into |
| **Data console…** | Open the web console: see the roster, pull a fresh one |
| **Settings…** | Addon path, how often to check, start with Windows |
| **Quit** | Stop it until next login |

The top line of the menu is the status: how many characters are installed and when
they last changed. Updates are installed silently — there are no pop-ups — so if
something is wrong the icon picks up a red dot and the tooltip says why. **Open log
folder** in Settings has the detail.

The icon also goes red when nothing is *wrong* but something needs looking at:
checks have stopped happening, or the installed roster has aged past the point
where guildmates will still accept it from you. That second one matters because
the addon shares rosters between guildmates — past about 90 days nobody will take
yours, and without a warning the only symptom is that sharing quietly stops.

## The data console

**Data console…** opens a page in your browser showing the roster that is
installed — every character, item level, when it was exported, and how close it is
to the size limit for sharing. That much needs nothing but the sidecar.

If you add Blizzard API credentials, the console can also pull the roster straight
from Blizzard rather than waiting for the daily export, review it against what you
already have, and install it.

### Getting credentials

Create an application at
[develop.battle.net/access/clients](https://develop.battle.net/access/clients).
The client-credentials flow the sidecar uses needs no redirect URI and grants no
access to your account — it reads the same public profile data the website shows.
Paste the client ID and secret into the console and pick your region.

The secret is encrypted with Windows DPAPI under your user account before it is
written to `sidecar.json`. Another Windows account on the same machine, or anyone
who copies that file off it, gets ciphertext they cannot use. The console never
sends the secret back to the page — it only ever reports whether one is stored. If
you already have `BLIZZARD_CLIENT_ID` and `BLIZZARD_CLIENT_SECRET` set for
`Tools/fetch_guild_info.py`, the console uses those and stores nothing.

### Pulling

A pull is one roster call plus one per character — about 180 requests against a
36,000/hour limit. **Nothing is written to your addon until you look at the result
and click Install.** The console shows you what changed against what you have:
who is new, who is gone, whose item level moved, and any names too long or odd for
the guild's sharing format to carry.

### Read this before you install a pull

The addon shares rosters between guildmates, and the newest export wins. When you
install a pull, your client announces it and hands it to every guildmate that asks
— so a pull that came back short does not just affect you, it replaces good data
for the whole guild.

That is a real risk, not a theoretical one: a few throttled requests or a batch of
private profiles produces a perfectly valid file that is simply missing people.
So the console refuses to install a roster more than 20% smaller than the one you
already have unless you explicitly tick the override. If you hit that, **pull
again first** — it usually comes back full the second time. Only override it when
the guild really did lose that many members.

Everything else the sidecar refuses a download for, it refuses a pull for too, and
for the same reasons. A pull for a guild other than the one this machine carries
is rejected before it spends a single API call.

### Why it is safe to leave running

The console listens on `localhost` only, on a random port, and it is off until you
open it. Every request needs a session token that is minted fresh each time the
app starts and never leaves your machine. The link the tray hands your browser
carries a separate single-use token that is burned the moment the page loads, so
the URL is worthless afterwards.

The background poller does not touch any of this. It still reads only the
published `guild-data` branch, on the same schedule, whether or not you ever add
credentials — CI stays the guild's one scheduled API consumer, which is what keeps
the call volume flat no matter how many people install the sidecar.

## What it refuses to install

The sidecar validates a download before it replaces anything, and since the addon
started sharing rosters between guildmates that check protects the whole guild
rather than just you: whoever installs a file announces it and serves it to
everyone who asks. So it refuses a roster for a different guild, an export dated
in the future or with no date at all, item levels or character names that
guildmates would drop, and anything that is not actually loadable Lua — including
files that look fine on a brace count. A refusal always leaves your existing
roster alone and says why in the tooltip.

The first roster it installs establishes which guild the machine carries; every
later file is checked against that. If you genuinely need to point it somewhere
else, clear `guildRegion` / `guildRealm` / `guildName` in `sidecar.json`.

## Is this against the rules?

No. It never touches the game — no memory access, no injection, no automation, no
reading WoW's files while it runs. It downloads a text file and writes it into
`Interface\AddOns\RoS-Tools\Data\`, on a timer. The TSM Desktop App, the
Raider.IO client and WeakAuras Companion all work the same way.

**Its scheduled polling never contacts Blizzard.** Roster data comes from the
Blizzard Community API via the daily `Guild data` workflow, which is the guild's
single *scheduled* API consumer; the poller only reads what that workflow
publishes. Blizzard is contacted only when a human clicks Pull in the data
console, with credentials that person created themselves. Because this app runs
on one or two machines rather than every guildmate's, that stays a handful of
calls rather than ~180 per person per check.

World of Warcraft is a trademark of Blizzard Entertainment, Inc. This is an
unofficial fan tool, free and open source, not affiliated with or endorsed by
Blizzard.

## What it writes

| Path | What |
|---|---|
| `…\AddOns\RoS-Tools\Data\GuildData.lua` | The roster — the only file it installs |
| `…\Data\GuildData.lua.bak` | One-generation rollback of the previous roster |
| `%LOCALAPPDATA%\RoS-Tools\sidecar.json` | Settings, the learned guild, and a per-destination request cache |
| `%LOCALAPPDATA%\RoS-Tools\logs\` | Rolling log, 512 KB before it rotates |
| `HKCU\…\CurrentVersion\Run` | Only if you turn on start-with-Windows |

To remove it completely: turn off start-with-Windows, quit, delete the exe and
`%LOCALAPPDATA%\RoS-Tools`.

## Development

```powershell
dotnet build .\Sidecar\RoSTools.Sidecar.sln
dotnet test  .\Sidecar\RoSTools.Sidecar.sln
```

Both run on any OS. `Directory.Build.props` sets `EnableWindowsTargeting`, so the
`net10.0-windows` tray project compiles on Linux and macOS too; only running it
needs Windows.

```
src/RoSTools.Sidecar.Core/   net10.0    — all the logic, all the tests
src/RoSTools.Sidecar/        net10.0-windows — WinForms tray and settings window
tests/                       net10.0    — xunit
```

The split is deliberate: everything worth testing lives in `Core` and targets plain
`net10.0`, so the suite runs on `ubuntu-latest` in CI. The Windows project is just
the UI shell.

To produce the release binary locally:

```powershell
$publish = @{
    Configuration = 'Release'
    Runtime       = 'win-x64'
}
dotnet publish .\Sidecar\src\RoSTools.Sidecar\RoSTools.Sidecar.csproj -c $publish.Configuration -r $publish.Runtime --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o .\publish
```

### The part to be careful with

`GuildDataValidator` decides whether a download is allowed to replace a working
roster. GitHub serves an HTML error page for a bad path or a private repo, and a
dropped connection yields a half-written file; either would silently wipe the
roster if installed.

It matters more than that, though. Whatever this machine installs, it announces to
the guild and serves to every peer that asks — so `GuildDataValidator` is a
**guild-wide admission gate**, not a local safety net, and its rules must stay in
step with the ones `Core/Sync.lua` applies to an adopted snapshot. It used to be
duplicated in three PowerShell scripts that were piped into `iex`; those scripts
are gone, and this C# copy is now the only one. Keep it that way.

Releases are cut by pushing a `sidecar-vX.Y.Z` tag. The sidecar's version is
independent of the addon's `## Version:` in `RoS-Tools.toc`.
