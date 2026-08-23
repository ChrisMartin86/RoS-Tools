<#
    RoS-Tools data updater -- designed to be run as a one-liner:

        irm https://raw.githubusercontent.com/ChrisMartin86/RoS-Tools/main/scripts/Update-RoSToolsData.ps1 | iex

    Refreshes nothing but Data\GuildData.lua in the installed addon. The
    addon code itself is left alone; use Install-RoSTools.ps1 for that. The
    new file only replaces the old one if it validates, so a 404 page or a
    dropped connection can never wipe a working roster.

    Windows PowerShell 5.1 and PowerShell 7+ both work.

    Two things follow from being run through `iex`:

      * There is no param() block and no $PSScriptRoot -- `iex` supplies
        neither. The one knob is an environment variable:

            $env:ROSTOOLS_ADDON_PATH   full path to the installed RoS-Tools folder

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

    # The guild-data branch is rewritten daily by the Guild data workflow and
    # holds nothing but the payload, so this URL is stable.
    $dataUrl = 'https://raw.githubusercontent.com/ChrisMartin86/RoS-Tools/guild-data/GuildData.lua'

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
    # Locating the installed addon
    # ------------------------------------------------------------------
    function Find-AddOn {
        if ($env:ROSTOOLS_ADDON_PATH) {
            if (-not (Test-Path (Join-Path $env:ROSTOOLS_ADDON_PATH 'RoS-Tools.toc'))) {
                throw "ROSTOOLS_ADDON_PATH '$env:ROSTOOLS_ADDON_PATH' has no RoS-Tools.toc in it."
            }
            return (Resolve-Path $env:ROSTOOLS_ADDON_PATH).Path
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
            $candidate = Join-Path $root '_retail_\Interface\AddOns\RoS-Tools'
            if (Test-Path (Join-Path $candidate 'RoS-Tools.toc')) { return $candidate }
        }

        throw "Could not find an installed RoS-Tools addon. Install it first, or set `$env:ROSTOOLS_ADDON_PATH."
    }

    # ------------------------------------------------------------------
    # Validation -- the important part
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
        if ($content -notmatch 'ilvls\s*=')         { return @{ Ok = $false; Reason = 'no ilvls table' } }

        $open  = ([regex]::Matches($content, '\{')).Count
        $close = ([regex]::Matches($content, '\}')).Count
        if ($open -ne $close) { return @{ Ok = $false; Reason = 'unbalanced braces -- truncated' } }

        $entries = ([regex]::Matches($content, '\["[^"]+"\]\s*=\s*\d+')).Count
        if ($entries -lt 1) { return @{ Ok = $false; Reason = 'no character entries found' } }

        $generated = $null
        if ($content -match 'generated_at\s*=\s*"([^"]+)"') { $generated = $Matches[1] }

        return @{ Ok = $true; Entries = $entries; GeneratedAt = $generated }
    }

    # ------------------------------------------------------------------
    # Main
    # ------------------------------------------------------------------
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "GuildData-$([guid]::NewGuid().ToString('N')).lua"

    try {
        Write-Host ""
        Write-Host "RoS-Tools data update" -ForegroundColor White

        $addOn       = Find-AddOn
        $destination = Join-Path $addOn 'Data\GuildData.lua'
        Write-Note "Addon: $addOn"

        Write-Step "Downloading the latest roster..."
        $requestArgs = @{
            Uri             = $dataUrl
            OutFile         = $staging
            UseBasicParsing = $true
            TimeoutSec      = 30
        }
        Invoke-WebRequest @requestArgs

        $check = Test-GuildData -Path $staging
        if (-not $check.Ok) {
            throw "Refusing to install the new file: $($check.Reason). Your existing data is untouched."
        }

        $dataDir = Split-Path -Parent $destination
        if (-not (Test-Path $dataDir)) { New-Item -ItemType Directory -Path $dataDir -Force | Out-Null }

        # Keep one rollback copy.
        if (Test-Path $destination) { Copy-Item -Path $destination -Destination "$destination.bak" -Force }

        Move-Item -Path $staging -Destination $destination -Force

        $summary = "$($check.Entries) characters"
        if ($check.GeneratedAt) {
            $summary += ", exported $($check.GeneratedAt)"

            # [ref] needs a properly typed variable, not an untyped $null.
            [datetime] $exportDate = [datetime]::MinValue
            if ([datetime]::TryParse($check.GeneratedAt, [ref] $exportDate)) {
                $age = [int] ((Get-Date) - $exportDate).TotalDays
                $summary += " ($age day$(if ($age -eq 1) { '' } else { 's' }) old)"
            }
        }

        Write-Good $summary
        Write-Good "Installed. Use /reload in game if you're already logged in."
    }
    catch {
        Write-Problem "Update failed: $($_.Exception.Message)"
    }
    finally {
        Remove-Item -Path $staging -Force -ErrorAction SilentlyContinue
    }

    Write-Host ""
}
