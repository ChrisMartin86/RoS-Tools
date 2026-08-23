<#
    RoS-Tools installer -- designed to be run as a one-liner:

        irm https://raw.githubusercontent.com/ChrisMartin86/RoS-Tools/main/scripts/Install-RoSTools.ps1 | iex

    Downloads the addon source from GitHub, installs Core\, Modules\, Data\
    and RoS-Tools.toc into AddOns\RoS-Tools, then replaces Data\GuildData.lua
    with the freshest copy from the guild-data branch. Nothing is written
    until the download validates, and settings live in WTF\ so a reinstall
    never touches them.

    Windows PowerShell 5.1 and PowerShell 7+ both work.

    Two things follow from being run through `iex`:

      * There is no param() block and no $PSScriptRoot -- `iex` supplies
        neither. The knobs are environment variables instead:

            $env:ROSTOOLS_ADDONS_PATH  full path to ...\_retail_\Interface\AddOns
            $env:ROSTOOLS_BRANCH       source branch (default: main)

      * The whole body runs inside `& { }`. `iex` executes in the caller's
        scope, so without that child scope Set-StrictMode, $ErrorActionPreference
        and every helper function below would leak into the console the
        guildmate typed the one-liner into, and stay there.
#>

& {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'
    $ProgressPreference    = 'SilentlyContinue'

    if ($PSVersionTable.PSVersion.Major -lt 5) {
        Write-Host "RoS-Tools needs Windows PowerShell 5.1 or newer." -ForegroundColor Yellow
        return
    }

    # Windows PowerShell 5.1 can still default to TLS 1.0, which GitHub rejects.
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $repo      = 'ChrisMartin86/RoS-Tools'
    $branch    = if ($env:ROSTOOLS_BRANCH) { $env:ROSTOOLS_BRANCH } else { 'main' }
    $sourceUrl = "https://github.com/$repo/archive/refs/heads/$branch.zip"
    $dataUrl   = "https://raw.githubusercontent.com/$repo/guild-data/GuildData.lua"
    $payload   = @('Core', 'Modules', 'Data')

    function Write-Step {
        param([string] $Message)
        Write-Host "  $Message" -ForegroundColor Cyan
    }

    function Write-Good {
        param([string] $Message)
        Write-Host "  $Message" -ForegroundColor Green
    }

    function Write-Note {
        param([string] $Message)
        Write-Host "  $Message" -ForegroundColor DarkGray
    }

    function Write-Problem {
        param([string] $Message)
        Write-Host "  $Message" -ForegroundColor Yellow
    }

    # ------------------------------------------------------------------
    # Locating the game
    # ------------------------------------------------------------------
    function Find-AddOnsRoot {
        <#
            Registry first -- it survives non-default install locations --
            then the usual suspects on every fixed drive.
        #>
        if ($env:ROSTOOLS_ADDONS_PATH) {
            if (-not (Test-Path $env:ROSTOOLS_ADDONS_PATH)) {
                throw "ROSTOOLS_ADDONS_PATH '$env:ROSTOOLS_ADDONS_PATH' does not exist."
            }
            return (Resolve-Path $env:ROSTOOLS_ADDONS_PATH).Path
        }

        $roots = @()

        $registryPaths = @(
            'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\World of Warcraft'
            'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\World of Warcraft'
        )
        foreach ($registryPath in $registryPaths) {
            if (-not (Test-Path $registryPath)) { continue }
            $installLocation = (Get-ItemProperty -Path $registryPath -ErrorAction SilentlyContinue).InstallLocation
            if ($installLocation) { $roots += $installLocation }
        }

        $drives = Get-PSDrive -PSProvider FileSystem -ErrorAction SilentlyContinue |
            Where-Object { $null -ne $_.Free } |
            Select-Object -ExpandProperty Root

        foreach ($drive in $drives) {
            $roots += @(
                Join-Path $drive 'Program Files (x86)\World of Warcraft'
                Join-Path $drive 'Program Files\World of Warcraft'
                Join-Path $drive 'World of Warcraft'
                Join-Path $drive 'Games\World of Warcraft'
                Join-Path $drive 'Battle.net\World of Warcraft'
            )
        }

        foreach ($root in $roots) {
            if (-not $root) { continue }
            $candidate = Join-Path $root '_retail_\Interface\AddOns'
            if (Test-Path $candidate) { return $candidate }
        }

        throw "Could not find World of Warcraft. Set `$env:ROSTOOLS_ADDONS_PATH to your _retail_\Interface\AddOns folder and run this again."
    }

    function Test-Writable {
        <#
            A WoW install under Program Files may be read-only for a standard
            user. Find that out before wiping the existing addon folder, not
            halfway through the copy.
        #>
        param([string] $Path)

        $probe = Join-Path $Path ".ros-tools-write-test-$([guid]::NewGuid().ToString('N'))"
        try {
            New-Item -ItemType File -Path $probe -Force | Out-Null
            Remove-Item -Path $probe -Force
            return $true
        }
        catch {
            return $false
        }
    }

    # ------------------------------------------------------------------
    # Validation -- a 404 page must never become GuildData.lua
    # ------------------------------------------------------------------
    function Test-GuildData {
        param([string] $Path)

        if (-not (Test-Path $Path)) { return @{ Ok = $false; Reason = 'file was never written' } }

        $bytes = (Get-Item $Path).Length
        if ($bytes -lt 200) { return @{ Ok = $false; Reason = "only $bytes bytes -- truncated or empty" } }

        $content = Get-Content -Path $Path -Raw -Encoding UTF8

        if ($content -match '^\s*<')                { return @{ Ok = $false; Reason = 'server returned HTML, not Lua' } }
        if ($content -notmatch 'AUTO-GENERATED')    { return @{ Ok = $false; Reason = 'missing the generated-file header' } }
        if ($content -notmatch 'ns\.GuildData\s*=') { return @{ Ok = $false; Reason = 'no ns.GuildData assignment' } }

        $open  = ([regex]::Matches($content, '\{')).Count
        $close = ([regex]::Matches($content, '\}')).Count
        if ($open -ne $close) { return @{ Ok = $false; Reason = 'unbalanced braces -- truncated' } }

        $entries = ([regex]::Matches($content, '\["[^"]+"\]\s*=\s*\d+')).Count
        if ($entries -lt 1) { return @{ Ok = $false; Reason = 'no character entries found' } }

        $generated = $null
        if ($content -match 'generated_at\s*=\s*"([^"]+)"') { $generated = $Matches[1] }

        return @{ Ok = $true; Entries = $entries; GeneratedAt = $generated }
    }

    function Get-TocVersion {
        param([string] $Path)

        if (-not (Test-Path $Path)) { return $null }
        $toc = Get-Content -Path $Path -Raw -Encoding UTF8
        if ($toc -match '##\s*Version:\s*(\S+)') { return $Matches[1] }
        return $null
    }

    # ------------------------------------------------------------------
    # Main
    # ------------------------------------------------------------------
    $workspace = Join-Path ([System.IO.Path]::GetTempPath()) "ros-tools-install-$([guid]::NewGuid().ToString('N'))"

    try {
        Write-Host ""
        Write-Host "RoS-Tools installer" -ForegroundColor White

        $addOnsRoot  = Find-AddOnsRoot
        $destination = Join-Path $addOnsRoot 'RoS-Tools'
        Write-Note "AddOns: $addOnsRoot"

        if (-not (Test-Writable -Path $addOnsRoot)) {
            throw "No write access to '$addOnsRoot'. Re-run this in a PowerShell window opened as Administrator."
        }

        New-Item -ItemType Directory -Path $workspace -Force | Out-Null

        # --- source ---------------------------------------------------
        Write-Step "Downloading the addon..."
        $zip = Join-Path $workspace 'source.zip'
        $sourceArgs = @{
            Uri             = $sourceUrl
            OutFile         = $zip
            UseBasicParsing = $true
            TimeoutSec      = 60
        }
        Invoke-WebRequest @sourceArgs

        $extracted = Join-Path $workspace 'src'
        Expand-Archive -Path $zip -DestinationPath $extracted -Force

        $sourceRoot = Get-ChildItem -Path $extracted -Directory |
            Where-Object { Test-Path (Join-Path $_.FullName 'RoS-Tools.toc') } |
            Select-Object -First 1

        if (-not $sourceRoot) { throw "The downloaded archive does not contain RoS-Tools.toc." }

        foreach ($dir in $payload) {
            if (-not (Test-Path (Join-Path $sourceRoot.FullName $dir))) {
                throw "The downloaded archive is missing '$dir'."
            }
        }

        $version = Get-TocVersion -Path (Join-Path $sourceRoot.FullName 'RoS-Tools.toc')

        # --- fresh data -----------------------------------------------
        # Best effort: main's committed copy is a valid fallback, just older.
        Write-Step "Fetching the latest roster..."
        $freshData = Join-Path $workspace 'GuildData.lua'
        $dataCheck = @{ Ok = $false; Reason = 'not attempted' }

        try {
            $dataArgs = @{
                Uri             = $dataUrl
                OutFile         = $freshData
                UseBasicParsing = $true
                TimeoutSec      = 30
            }
            Invoke-WebRequest @dataArgs
            $dataCheck = Test-GuildData -Path $freshData
        }
        catch {
            $dataCheck = @{ Ok = $false; Reason = $_.Exception.Message }
        }

        # --- install --------------------------------------------------
        # Everything above validated, so wiping the old folder is safe now.
        Write-Step "Installing..."
        if (Test-Path $destination) { Remove-Item -Path $destination -Recurse -Force }
        New-Item -ItemType Directory -Path $destination -Force | Out-Null

        foreach ($dir in $payload) {
            $copyArgs = @{
                Path        = Join-Path $sourceRoot.FullName $dir
                Destination = Join-Path $destination $dir
                Recurse     = $true
                Force       = $true
            }
            Copy-Item @copyArgs
        }

        $tocArgs = @{
            Path        = Join-Path $sourceRoot.FullName 'RoS-Tools.toc'
            Destination = $destination
            Force       = $true
        }
        Copy-Item @tocArgs

        # The exporter's .bak files are dev artifacts; they'd load as stray Lua.
        Get-ChildItem -Path $destination -Recurse -Filter '*.bak' -File |
            Remove-Item -Force -ErrorAction SilentlyContinue

        if ($dataCheck.Ok) {
            Copy-Item -Path $freshData -Destination (Join-Path $destination 'Data\GuildData.lua') -Force
            $summary = "$($dataCheck.Entries) characters"
            if ($dataCheck.GeneratedAt) { $summary += ", exported $($dataCheck.GeneratedAt)" }
            Write-Note $summary
        }
        else {
            Write-Problem "Could not refresh the roster ($($dataCheck.Reason)); using the copy bundled with the addon."
        }

        Write-Good "RoS-Tools $version installed to $destination"
        Write-Note "Restart WoW, or /reload if it's already running."
    }
    catch {
        Write-Problem "Install failed: $($_.Exception.Message)"
    }
    finally {
        Remove-Item -Path $workspace -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host ""
}
