<#
.SYNOPSIS
    Copies this checkout's addon source into the installed WoW AddOns folder.

.DESCRIPTION
    Dev-loop deploy. Not for end users -- guildmates install from CurseForge and
    get roster updates in-game from their peers. This script ships Core\, Modules\,
    Data\, and RoS-Tools.toc -- the same payload CI zips, excluding Tools,
    .git*, and *.md -- into <WoW>\_retail_\Interface\AddOns\RoS-Tools.

    The destination folder is wiped and rewritten each run, so a file you
    deleted or renamed in source does not linger in the installed copy.
    SavedVariables (RoSToolsDB) live under WTF\, not AddOns\, so this never
    touches your settings.

.PARAMETER WowPath
    Root WoW install folder -- the one that directly contains '_retail_'.
    Defaults to 'C:\Program Files (x86)\World of Warcraft'. If nothing is
    found there, falls back to the Windows uninstall registry entry and a
    couple of other common install locations before giving up.

.EXAMPLE
    .\scripts\Deploy-RoSTools.ps1

.EXAMPLE
    .\scripts\Deploy-RoSTools.ps1 -WowPath 'D:\Games\World of Warcraft'
#>

[CmdletBinding()]
param(
    [string] $WowPath = 'C:\Program Files (x86)\World of Warcraft'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Write-Step {
    param([string] $Message)
    Write-Host "  $Message" -ForegroundColor Cyan
}

function Write-Good {
    param([string] $Message)
    Write-Host "  $Message" -ForegroundColor Green
}

function Write-Problem {
    param([string] $Message)
    Write-Host "  $Message" -ForegroundColor Yellow
}

# ----------------------------------------------------------------------
# Verify source: this script lives in <repo>\scripts, so the repo root
# is one level up. Confirm it's actually RoS-Tools before touching anyone's
# AddOns folder.
# ----------------------------------------------------------------------
function Resolve-RepoRoot {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $tocPath  = Join-Path $repoRoot 'RoS-Tools.toc'

    if (-not (Test-Path $tocPath)) {
        throw "RoS-Tools.toc not found at '$repoRoot'. Run this script from scripts\ inside the RoS-Tools checkout."
    }

    foreach ($dir in @('Core', 'Modules', 'Data')) {
        if (-not (Test-Path (Join-Path $repoRoot $dir))) {
            throw "Expected folder '$dir' is missing from '$repoRoot' -- this doesn't look like the RoS-Tools repo."
        }
    }

    return $repoRoot
}

# ----------------------------------------------------------------------
# Verify destination: WowPath must actually be a WoW install, not just
# any folder that happens to exist.
# ----------------------------------------------------------------------
function Resolve-AddOnsRoot {
    param([string] $PreferredRoot)

    $candidates = @($PreferredRoot)

    $registryPaths = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\World of Warcraft'
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\World of Warcraft'
    )
    foreach ($registryPath in $registryPaths) {
        if (-not (Test-Path $registryPath)) { continue }
        $installLocation = (Get-ItemProperty -Path $registryPath -ErrorAction SilentlyContinue).InstallLocation
        if ($installLocation) { $candidates += $installLocation }
    }

    $candidates += @(
        'C:\Program Files\World of Warcraft'
        'C:\World of Warcraft'
    )

    foreach ($candidate in $candidates) {
        if (-not $candidate) { continue }

        # Plain string concatenation, deliberately not Join-Path, and Test-Path
        # with -ErrorAction SilentlyContinue. Join-Path resolves the drive
        # qualifier through the provider and throws "Cannot find drive" for a
        # letter that is not mounted, and Test-Path raises the same error; with
        # $ErrorActionPreference = 'Stop' either one ends the run instead of
        # moving on to the next candidate, so WoW on a detached E: -- or
        # -WowPath 'Z:\WoW' -- died with "Cannot find drive" rather than
        # reaching the friendly throw below. Install-Dev.ps1 does the same.
        $addOnsRoot = ([string] $candidate).TrimEnd('\') + '\_retail_\Interface\AddOns'
        if (Test-Path -LiteralPath $addOnsRoot -ErrorAction SilentlyContinue) { return $addOnsRoot }
    }

    throw "Could not find a WoW '_retail_\Interface\AddOns' folder under '$WowPath'. Pass -WowPath pointing at your WoW install (the folder that directly contains _retail_)."
}

# ----------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------
Write-Host ""
Write-Host "RoS-Tools deploy" -ForegroundColor White

$repoRoot    = Resolve-RepoRoot
$addOnsRoot  = Resolve-AddOnsRoot -PreferredRoot $WowPath
$destination = Join-Path $addOnsRoot 'RoS-Tools'

Write-Step "Source:      $repoRoot"
Write-Step "Destination: $destination"

if (Test-Path $destination) {
    Remove-Item -Path $destination -Recurse -Force
}
New-Item -ItemType Directory -Path $destination -Force | Out-Null

foreach ($dir in @('Core', 'Modules', 'Data')) {
    $copyArgs = @{
        Path        = Join-Path $repoRoot $dir
        Destination = Join-Path $destination $dir
        Recurse     = $true
        Force       = $true
    }
    Copy-Item @copyArgs
}

$tocCopyArgs = @{
    Path        = Join-Path $repoRoot 'RoS-Tools.toc'
    Destination = $destination
    Force       = $true
}
Copy-Item @tocCopyArgs

Write-Good "Deployed. Use /reload in game if you're already logged in."
Write-Host ""
