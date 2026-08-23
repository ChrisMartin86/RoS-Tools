<#
.SYNOPSIS
    Copies this checkout's addon source into the installed WoW AddOns folder.

.DESCRIPTION
    Dev-loop deploy, not the end-user updater (that's Tools\Update-Riddled.ps1,
    which only refreshes Data\GuildData.lua). This script ships Core\, Modules\,
    Data\, and Riddled.toc -- the same payload CI zips, excluding Tools,
    .git*, and *.md -- into <WoW>\_retail_\Interface\AddOns\Riddled.

    The destination folder is wiped and rewritten each run, so a file you
    deleted or renamed in source does not linger in the installed copy.
    SavedVariables (RiddledDB) live under WTF\, not AddOns\, so this never
    touches your settings.

.PARAMETER WowPath
    Root WoW install folder -- the one that directly contains '_retail_'.
    Defaults to 'C:\Program Files (x86)\World of Warcraft'. If nothing is
    found there, falls back to the Windows uninstall registry entry and a
    couple of other common install locations before giving up.

.EXAMPLE
    .\scripts\Deploy-Riddled.ps1

.EXAMPLE
    .\scripts\Deploy-Riddled.ps1 -WowPath 'D:\Games\World of Warcraft'
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
# is one level up. Confirm it's actually Riddled before touching anyone's
# AddOns folder.
# ----------------------------------------------------------------------
function Resolve-RepoRoot {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $tocPath  = Join-Path $repoRoot 'Riddled.toc'

    if (-not (Test-Path $tocPath)) {
        throw "Riddled.toc not found at '$repoRoot'. Run this script from scripts\ inside the Riddled-2.0 checkout."
    }

    foreach ($dir in @('Core', 'Modules', 'Data')) {
        if (-not (Test-Path (Join-Path $repoRoot $dir))) {
            throw "Expected folder '$dir' is missing from '$repoRoot' -- this doesn't look like the Riddled-2.0 repo."
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

        $addOnsRoot = Join-Path $candidate '_retail_\Interface\AddOns'
        if (Test-Path $addOnsRoot) { return $addOnsRoot }
    }

    throw "Could not find a WoW '_retail_\Interface\AddOns' folder under '$WowPath'. Pass -WowPath pointing at your WoW install (the folder that directly contains _retail_)."
}

# ----------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------
Write-Host ""
Write-Host "Riddled deploy" -ForegroundColor White

$repoRoot    = Resolve-RepoRoot
$addOnsRoot  = Resolve-AddOnsRoot -PreferredRoot $WowPath
$destination = Join-Path $addOnsRoot 'Riddled'

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
    Path        = Join-Path $repoRoot 'Riddled.toc'
    Destination = $destination
    Force       = $true
}
Copy-Item @tocCopyArgs

Write-Good "Deployed. Use /reload in game if you're already logged in."
Write-Host ""
