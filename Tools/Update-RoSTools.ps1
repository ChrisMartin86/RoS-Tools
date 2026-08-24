<#
.SYNOPSIS
    Refreshes the RoS-Tools guild data file, then optionally launches WoW.

.DESCRIPTION
    Two modes, picked automatically:

      Export   - Blizzard API credentials are present and the Python exporter
                 is reachable. Calls the API directly and writes a fresh
                 Data/GuildData.lua. This is the maintainer path.

      Download - No credentials. Fetches the published GuildData.lua from
                 GitHub and drops it into the installed addon. This is the
                 guildmate path and needs nothing but PowerShell.

    Either way the file only gets replaced if the new copy passes validation,
    so a 404 page or a truncated download can never clobber good data.

.PARAMETER Launch
    Start WoW after updating. The updater never blocks the game -- if the
    refresh fails, it warns and launches anyway.

.PARAMETER Mode
    Force 'Export' or 'Download' instead of auto-detecting.

.PARAMETER AddOnPath
    Path to the installed RoS-Tools addon folder. Auto-detected if omitted.

.PARAMETER Force
    Re-download even if the remote file has not changed since last run.

.EXAMPLE
    .\Update-RoSTools.ps1
    Refresh the data file in place.

.EXAMPLE
    .\Update-RoSTools.ps1 -Launch
    Refresh, then start the game. This is what the shortcut runs.

.EXAMPLE
    .\Update-RoSTools.ps1 -Mode Export -Launch
    Maintainer run: hit the Blizzard API, write the file, play.
#>

[CmdletBinding()]
param(
    [switch] $Launch,

    [ValidateSet('Auto', 'Export', 'Download')]
    [string] $Mode = 'Auto',

    [string] $AddOnPath,

    # The guild-data branch is written daily by the Guild data workflow. It
    # holds nothing but the payload, so this URL is stable.
    [string] $RepoUrl = 'https://raw.githubusercontent.com/ChrisMartin86/RoS-Tools/guild-data/GuildData.lua',

    [string] $Realm = 'khadgar',

    [string] $Guild = 'Riddle of Steel',

    [switch] $Force,

    [switch] $NoColor
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# State lives beside the user's other app data, not in the repo.
$script:StateDir  = Join-Path $env:LOCALAPPDATA 'RoS-Tools'
$script:StateFile = Join-Path $script:StateDir 'updater-state.json'

# ----------------------------------------------------------------------
# Output
# ----------------------------------------------------------------------
function Write-Step {
    param([string] $Message)
    if ($NoColor) { Write-Host "  $Message" }
    else { Write-Host "  $Message" -ForegroundColor Cyan }
}

function Write-Good {
    param([string] $Message)
    if ($NoColor) { Write-Host "  $Message" }
    else { Write-Host "  $Message" -ForegroundColor Green }
}

function Write-Note {
    param([string] $Message)
    if ($NoColor) { Write-Host "  $Message" }
    else { Write-Host "  $Message" -ForegroundColor DarkGray }
}

function Write-Problem {
    param([string] $Message)
    if ($NoColor) { Write-Host "  $Message" }
    else { Write-Host "  $Message" -ForegroundColor Yellow }
}

# ----------------------------------------------------------------------
# Locating things
# ----------------------------------------------------------------------
function Find-WowRoot {
    <#
        Returns the _retail_ folder, or $null. Checks the uninstall registry
        first because that survives non-default install locations, then falls
        back to the usual suspects on every fixed drive.
    #>
    $registryPaths = @(
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\World of Warcraft'
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\World of Warcraft'
    )

    foreach ($registryPath in $registryPaths) {
        if (-not (Test-Path $registryPath)) { continue }

        $installLocation = (Get-ItemProperty -Path $registryPath -ErrorAction SilentlyContinue).InstallLocation
        if (-not $installLocation) { continue }

        $candidate = Join-Path $installLocation '_retail_'
        if (Test-Path $candidate) { return $candidate }
    }

    $drives = Get-PSDrive -PSProvider FileSystem |
        Where-Object { $_.Free -ne $null } |
        Select-Object -ExpandProperty Root

    foreach ($drive in $drives) {
        $candidates = @(
            Join-Path $drive 'Program Files (x86)\World of Warcraft\_retail_'
            Join-Path $drive 'Program Files\World of Warcraft\_retail_'
            Join-Path $drive 'World of Warcraft\_retail_'
            Join-Path $drive 'Games\World of Warcraft\_retail_'
            Join-Path $drive 'Battle.net\World of Warcraft\_retail_'
        )

        foreach ($candidate in $candidates) {
            if (Test-Path $candidate) { return $candidate }
        }
    }

    return $null
}

function Resolve-AddOnPath {
    param([string] $Explicit)

    if ($Explicit) {
        if (-not (Test-Path $Explicit)) {
            throw "AddOnPath '$Explicit' does not exist."
        }
        return (Resolve-Path $Explicit).Path
    }

    # Running from inside a checkout of the repo? Update the checkout.
    # $PSScriptRoot is empty when the script has no file on disk, e.g. when
    # invoked via `irm ... | iex` -- guard against that instead of letting
    # Split-Path throw "Cannot bind argument to parameter 'Path'".
    $repoRoot = if ($PSScriptRoot) { Split-Path -Parent $PSScriptRoot } else { $null }
    if ($repoRoot -and (Test-Path (Join-Path $repoRoot 'RoS-Tools.toc'))) {
        return $repoRoot
    }

    $wowRoot = Find-WowRoot
    if (-not $wowRoot) {
        throw "Could not find World of Warcraft. Pass -AddOnPath pointing at your RoS-Tools folder."
    }

    $addOn = Join-Path $wowRoot 'Interface\AddOns\RoS-Tools'
    if (-not (Test-Path $addOn)) {
        throw "Found WoW at '$wowRoot' but no RoS-Tools addon inside it. Install the addon first."
    }

    return $addOn
}

function Find-WowLauncher {
    $wowRoot = Find-WowRoot
    if ($wowRoot) {
        $exe = Join-Path $wowRoot 'Wow.exe'
        if (Test-Path $exe) { return $exe }
    }

    foreach ($drive in @($env:SystemDrive, 'C:', 'D:')) {
        $battleNet = Join-Path $drive '\Program Files (x86)\Battle.net\Battle.net Launcher.exe'
        if (Test-Path $battleNet) { return $battleNet }
    }

    return $null
}

# ----------------------------------------------------------------------
# Validation -- the important part
# ----------------------------------------------------------------------
function Test-GuildData {
    <#
        A downloaded file is only allowed to replace a working one if it
        actually looks like a generated GuildData.lua. GitHub serves an HTML
        error page for a bad path or a private repo, and a dropped connection
        yields a half-written file; both would otherwise silently wipe the
        roster.
    #>
    param([string] $Path)

    if (-not (Test-Path $Path)) {
        return @{ Ok = $false; Reason = 'file was never written' }
    }

    $bytes = (Get-Item $Path).Length
    if ($bytes -lt 200) {
        return @{ Ok = $false; Reason = "only $bytes bytes -- truncated or empty" }
    }

    $content = Get-Content -Path $Path -Raw -Encoding UTF8

    if ($content -match '^\s*<') {
        return @{ Ok = $false; Reason = 'server returned HTML, not Lua (check the URL and that the repo is public)' }
    }

    if ($content -notmatch 'AUTO-GENERATED') {
        return @{ Ok = $false; Reason = 'missing the generated-file header' }
    }

    if ($content -notmatch 'ns\.GuildData\s*=') {
        return @{ Ok = $false; Reason = 'no ns.GuildData assignment' }
    }

    if ($content -notmatch 'ilvls\s*=') {
        return @{ Ok = $false; Reason = 'no ilvls table' }
    }

    # Balanced-enough check: a truncated file loses its closing braces.
    $open  = ([regex]::Matches($content, '\{')).Count
    $close = ([regex]::Matches($content, '\}')).Count
    if ($open -ne $close) {
        return @{ Ok = $false; Reason = "unbalanced braces ($open open, $close close) -- truncated" }
    }

    $entries = ([regex]::Matches($content, '\["[^"]+"\]\s*=\s*\d+')).Count
    if ($entries -lt 1) {
        return @{ Ok = $false; Reason = 'no character entries found' }
    }

    $generated = $null
    if ($content -match 'generated_at\s*=\s*"([^"]+)"') {
        $generated = $Matches[1]
    }

    return @{ Ok = $true; Entries = $entries; GeneratedAt = $generated }
}

function Show-DataSummary {
    param([hashtable] $Result)

    $summary = "$($Result.Entries) characters"
    if ($Result.GeneratedAt) {
        $summary += ", exported $($Result.GeneratedAt)"

        # [ref] needs a properly typed variable, not an untyped $null.
        [datetime] $exportDate = [datetime]::MinValue
        if ([datetime]::TryParse($Result.GeneratedAt, [ref] $exportDate)) {
            $age = [int] ((Get-Date) - $exportDate).TotalDays
            $summary += " ($age day$(if ($age -eq 1) { '' } else { 's' }) old)"
        }
    }

    Write-Good $summary
}

# ----------------------------------------------------------------------
# State (ETag cache)
# ----------------------------------------------------------------------
function Get-ResponseETag {
    <#
        Windows PowerShell 5.1 exposes Invoke-WebRequest's Headers as
        Dictionary<string,string>; PowerShell 7 exposes it as
        Dictionary<string,string[]>. Neither flavor has a public
        Contains(key) overload -- Dictionary<TKey,TValue> only exposes
        ContainsKey(TKey); Contains(KeyValuePair) is an explicit interface
        implementation that needs a KeyValuePair, not a bare string, so
        calling .Contains('ETag') throws "Cannot find an overload for
        Contains and the argument count: 1". Use ContainsKey instead.
    #>
    param($Response)

    if (-not $Response) { return $null }

    $headers = $Response.Headers
    if (-not $headers) { return $null }

    $value = $null
    if ($headers -is [System.Net.WebHeaderCollection]) {
        $value = $headers['ETag']
    }
    elseif ($headers.PSObject.Methods.Name -contains 'ContainsKey') {
        if ($headers.ContainsKey('ETag')) { $value = $headers['ETag'] }
    }
    else {
        foreach ($entry in $headers.GetEnumerator()) {
            if ($entry.Key -eq 'ETag') { $value = $entry.Value; break }
        }
    }

    if (-not $value) { return $null }
    if ($value -is [string]) { return $value }

    return (@($value) -join '')
}

function Get-State {
    if (-not (Test-Path $script:StateFile)) { return @{} }

    try {
        $raw = Get-Content -Path $script:StateFile -Raw -Encoding UTF8
        $parsed = $raw | ConvertFrom-Json

        $state = @{}
        foreach ($property in $parsed.PSObject.Properties) {
            $state[$property.Name] = $property.Value
        }
        return $state
    }
    catch {
        # A corrupt cache is not worth failing over; just start fresh.
        return @{}
    }
}

function Save-State {
    param([hashtable] $State)

    if (-not (Test-Path $script:StateDir)) {
        New-Item -ItemType Directory -Path $script:StateDir -Force | Out-Null
    }

    $State | ConvertTo-Json -Depth 4 | Set-Content -Path $script:StateFile -Encoding UTF8
}

# ----------------------------------------------------------------------
# Modes
# ----------------------------------------------------------------------
function Get-EffectiveMode {
    param([string] $Requested)

    if ($Requested -ne 'Auto') { return $Requested }

    $hasCredentials = $env:BLIZZARD_CLIENT_ID -and $env:BLIZZARD_CLIENT_SECRET
    $exporter = if ($PSScriptRoot) { Join-Path $PSScriptRoot 'fetch_guild_info.py' } else { $null }

    if ($hasCredentials -and $exporter -and (Test-Path $exporter)) { return 'Export' }

    return 'Download'
}

function Invoke-ExportMode {
    param([string] $Destination)

    $exporter = if ($PSScriptRoot) { Join-Path $PSScriptRoot 'fetch_guild_info.py' } else { $null }
    if (-not $exporter -or -not (Test-Path $exporter)) {
        throw "Export mode needs fetch_guild_info.py next to this script (not available when run via irm | iex)."
    }

    $python = Get-Command python -ErrorAction SilentlyContinue
    if (-not $python) {
        $python = Get-Command python3 -ErrorAction SilentlyContinue
    }
    if (-not $python) {
        throw "Export mode needs Python on PATH."
    }

    if (-not ($env:BLIZZARD_CLIENT_ID -and $env:BLIZZARD_CLIENT_SECRET)) {
        throw "Set BLIZZARD_CLIENT_ID and BLIZZARD_CLIENT_SECRET first."
    }

    # Write to a temp file so a failed export leaves the current data intact.
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "GuildData-$([guid]::NewGuid().ToString('N')).lua"

    Write-Step "Querying the Blizzard API for $Guild on $Realm..."

    $exporterArgs = @(
        $exporter
        '--realm',  $Realm
        '--guild',  $Guild
        '--out',    $staging
    )

    & $python.Source @exporterArgs
    $exporterExit = $LASTEXITCODE

    if ($exporterExit -ne 0) {
        Remove-Item -Path $staging -Force -ErrorAction SilentlyContinue
        throw "Exporter failed with exit code $exporterExit."
    }

    return $staging
}

function Invoke-DownloadMode {
    param([string] $Destination)

    $state = Get-State
    $staging = Join-Path ([System.IO.Path]::GetTempPath()) "GuildData-$([guid]::NewGuid().ToString('N')).lua"

    $headers = @{}
    $cachedTag = $null
    if ($state.ContainsKey('etag')) { $cachedTag = $state['etag'] }

    if ($cachedTag -and -not $Force -and (Test-Path $Destination)) {
        $headers['If-None-Match'] = $cachedTag
    }

    Write-Step "Checking for a newer roster..."

    $requestArgs = @{
        Uri             = $RepoUrl
        OutFile         = $staging
        Headers         = $headers
        UseBasicParsing = $true
        TimeoutSec      = 30
        PassThru        = $true
        ErrorAction     = 'Stop'
    }

    try {
        $response = Invoke-WebRequest @requestArgs

        $etag = Get-ResponseETag -Response $response
        if ($etag) { $state['etag'] = $etag }
        $state['lastCheck'] = (Get-Date).ToString('s')
        Save-State $state
    }
    catch {
        $status = $null
        if ($_.Exception.PSObject.Properties.Name -contains 'Response' -and $_.Exception.Response) {
            $status = [int] $_.Exception.Response.StatusCode
        }

        Remove-Item -Path $staging -Force -ErrorAction SilentlyContinue

        if ($status -eq 304) {
            Write-Note "Already up to date."
            return $null
        }

        if ($status -eq 404) {
            throw "The roster file was not found at $RepoUrl (404). Check the URL and that the repo is public."
        }

        throw "Download failed: $($_.Exception.Message)"
    }

    return $staging
}

# ----------------------------------------------------------------------
# Main
# ----------------------------------------------------------------------
$updateSucceeded = $false

try {
    Write-Host ""
    Write-Host "RoS-Tools updater" -ForegroundColor White

    $addOnPath   = Resolve-AddOnPath -Explicit $AddOnPath
    $destination = Join-Path $addOnPath 'Data\GuildData.lua'
    $effective   = Get-EffectiveMode -Requested $Mode

    Write-Note "Addon:  $addOnPath"
    Write-Note "Mode:   $effective"

    $staging = if ($effective -eq 'Export') {
        Invoke-ExportMode -Destination $destination
    }
    else {
        Invoke-DownloadMode -Destination $destination
    }

    if ($null -eq $staging) {
        # Nothing changed; the file on disk is already current.
        $updateSucceeded = $true
    }
    else {
        $check = Test-GuildData -Path $staging

        if (-not $check.Ok) {
            Remove-Item -Path $staging -Force -ErrorAction SilentlyContinue
            throw "Refusing to install the new file: $($check.Reason). Your existing data is untouched."
        }

        $dataDir = Split-Path -Parent $destination
        if (-not (Test-Path $dataDir)) {
            New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
        }

        # Keep one rollback copy.
        if (Test-Path $destination) {
            Copy-Item -Path $destination -Destination "$destination.bak" -Force
        }

        Move-Item -Path $staging -Destination $destination -Force

        Show-DataSummary -Result $check
        Write-Good "Installed. Use /reload in game if you are already logged in."

        $updateSucceeded = $true
    }
}
catch {
    Write-Problem "Update failed: $($_.Exception.Message)"

    # $destination is only set once the addon folder resolved; if the failure
    # happened before that there is nothing useful to reassure the user about.
    if ((Get-Variable -Name destination -Scope Script -ErrorAction SilentlyContinue) -or
        (Test-Path variable:destination)) {
        if ($destination -and (Test-Path $destination)) {
            Write-Note "Existing roster data is still in place."
        }
    }
}

if ($Launch) {
    $launcher = Find-WowLauncher

    if ($launcher) {
        Write-Step "Launching WoW..."
        Start-Process -FilePath $launcher
    }
    else {
        Write-Problem "Could not find Wow.exe or the Battle.net launcher -- start the game yourself."
    }
}
elseif (-not $updateSucceeded) {
    Write-Host ""
    Read-Host "Press Enter to close"
}

Write-Host ""
