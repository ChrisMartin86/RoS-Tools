# =============================================================================
#  RoS-Tools -- development install
#
#  Pulls a branch, tag or commit of the addon straight from GitHub into the
#  installed AddOns folder.
#
#      irm https://raw.githubusercontent.com/ChrisMartin86/RoS-Tools/main/scripts/Install-Dev.ps1 | iex
#
#  THIS IS A DEBUGGING TOOL. It is not how guildmates get the addon -- they
#  install from CurseForge, and their roster keeps itself current through
#  Core/Sync.lua. It is deliberately not linked from the user-facing part of
#  README.md, and it deliberately does not touch the `guild-data` branch: it
#  installs whatever Data/GuildData.lua the ref happens to carry, so there is
#  no second copy of GuildDataValidator to keep in step with anything.
#
#  Knobs, as environment variables -- `iex` leaves no way to pass parameters:
#
#      ROSTOOLS_REF          Branch, tag or commit SHA to install. Default 'main'.
#      ROSTOOLS_ADDONS_PATH  Full path to _retail_\Interface\AddOns, if the
#                            default location and the registry both miss.
#      ROSTOOLS_KEEP_DATA    Any non-empty value keeps the Data\GuildData.lua
#                            that is already installed instead of the one in the
#                            ref. Useful when you want a deliberately stale
#                            roster on one client and watch Core/Sync.lua adopt
#                            a newer one from a peer.
#
#  Rules this file has to keep (see CLAUDE.md):
#    * Windows PowerShell 5.1 as well as 7. TLS 1.2 is set explicitly because
#      5.1 can still negotiate 1.0.
#    * No param() block and no $PSScriptRoot -- piping into iex supplies neither.
#    * The whole body lives inside & { }. iex executes in the CALLER's scope, so
#      an unwrapped script would leave Set-StrictMode, $ErrorActionPreference and
#      every helper function behind in the console it was pasted into.
#    * Self-contained: it dot-sources nothing from the repo.
#    * Document the one-liner as `irm`, never `iwr`. On 5.1 `iwr` routes the
#      response through the IE parsing engine, which Windows 11 does not ship.
# =============================================================================

& {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $repo    = 'ChrisMartin86/RoS-Tools'
    $addon   = 'RoS-Tools'
    $ref     = if ($env:ROSTOOLS_REF) { $env:ROSTOOLS_REF } else { 'main' }
    $payload = @('Core', 'Modules', 'Data', "$addon.toc")

    function Say  { param([string] $m) Write-Host "  $m" }
    function Good { param([string] $m) Write-Host "  $m" -ForegroundColor Green }
    function Bad  { param([string] $m) Write-Host "  $m" -ForegroundColor Red }

    function Resolve-AddOnsPath {
        if ($env:ROSTOOLS_ADDONS_PATH) {
            if (-not (Test-Path -LiteralPath $env:ROSTOOLS_ADDONS_PATH)) {
                throw "ROSTOOLS_ADDONS_PATH points at '$env:ROSTOOLS_ADDONS_PATH', which does not exist."
            }
            return (Resolve-Path -LiteralPath $env:ROSTOOLS_ADDONS_PATH).Path
        }

        $roots = New-Object System.Collections.Generic.List[string]

        # The default install first -- that is the case this script exists for.
        $roots.Add('C:\Program Files (x86)\World of Warcraft')
        $roots.Add('C:\Program Files\World of Warcraft')
        $roots.Add('C:\World of Warcraft')

        # Then the uninstall key, which survives a non-default location.
        foreach ($key in @(
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\World of Warcraft',
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\World of Warcraft'
        )) {
            # No try/catch: an absent key is the normal case, not an error, and
            # StrictMode turns a missing property on the result into a throw.
            $entry = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
            if ($entry -and $entry.PSObject.Properties['InstallLocation']) {
                $location = $entry.InstallLocation
                if ($location) { $roots.Add($location.TrimEnd('\')) }
            }
        }

        # Then the usual suspects, on drives that actually exist. Probing a
        # hardcoded 'E:\...' is not merely a miss: Test-Path against an absent
        # drive letter raises "Cannot find drive", and $ErrorActionPreference
        # is 'Stop', so it would kill the run with a nonsense message on any
        # machine whose WoW is not at the very first candidate.
        foreach ($drive in Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue) {
            $base = $drive.Root.TrimEnd('\')
            $roots.Add("$base\World of Warcraft")
            $roots.Add("$base\Games\World of Warcraft")
            $roots.Add("$base\Program Files (x86)\World of Warcraft")
        }

        foreach ($root in $roots) {
            # Plain string concatenation, deliberately not Join-Path: Join-Path
            # resolves the drive qualifier through the provider and throws
            # "Cannot find drive" for a letter that is not mounted -- which,
            # with $ErrorActionPreference = 'Stop', would end the run instead of
            # moving on to the next candidate. A stale registry InstallLocation
            # naming a detached drive is exactly how that happens.
            $candidate = $root.TrimEnd('\') + '\_retail_\Interface\AddOns'
            if (Test-Path -LiteralPath $candidate -ErrorAction SilentlyContinue) {
                return (Resolve-Path -LiteralPath $candidate).Path
            }
        }

        throw "Could not find _retail_\Interface\AddOns. Set ROSTOOLS_ADDONS_PATH to it and re-run."
    }

    $work     = Join-Path ([IO.Path]::GetTempPath()) ('rostools-' + [Guid]::NewGuid().ToString('N'))
    $zip      = "$work.zip"
    $progress = $ProgressPreference

    try {
        Write-Host ''
        Write-Host "  RoS-Tools -- development install" -ForegroundColor Cyan

        $addons = Resolve-AddOnsPath
        $dest   = Join-Path $addons $addon
        Say "ref     $ref"
        Say "into    $dest"

        # 5.1's progress bar makes Invoke-WebRequest roughly an order of
        # magnitude slower on a file download. Off for the transfer, back after.
        $download = @{
            Uri             = "https://codeload.github.com/$repo/zip/$ref"
            OutFile         = $zip
            UseBasicParsing = $true
        }
        $ProgressPreference = 'SilentlyContinue'
        Invoke-WebRequest @download
        $ProgressPreference = $progress

        New-Item -ItemType Directory -Path $work -Force | Out-Null

        # .NET rather than Expand-Archive. Both work on 5.1, but 5.1's
        # Expand-Archive is markedly slower and lives in an optional module
        # (Microsoft.PowerShell.Archive) whose absence reports as an
        # unrecognised-command error that says nothing useful. On 5.1 the
        # assembly needs loading by name; on 7 it is already there, and
        # Add-Type -AssemblyName would fail on the .NET Framework name.
        if (-not ('System.IO.Compression.ZipFile' -as [type])) {
            Add-Type -AssemblyName System.IO.Compression.FileSystem
        }
        [System.IO.Compression.ZipFile]::ExtractToDirectory($zip, $work)

        # GitHub names the top folder after the ref, and a ref can contain a
        # slash, so find it by what is inside rather than by guessing the name.
        $src = Get-ChildItem -LiteralPath $work -Directory |
               Where-Object { Test-Path -LiteralPath (Join-Path $_.FullName "$addon.toc") } |
               Select-Object -First 1
        if (-not $src) {
            throw "The archive for '$ref' has no $addon.toc at its root. Wrong ref?"
        }

        $version = 'unknown'
        foreach ($line in [IO.File]::ReadAllLines((Join-Path $src.FullName "$addon.toc"))) {
            if ($line -cmatch '^##\s*Version:\s*(.+?)\s*$') { $version = $Matches[1]; break }
        }

        # Optionally hold on to the roster that is already installed. Done
        # before the wipe, from a copy outside the destination.
        $keep = $null
        if ($env:ROSTOOLS_KEEP_DATA) {
            $installed = Join-Path $dest 'Data\GuildData.lua'
            if (Test-Path -LiteralPath $installed) {
                $keep = Join-Path $work 'keep-GuildData.lua'
                Copy-Item -LiteralPath $installed -Destination $keep -Force
                Say 'keeping the roster already installed (ROSTOOLS_KEEP_DATA)'
            } else {
                Say 'ROSTOOLS_KEEP_DATA is set, but nothing is installed yet -- using the ref''s roster'
            }
        }

        # Wipe and rewrite, so a file deleted or renamed in source does not
        # linger in the installed copy. Settings live in WTF\, never here.
        if (Test-Path -LiteralPath $dest) { Remove-Item -LiteralPath $dest -Recurse -Force }
        New-Item -ItemType Directory -Path $dest -Force | Out-Null

        foreach ($item in $payload) {
            $from = Join-Path $src.FullName $item
            if (-not (Test-Path -LiteralPath $from)) {
                throw "'$item' is missing from the archive for '$ref'."
            }
            Copy-Item -LiteralPath $from -Destination $dest -Recurse -Force
        }

        if ($keep) {
            Copy-Item -LiteralPath $keep -Destination (Join-Path $dest 'Data\GuildData.lua') -Force
        }

        # Report what actually landed, not what was supposed to.
        $entries = 0
        $stamp   = 'unknown'
        $data    = Join-Path $dest 'Data\GuildData.lua'
        if (Test-Path -LiteralPath $data) {
            $text    = [IO.File]::ReadAllText($data)
            $entries = ([regex]::Matches($text, '(?m)^\s*\["')).Count
            $match   = [regex]::Match($text, 'generated_at\s*=\s*"([^"]*)"')
            if ($match.Success) { $stamp = $match.Groups[1].Value }
        }

        Write-Host ''
        Good "installed $addon $version"
        Say   "roster    $entries characters, exported $stamp"
        Write-Host ''
        Say '/reload in game, or restart it if it was not running.'
        Write-Host ''
    }
    catch {
        $ProgressPreference = $progress
        Write-Host ''
        Bad $_.Exception.Message

        if ($_.Exception -is [UnauthorizedAccessException] -or
            $_.Exception.Message -match 'Access to the path|is denied') {
            Bad 'WoW is probably under Program Files -- re-run this in an elevated window.'
        }
        Write-Host ''
    }
    finally {
        $ProgressPreference = $progress
        Remove-Item -LiteralPath $zip  -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    }
}
