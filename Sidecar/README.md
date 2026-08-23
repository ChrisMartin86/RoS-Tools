# RoS-Tools Sidecar

A small Windows tray app that keeps the addon's guild roster current so nobody has
to remember to run the updater.

It polls the `guild-data` branch every few hours, checks that what came back is
actually a generated `GuildData.lua`, and drops it into your installed addon. If
you are already logged in, `/reload` picks it up; otherwise your next launch does.

## Install

1. Download `RoSToolsSidecar.exe` from the
   [latest sidecar release](https://github.com/ChrisMartin86/RoS-Tools/releases).
2. Put it somewhere it can live — `%LOCALAPPDATA%\RoS-Tools` is a fine home.
3. Run it. It finds your WoW install, shows you where, and offers to start with
   Windows.

Install the addon first — the sidecar updates RoS-Tools, it does not install it.

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
| **Settings…** | Addon path, how often to check, start with Windows |
| **Quit** | Stop it until next login |

The top line of the menu is the status: how many characters are installed and when
they last changed. Updates are installed silently — there are no pop-ups — so if
something is wrong the icon picks up a red dot and the tooltip says why. **Open log
folder** in Settings has the detail.

## Is this against the rules?

No. It never touches the game — no memory access, no injection, no automation, no
reading WoW's files while it runs. It downloads a text file and writes it into
`Interface\AddOns\RoS-Tools\Data\`, which is the same thing
`Tools\Update-RoSTools.ps1` has always done, just on a timer. The TSM Desktop App,
the Raider.IO client and WeakAuras Companion all work this way.

It also never contacts Blizzard. Roster data comes from the Blizzard Community API
via the daily `Guild data` workflow, which is the single API consumer; the sidecar
only reads what that workflow publishes. That keeps the client secret off your
machine and the call volume at one export a day for the whole guild rather than
~180 calls per person per check.

World of Warcraft is a trademark of Blizzard Entertainment, Inc. This is an
unofficial fan tool, free and open source, not affiliated with or endorsed by
Blizzard.

## What it writes

| Path | What |
|---|---|
| `…\AddOns\RoS-Tools\Data\GuildData.lua` | The roster — the only file it installs |
| `…\Data\GuildData.lua.bak` | One-generation rollback of the previous roster |
| `%LOCALAPPDATA%\RoS-Tools\sidecar.json` | Settings and the cached ETag |
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
roster if installed. **The same rules exist in three PowerShell copies** —
`Tools\Update-RoSTools.ps1`, `scripts\Install-RoSTools.ps1` and
`scripts\Update-RoSToolsData.ps1` — because the two `scripts\` ones are piped into
`iex` and cannot dot-source anything. A bug found in one must be fixed in all four.

Releases are cut by pushing a `sidecar-vX.Y.Z` tag. The sidecar's version is
independent of the addon's `## Version:` in `RoS-Tools.toc`.
