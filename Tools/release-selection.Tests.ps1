# =============================================================================
#  Tests for scripts/Install-Sidecar.ps1 and scripts/Install-Dev.ps1.
#
#      pwsh -NoProfile -File Tools/release-selection.Tests.ps1
#
#  Two halves.
#
#  1. Pure helpers, lifted out of the shipped scripts by the PowerShell parser
#     rather than copied here -- an installer's whole body lives inside `& { }`
#     so it cannot be dot-sourced, and a hand-copied helper would happily keep
#     passing after the real one regressed.
#
#  2. Sandbox runs of the WHOLE installer against a real directory tree, with
#     the network, the process table and the registry shadowed by functions in
#     this scope (a function beats a cmdlet in command resolution, and the
#     installer's `& { }` body is a CHILD scope, so it inherits them). Failures
#     are injected into the destructive steps, because every serious bug in
#     these two files has been in what happens when a rename fails half way --
#     and none of it is reachable by testing helpers in isolation.
#
#  The file keeps its original name: it is the documented entry point and what
#  CI and CLAUDE.md's verification section invoke.
#
#  What is being pinned down:
#    * `sidecar-v1.4.0-rc1` must NOT be selected over the stable `sidecar-v1.3.0`
#      -- the old code stripped the suffix with -split '[+-]', parsed it as
#      1.4.0, and shipped an RC to everyone running the one-liner.
#    * The later stable `sidecar-v1.4.0` must REPLACE an installed 1.4.0-rc1
#      rather than comparing equal to it and reporting "already up to date".
#    * A stable `1.4.0+build-123` must NOT be mistaken for a pre-release, which
#      would rank it below the identical release and re-download 50 MB per run.
#    * Another release stream (`v2.0.0`) must never be mistaken for a sidecar
#      build, which CLAUDE.md is emphatic about.
#    * Install-Dev must never be left with no addon installed and no way back:
#      not when the swap fails, not when the restore fails too, and not on
#      Ctrl-C -- PowerShell runs `finally` on a stopped pipeline but not `catch`.
#    * Install-Sidecar must never leave a truncated exe where the working one
#      was, must never launch a binary it cannot identify, and must never leave
#      the seeding machine stopped after a failure PAST the swap.
# =============================================================================

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$script:Failures = 0
$script:Total    = 0

function Assert-That {
    param([string] $Name, [bool] $Condition, [string] $Detail = '')
    $script:Total++
    if ($Condition) {
        Write-Host "  PASS  $Name" -ForegroundColor Green
    }
    else {
        $script:Failures++
        Write-Host "  FAIL  $Name" -ForegroundColor Red
        if ($Detail) { Write-Host "        $Detail" -ForegroundColor Red }
    }
}

$repoRoot     = Split-Path -Parent $PSScriptRoot
$sidecarPath  = Join-Path $repoRoot 'scripts/Install-Sidecar.ps1'
$devPath      = Join-Path $repoRoot 'scripts/Install-Dev.ps1'

foreach ($path in @($sidecarPath, $devPath)) {
    if (-not (Test-Path -LiteralPath $path)) { throw "Could not find $path" }
    $parseErrors = $null
    $null = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref] $null, [ref] $parseErrors)
    if ($parseErrors) { throw "$path does not parse: $($parseErrors[0].Message)" }
}

# --- load the real helpers out of the installer ------------------------------
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($sidecarPath, [ref] $null, [ref] $errors)
if ($errors) { throw "Install-Sidecar.ps1 does not parse: $($errors[0].Message)" }

$wanted = @('Get-Comparable', 'Get-TagVersion', 'Test-ReleaseFlag',
            'Select-SidecarRelease', 'ConvertTo-InstalledVersion')
$found  = @{}
foreach ($fn in $ast.FindAll({ $args[0] -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $true)) {
    if ($wanted -contains $fn.Name) {
        $found[$fn.Name] = $fn.Extent.Text
    }
}
foreach ($name in $wanted) {
    if (-not $found.ContainsKey($name)) { throw "Install-Sidecar.ps1 no longer defines $name" }
    . ([scriptblock]::Create($found[$name]))
}

$prefix = 'sidecar-v'

function Get-FakeRelease {
    param([string] $Tag, $Draft = $false, $Prerelease = $false)
    return [pscustomobject]@{ tag_name = $Tag; draft = $Draft; prerelease = $Prerelease }
}

# The list the real API would return: two stable sidecar builds, an RC flagged
# as a prerelease, a second RC that a human forgot to flag, a draft of a version
# newer than anything released, and an addon release from the other stream.
$releases = @(
    Get-FakeRelease -Tag 'v2.0.0'
    Get-FakeRelease -Tag 'sidecar-v1.5.0' -Draft $true
    Get-FakeRelease -Tag 'sidecar-v1.4.0-rc1' -Prerelease $true
    Get-FakeRelease -Tag 'sidecar-v1.4.0-rc2'
    Get-FakeRelease -Tag 'sidecar-v1.3.0'
    Get-FakeRelease -Tag 'sidecar-v1.2.0'
    Get-FakeRelease -Tag 'RoS-Tools-2.1.0'
)

Write-Host ''
Write-Host 'Install-Sidecar release selection' -ForegroundColor Cyan

# --- selection ---------------------------------------------------------------
$picked = Select-SidecarRelease -Releases $releases -Prefix $prefix
Assert-That 'the newest STABLE sidecar release is selected' `
    ($null -ne $picked -and $picked.tag_name -eq 'sidecar-v1.3.0') `
    -Detail ("picked '" + $(if ($picked) { $picked.tag_name } else { '<null>' }) + "', expected 'sidecar-v1.3.0'")

Assert-That 'a prerelease-flagged RC is never selected' `
    ($null -ne $picked -and $picked.tag_name -ne 'sidecar-v1.4.0-rc1')

Assert-That 'an RC that was not flagged is still rejected on its tag suffix' `
    ($null -ne $picked -and $picked.tag_name -ne 'sidecar-v1.4.0-rc2')

Assert-That 'a draft is never selected' `
    ($null -ne $picked -and $picked.tag_name -ne 'sidecar-v1.5.0')

Assert-That 'another release stream is never mistaken for a sidecar build' `
    ($null -ne $picked -and $picked.tag_name -notin @('v2.0.0', 'RoS-Tools-2.1.0'))

# Once the stable 1.4.0 is cut it must win over the 1.3.0 that was selected above.
$withStable = @($releases) + @(Get-FakeRelease -Tag 'sidecar-v1.4.0')
$picked2 = Select-SidecarRelease -Releases $withStable -Prefix $prefix
Assert-That 'the stable 1.4.0 outranks 1.3.0 once it exists' `
    ($null -ne $picked2 -and $picked2.tag_name -eq 'sidecar-v1.4.0') `
    -Detail ("picked '" + $(if ($picked2) { $picked2.tag_name } else { '<null>' }) + "'")

# API order must not decide anything.
$shuffled = @(
    Get-FakeRelease -Tag 'sidecar-v1.2.0'
    Get-FakeRelease -Tag 'sidecar-v1.10.0'
    Get-FakeRelease -Tag 'sidecar-v1.9.0'
)
$picked3 = Select-SidecarRelease -Releases $shuffled -Prefix $prefix
Assert-That '1.10.0 sorts above 1.9.0 (numeric, not lexical)' `
    ($null -ne $picked3 -and $picked3.tag_name -eq 'sidecar-v1.10.0') `
    -Detail ("picked '" + $(if ($picked3) { $picked3.tag_name } else { '<null>' }) + "'")

# Nothing but prereleases and drafts is not "install the RC anyway".
$noStable = @(
    Get-FakeRelease -Tag 'sidecar-v1.4.0-rc1' -Prerelease $true
    Get-FakeRelease -Tag 'sidecar-v1.5.0' -Draft $true
)
Assert-That 'a list with no stable release selects nothing at all' `
    ($null -eq (Select-SidecarRelease -Releases $noStable -Prefix $prefix))

# The installer does NOT hand this an array. It walks the paged releases
# endpoint into a System.Collections.Generic.List[object] and passes that.
# Splatting one of those into the pipeline throws "Argument types do not
# match", so the released code failed on every real multi-page run while every
# test here -- all of which passed arrays -- stayed green.
$asList = New-Object System.Collections.Generic.List[object]
foreach ($r in $withStable) { $asList.Add($r) }
$pickedList = $null
$listError  = ''
try { $pickedList = Select-SidecarRelease -Releases $asList -Prefix $prefix }
catch { $listError = $_.Exception.Message }
Assert-That 'the List[object] the installer actually builds is accepted' `
    ($listError -eq '' -and $null -ne $pickedList -and $pickedList.tag_name -eq 'sidecar-v1.4.0') `
    -Detail ("error '" + $listError + "', picked '" + $(if ($pickedList) { $pickedList.tag_name } else { '<null>' }) + "'")

# --- tag parsing -------------------------------------------------------------
Write-Host ''
Write-Host 'Tag parsing' -ForegroundColor Cyan

Assert-That 'a plain tag parses' `
    ((Get-TagVersion -Tag 'sidecar-v1.3.0' -Prefix $prefix) -eq ([Version] '1.3.0'))

Assert-That 'a prerelease suffix is rejected, not stripped' `
    ($null -eq (Get-TagVersion -Tag 'sidecar-v1.4.0-rc1' -Prefix $prefix))

Assert-That 'build metadata is rejected, not stripped' `
    ($null -eq (Get-TagVersion -Tag 'sidecar-v1.4.0+abc1234' -Prefix $prefix))

Assert-That 'a tag from another stream is rejected' `
    ($null -eq (Get-TagVersion -Tag 'v1.4.0' -Prefix $prefix))

Assert-That 'a non-numeric tag is rejected' `
    ($null -eq (Get-TagVersion -Tag 'sidecar-vlatest' -Prefix $prefix))

# Exactly MAJOR.MINOR.PATCH, which is what the comment and the error text both
# promise. A 4-part tag loses its last part to Get-Comparable's prerelease rank
# slot, so 'sidecar-v1.4.0.7' collapsed to the same comparable as
# 'sidecar-v1.4.0' -- the revision-slot collision the version work was done
# to kill, reintroduced through the tag side.
Assert-That 'a 4-part tag is rejected (it would collide in the revision slot)' `
    ($null -eq (Get-TagVersion -Tag 'sidecar-v1.4.0.7' -Prefix $prefix))

Assert-That 'a 2-part tag is rejected' `
    ($null -eq (Get-TagVersion -Tag 'sidecar-v1.4' -Prefix $prefix))

# --- installed-version parsing ----------------------------------------------
Write-Host ''
Write-Host 'Installed-version parsing' -ForegroundColor Cyan

$stable = ConvertTo-InstalledVersion -Raw '1.4.0'
Assert-That 'a plain ProductVersion parses and is not a pre-release' `
    ($null -ne $stable -and $stable.Version -eq ([Version] '1.4.0') -and -not $stable.IsPrerelease)

$fourPart = ConvertTo-InstalledVersion -Raw '1.4.0.0'
Assert-That 'the 4-part FileVersion fallback parses' `
    ($null -ne $fourPart -and $fourPart.Version -eq ([Version] '1.4.0.0') -and -not $fourPart.IsPrerelease)

$rc = ConvertTo-InstalledVersion -Raw '1.4.0-rc1'
Assert-That 'an -rc1 build is remembered as a pre-release' `
    ($null -ne $rc -and $rc.Version -eq ([Version] '1.4.0') -and $rc.IsPrerelease)

# The half most likely to misfire. `$raw -match '-'` tested the WHOLE string,
# and SemVer build metadata may contain a hyphen: a stable 1.4.0+build-123 was
# flagged a pre-release, ranked 1.4.0.0 against the release's 1.4.0.1, and so
# looked permanently out of date -- 50 MB down the wire on every single run,
# which is precisely the failure CLAUDE.md's normaliser exists to prevent.
$buildMeta = ConvertTo-InstalledVersion -Raw '1.4.0+build-123'
Assert-That 'build metadata containing a hyphen is NOT a pre-release' `
    ($null -ne $buildMeta -and $buildMeta.Version -eq ([Version] '1.4.0') -and -not $buildMeta.IsPrerelease) `
    -Detail ("parsed " + $(if ($buildMeta) { "$($buildMeta.Version) prerelease=$($buildMeta.IsPrerelease)" } else { '<null>' }))

Assert-That 'a stable 1.4.0+build-123 install compares EQUAL to the 1.4.0 release' `
    ((Get-Comparable -V $buildMeta.Version -Prerelease:$buildMeta.IsPrerelease) -eq (Get-Comparable -V ([Version] '1.4.0'))) `
    -Detail 'if it does not, the installer re-downloads the same build on every run'

$both = ConvertTo-InstalledVersion -Raw '1.4.0-rc1+build-123'
Assert-That 'a hyphen BEFORE the plus is still a pre-release' `
    ($null -ne $both -and $both.Version -eq ([Version] '1.4.0') -and $both.IsPrerelease)

Assert-That 'surrounding whitespace is tolerated' `
    ((ConvertTo-InstalledVersion -Raw '  1.4.0  ').Version -eq ([Version] '1.4.0'))

Assert-That 'an unparsable ProductVersion is $null, not a throw' `
    ($null -eq (ConvertTo-InstalledVersion -Raw 'not a version'))

Assert-That 'an empty ProductVersion is $null' `
    ($null -eq (ConvertTo-InstalledVersion -Raw ''))

# --- installed-vs-latest comparison -----------------------------------------
Write-Host ''
Write-Host 'Version comparison' -ForegroundColor Cyan

# This is the second half of the bug: an RC installed by hand reports
# ProductVersion '1.4.0-rc1'. Flattened naively it equals the stable 1.4.0 and
# the script says "already up to date" forever.
$rcInstalled     = Get-Comparable -V ([Version] '1.4.0') -Prerelease
$stableInstalled = Get-Comparable -V ([Version] '1.4.0')
$stableRelease   = Get-Comparable -V ([Version] '1.4.0')

Assert-That 'an installed 1.4.0-rc1 is STRICTLY OLDER than the stable 1.4.0' `
    ($rcInstalled -lt $stableRelease) `
    -Detail ("rc=$rcInstalled stable=$stableRelease")

Assert-That 'an installed stable 1.4.0 still equals the stable 1.4.0 release' `
    ($stableInstalled -eq $stableRelease) `
    -Detail ("installed=$stableInstalled release=$stableRelease")

# The reason Get-Comparable exists at all: FileVersionInfo hands back 4 parts.
Assert-That 'a 4-part FileVersion 1.3.0.0 equals the 3-part tag 1.3.0' `
    ((Get-Comparable -V ([Version] '1.3.0.0')) -eq (Get-Comparable -V ([Version] '1.3.0')))

Assert-That 'a 2-part 1.3 equals the tag 1.3.0' `
    ((Get-Comparable -V ([Version] '1.3')) -eq (Get-Comparable -V ([Version] '1.3.0')))

Assert-That 'an older install is still older' `
    ((Get-Comparable -V ([Version] '1.2.0')) -lt (Get-Comparable -V ([Version] '1.3.0')))

Assert-That 'a newer install is still newer' `
    ((Get-Comparable -V ([Version] '1.5.0')) -gt (Get-Comparable -V ([Version] '1.3.0')))

Assert-That '$null in, $null out' ($null -eq (Get-Comparable -V $null))

# --- release flags -----------------------------------------------------------
Write-Host ''
Write-Host 'Release flags' -ForegroundColor Cyan

Assert-That 'a release object with no draft property does not throw' `
    ((Test-ReleaseFlag ([pscustomobject]@{ tag_name = 'x' }) 'draft') -eq $false)

Assert-That 'a boolean $true flag is true' `
    ((Test-ReleaseFlag ([pscustomobject]@{ draft = $true }) 'draft') -eq $true)

# [bool] 'false' is $TRUE in PowerShell -- every non-empty string is. A payload
# whose flags arrive as strings therefore marked EVERY release a draft, and the
# installer reported "No stable sidecar-v* release found" against a repo full
# of them.
Assert-That 'the string "false" is false, not $true' `
    ((Test-ReleaseFlag ([pscustomobject]@{ draft = 'false' }) 'draft') -eq $false)

Assert-That 'the string "true" is still true' `
    ((Test-ReleaseFlag ([pscustomobject]@{ draft = 'true' }) 'draft') -eq $true)

Assert-That 'a $null flag is false' `
    ((Test-ReleaseFlag ([pscustomobject]@{ draft = $null }) 'draft') -eq $false)

$stringFlagged = @(
    [pscustomobject]@{ tag_name = 'sidecar-v1.3.0'; draft = 'false'; prerelease = 'false' }
)
Assert-That 'a release whose flags are strings is still selectable' `
    ((Select-SidecarRelease -Releases $stringFlagged -Prefix $prefix).tag_name -eq 'sidecar-v1.3.0')

# =============================================================================
#  Sandbox runs
# =============================================================================
$script:SandboxRoot = Join-Path ([IO.Path]::GetTempPath()) ('rostools-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $script:SandboxRoot -Force | Out-Null

function New-DevSandbox {
    <#
      An AddOns folder holding a real install (roster included, because that is
      the irreplaceable part), an unrelated addon that must never be touched,
      and a real zip shaped the way codeload hands one back.
    #>
    param([string] $Name)

    $root   = Join-Path $script:SandboxRoot $Name
    $addons = Join-Path $root 'AddOns'
    New-Item -ItemType Directory -Path (Join-Path $addons 'RoS-Tools/Data') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $addons 'RoS-Tools/Core') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $addons 'RoS-Tools/Data/GuildData.lua') `
        -Value "ns.GuildData = { ilvls = { [`"Alpha0-khadgar`"] = 600, }, }" -NoNewline
    Set-Content -LiteralPath (Join-Path $addons 'RoS-Tools/Core/Init.lua') -Value '-- old' -NoNewline
    Set-Content -LiteralPath (Join-Path $addons 'RoS-Tools/RoS-Tools.toc') -Value '## Version: 1.0.0' -NoNewline
    New-Item -ItemType Directory -Path (Join-Path $addons 'SomeOtherAddon') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $addons 'SomeOtherAddon/x.toc') -Value '## Version: 9' -NoNewline

    $zipSrc = Join-Path $root 'zipsrc'
    $top    = Join-Path $zipSrc 'RoS-Tools-main'
    New-Item -ItemType Directory -Path (Join-Path $top 'Core') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $top 'Modules') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $top 'Data') -Force | Out-Null
    Set-Content -LiteralPath (Join-Path $top 'Core/Init.lua') -Value '-- new' -NoNewline
    Set-Content -LiteralPath (Join-Path $top 'Modules/Tooltip.lua') -Value '-- new' -NoNewline
    Set-Content -LiteralPath (Join-Path $top 'Data/GuildData.lua') `
        -Value "ns.GuildData = { ilvls = { [`"Alpha0-khadgar`"] = 610, }, }" -NoNewline
    Set-Content -LiteralPath (Join-Path $top 'RoS-Tools.toc') -Value '## Version: 2.1.0' -NoNewline

    if (-not ('System.IO.Compression.ZipFile' -as [type])) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
    }
    [System.IO.Compression.ZipFile]::CreateFromDirectory($zipSrc, (Join-Path $root 'payload.zip'))

    return $root
}

# Install-Dev runs in a CHILD PROCESS, because one of the cases below is
# Ctrl-C. A stopped pipeline cannot be caught -- that is the whole point of it,
# and the reason the recovery messages had to move into `finally` -- so running
# it in-process would take this test file down with it, which is precisely what
# it does to the user's console.
$script:HostExe   = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$script:DevRunner = Join-Path $script:SandboxRoot 'dev-runner.ps1'

Set-Content -LiteralPath $script:DevRunner -Encoding utf8 -Value @'
# Drives the real Install-Dev.ps1 with Move-Item and Invoke-WebRequest shadowed
# in this scope: a function beats a cmdlet, and the installer's `& { }` body is
# a child scope, so it inherits them.
#
# Failures are injected by ROLE rather than by call number, because the
# installer retries each rename:
#   aside    <addon>          -> <addon>.old-<guid>
#   in       <addon>.new-...  -> <addon>
#   restore  <addon>.old-...  -> <addon>
#
# -AsStop throws a PipelineStoppedException rather than an ordinary error.
# PowerShell skips `catch` and runs `finally` for that, exactly as it does for
# Ctrl-C, which is the only way to reach the finally block's rules from a test.
param(
    [Parameter(Mandatory)] [string] $Installer,
    [Parameter(Mandatory)] [string] $Root,
    [string] $FailRoles = '',
    [switch] $AsStop,
    [switch] $KeepData
)

$global:RosFailRoles = @($FailRoles -split ',' | Where-Object { $_ } | ForEach-Object { $_.Trim() })
$global:RosAsStop    = [bool] $AsStop
$global:RosZip       = Join-Path $Root 'payload.zip'

function Invoke-WebRequest {
    param($Uri, $OutFile, [switch] $UseBasicParsing, $Headers)
    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $global:RosZip -Destination $OutFile -Force
}

function Move-Item {
    param([string] $LiteralPath, [string] $Destination, [switch] $Force)
    $role = 'other'
    if     ($LiteralPath -like '*.new-*') { $role = 'in' }
    elseif ($LiteralPath -like '*.old-*') { $role = 'restore' }
    elseif ($Destination -like '*.old-*') { $role = 'aside' }
    if ($global:RosFailRoles -contains $role) {
        if ($global:RosAsStop) { throw (New-Object System.Management.Automation.PipelineStoppedException) }
        throw ("simulated: the " + $role + " rename failed")
    }
    Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
}

$env:ROSTOOLS_ADDONS_PATH = Join-Path $Root 'AddOns'
if ($KeepData) { $env:ROSTOOLS_KEEP_DATA = '1' }
else           { Remove-Item Env:\ROSTOOLS_KEEP_DATA -ErrorAction SilentlyContinue }

Invoke-Expression (Get-Content -LiteralPath $Installer -Raw)
'@

function Invoke-DevInstaller {
    param([string] $Root, [string[]] $FailRoles = @(), [switch] $AsStop, [switch] $KeepData)

    $arguments = @('-NoProfile', '-File', $script:DevRunner,
                   '-Installer', $devPath, '-Root', $Root)
    if ($FailRoles.Count -gt 0) { $arguments += @('-FailRoles', ($FailRoles -join ',')) }
    if ($AsStop)                { $arguments += '-AsStop' }
    if ($KeepData)              { $arguments += '-KeepData' }

    return (& $script:HostExe @arguments 2>&1 | Out-String)
}

function Get-AddOnName {
    param([string] $Root)
    return @(Get-ChildItem -LiteralPath (Join-Path $Root 'AddOns') -Force | ForEach-Object { $_.Name } | Sort-Object)
}

function Test-RosterSurvival {
    <# The roster is the only part of an install a user cannot simply refetch. #>
    param([string] $Root)
    $hits = @(Get-ChildItem -LiteralPath (Join-Path $Root 'AddOns') -Recurse -Force -Filter 'GuildData.lua' -ErrorAction SilentlyContinue)
    return ($hits.Count -gt 0)
}

Write-Host ''
Write-Host 'Install-Dev -- the swap, under injected failure' -ForegroundColor Cyan

# --- 1. it still works ------------------------------------------------------
$s = New-DevSandbox 'dev-happy'
$out = Invoke-DevInstaller -Root $s
$names = Get-AddOnName -Root $s
Assert-That 'a clean run installs the addon and leaves nothing else behind' `
    (($names -join ',') -eq 'RoS-Tools,SomeOtherAddon' -and
     (Get-Content -LiteralPath (Join-Path $s 'AddOns/RoS-Tools/RoS-Tools.toc') -Raw).Contains('2.1.0')) `
    -Detail ("AddOns held: " + ($names -join ', '))

# --- 2. the swap fails, the restore works -----------------------------------
$s = New-DevSandbox 'dev-restored'
$out = Invoke-DevInstaller -Root $s -FailRoles 'in'
$names = Get-AddOnName -Root $s
Assert-That 'a failed swap puts the previous install back' `
    (($names -join ',') -eq 'RoS-Tools,SomeOtherAddon' -and
     (Get-Content -LiteralPath (Join-Path $s 'AddOns/RoS-Tools/RoS-Tools.toc') -Raw).Contains('1.0.0')) `
    -Detail ("AddOns held: " + ($names -join ', '))

Assert-That 'and reports the ORIGINAL failure, not a cleanup one' `
    ($out -match 'the in rename failed') `
    -Detail $out

# --- 3. the swap fails AND the restore fails --------------------------------
# The worst case, and the one the previous fix made worse: the finally deleted
# the backup unconditionally, so the last surviving copy of the addon -- the
# one created specifically so it could be put back -- was removed, and the only
# thing printed was the restore's own exception.
$s = New-DevSandbox 'dev-both-fail'
$out = Invoke-DevInstaller -Root $s -FailRoles 'in', 'restore'
$names = Get-AddOnName -Root $s

Assert-That 'a failed restore does NOT delete the backup' `
    (@($names | Where-Object { $_ -like 'RoS-Tools.old-*' }).Count -eq 1) `
    -Detail ("AddOns held: " + ($names -join ', '))

Assert-That 'and the roster is still on disk somewhere' `
    (Test-RosterSurvival -Root $s) `
    -Detail ("AddOns held: " + ($names -join ', '))

Assert-That 'and the user is told the addon is not installed' `
    ($out -match 'is NOT installed right now') -Detail $out

Assert-That 'and is given the full path to rename back' `
    ($out -match 'RoS-Tools\.old-[0-9a-f]{32}') -Detail $out

Assert-That 'and the ORIGINAL failure is not replaced by the restore''s' `
    ($out -match 'the in rename failed') -Detail $out

Assert-That 'and the restore failure is reported too, not swallowed' `
    ($out -match 'could not be put back') -Detail $out

Assert-That 'the staged copy is still cleaned up' `
    (@($names | Where-Object { $_ -like 'RoS-Tools.new-*' }).Count -eq 0) `
    -Detail ("AddOns held: " + ($names -join ', '))

# --- 4. Ctrl-C between the two renames --------------------------------------
# PowerShell runs `finally` on a stopped pipeline and does NOT run `catch`, so
# every recovery message has to live in the finally to be reachable here.
$s = New-DevSandbox 'dev-ctrl-c'
$out = Invoke-DevInstaller -Root $s -FailRoles 'in' -AsStop
$names = Get-AddOnName -Root $s

Assert-That 'Ctrl-C between the renames does not delete the backup' `
    (@($names | Where-Object { $_ -like 'RoS-Tools.old-*' }).Count -eq 1) `
    -Detail ("AddOns held: " + ($names -join ', '))

Assert-That 'Ctrl-C leaves the roster on disk' `
    (Test-RosterSurvival -Root $s) `
    -Detail ("AddOns held: " + ($names -join ', '))

Assert-That 'Ctrl-C still tells the user where the install went' `
    ($out -match 'is NOT installed right now') -Detail $out

# The rescued roster under ROSTOOLS_KEEP_DATA is the same shape of problem: it
# is kept only while the run has not finished, and the line that says where it
# went used to live in the `catch`, which Ctrl-C never reaches.
$s = New-DevSandbox 'dev-ctrl-c-keep'
$out = Invoke-DevInstaller -Root $s -FailRoles 'in' -AsStop -KeepData
Assert-That 'Ctrl-C names the roster rescued by ROSTOOLS_KEEP_DATA' `
    ($out -match 'was rescued to') -Detail $out

$rescueFile = ($out -split "`n" | Where-Object { $_ -match 'was rescued to (.+)$' } |
               ForEach-Object { $Matches[1].Trim() } | Select-Object -First 1)
Assert-That 'and that file is really there' `
    ($rescueFile -and (Test-Path -LiteralPath $rescueFile)) `
    -Detail ("looked for '" + $rescueFile + "'")
if ($rescueFile) { Remove-Item -LiteralPath $rescueFile -Force -ErrorAction SilentlyContinue }

# --- 5. leftovers -----------------------------------------------------------
$s = New-DevSandbox 'dev-leftovers'
New-Item -ItemType Directory -Path (Join-Path $s 'AddOns/RoS-Tools.old-deadbeef/Data') -Force | Out-Null
New-Item -ItemType Directory -Path (Join-Path $s 'AddOns/RoS-Tools.new-cafef00d') -Force | Out-Null
$out = Invoke-DevInstaller -Root $s
$names = Get-AddOnName -Root $s

Assert-That 'leftovers from earlier runs are swept once the install is in place' `
    (($names -join ',') -eq 'RoS-Tools,SomeOtherAddon') `
    -Detail ("AddOns held: " + ($names -join ', '))

Assert-That 'and the sweep says what it removed' `
    ($out -match 'removed a leftover from an earlier run') -Detail $out

# A '.old-' with NO install beside it is the rescue copy from an interrupted
# run -- the sweep must report it and leave it exactly where it is, including
# through the run that reports it.
$s = New-DevSandbox 'dev-rescue'
Move-Item -LiteralPath (Join-Path $s 'AddOns/RoS-Tools') -Destination (Join-Path $s 'AddOns/RoS-Tools.old-abc123') -Force
$out = Invoke-DevInstaller -Root $s
$names = Get-AddOnName -Root $s

Assert-That 'a rescue copy is never swept away as a leftover' `
    ($names -contains 'RoS-Tools.old-abc123' -and
     (Test-Path -LiteralPath (Join-Path $s 'AddOns/RoS-Tools.old-abc123/Data/GuildData.lua'))) `
    -Detail ("AddOns held: " + ($names -join ', '))

Assert-That 'and it is reported rather than silently kept' `
    ($out -match 'is not installed, but an earlier run left') -Detail $out

# =============================================================================
#  Install-Sidecar sandbox
# =============================================================================
Write-Host ''
Write-Host 'Install-Sidecar -- the swap and the restart, under injected failure' -ForegroundColor Cyan

$script:SidecarText = Get-Content -LiteralPath $sidecarPath -Raw

function New-SidecarSandbox {
    param([string] $Name, [int] $NewSize = 300000)
    $root = Join-Path $script:SandboxRoot $Name
    New-Item -ItemType Directory -Path (Join-Path $root 'install') -Force | Out-Null

    # The build already installed and running.
    $old = Join-Path $root 'install/RoSToolsSidecar.exe'
    [IO.File]::WriteAllBytes($old, [byte[]] (1..2000 | ForEach-Object { 0x4F }))

    # The build the release publishes.
    $new = Join-Path $root 'new.exe'
    $bytes = New-Object byte[] $NewSize
    for ($i = 0; $i -lt $NewSize; $i++) { $bytes[$i] = 0x5A }
    [IO.File]::WriteAllBytes($new, $bytes)

    return [pscustomobject]@{
        Root     = $root
        Target   = $old
        NewExe   = $new
        OldHash  = (Get-FileHash -LiteralPath $old -Algorithm SHA256).Hash
        NewHash  = (Get-FileHash -LiteralPath $new -Algorithm SHA256).Hash
    }
}

function Invoke-SidecarInstaller {
    <#
      Runs the REAL Install-Sidecar.ps1 with the network, the process table and
      the registry shadowed. -TruncateAt simulates a transfer onto a different
      volume that runs out of space part-way: the destination is left short and
      the operation throws, which is exactly what Move-Item does across volumes.
    #>
    param(
        $Box,
        [int]    $TruncateAt = 0,
        [switch] $Running,
        [switch] $KillDenied,
        [switch] $RunKeyThrows,
        [switch] $StartThrows,
        [switch] $CorruptOnRunKey
    )

    $script:RosBox          = $Box
    $script:RosTruncateAt   = $TruncateAt
    $script:RosRunning      = [bool] $Running
    $script:RosKillDenied   = [bool] $KillDenied
    $script:RosRunKeyThrows = [bool] $RunKeyThrows
    $script:RosStartThrows  = [bool] $StartThrows
    $script:RosCorrupt      = [bool] $CorruptOnRunKey
    $script:RosStarts       = New-Object System.Collections.Generic.List[string]

    $env:ROSTOOLS_SIDECAR_PATH  = $Box.Target
    $env:ROSTOOLS_SIDECAR_FORCE = '1'
    Remove-Item Env:\ROSTOOLS_SIDECAR_VERSION -ErrorAction SilentlyContinue
    Remove-Item Env:\ROSTOOLS_SIDECAR_NOSTART -ErrorAction SilentlyContinue

    try {
        $out = & {
            param($Text)

            function Copy-ShortAndFail {
                param([string] $From, [string] $To)
                $all = [IO.File]::ReadAllBytes($From)
                $n = [Math]::Min($script:RosTruncateAt, $all.Length)
                [IO.File]::WriteAllBytes($To, $all[0..($n - 1)])
                throw 'No space left on device'
            }

            function Copy-Item {
                param([string] $LiteralPath, [string] $Destination, [switch] $Force, [switch] $Recurse)
                if ($script:RosTruncateAt -gt 0 -and $Destination -like ($script:RosBox.Target + '*')) {
                    Copy-ShortAndFail -From $LiteralPath -To $Destination
                }
                Microsoft.PowerShell.Management\Copy-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
            }

            function Move-Item {
                param([string] $LiteralPath, [string] $Destination, [switch] $Force)
                # A cross-volume Move-Item is copy-then-delete, so it truncates
                # in exactly the same way the copy above does.
                if ($script:RosTruncateAt -gt 0 -and $Destination -like ($script:RosBox.Target + '*') -and
                    $LiteralPath -notlike ($script:RosBox.Target + '*')) {
                    Copy-ShortAndFail -From $LiteralPath -To $Destination
                }
                Microsoft.PowerShell.Management\Move-Item -LiteralPath $LiteralPath -Destination $Destination -Force:$Force
            }

            function Invoke-RestMethod {
                param($Uri, $Headers, [switch] $UseBasicParsing)
                $assets = @(
                    [pscustomobject]@{ name = 'RoSToolsSidecar.exe'; browser_download_url = 'https://example/exe' }
                    [pscustomobject]@{ name = 'RoSToolsSidecar.exe.sha256'; browser_download_url = 'https://example/sha' }
                )
                if ("$Uri" -match 'page=1(&|$)' -or "$Uri" -match '/releases/tags/') {
                    return @([pscustomobject]@{ tag_name = 'sidecar-v1.4.0'; draft = $false
                                                prerelease = $false; assets = $assets })
                }
                return @()
            }

            function Invoke-WebRequest {
                param($Uri, $OutFile, [switch] $UseBasicParsing, $Headers)
                if ("$Uri" -match 'sha') {
                    [IO.File]::WriteAllText($OutFile, ($script:RosBox.NewHash.ToLowerInvariant() + '  RoSToolsSidecar.exe'))
                }
                else {
                    Microsoft.PowerShell.Management\Copy-Item -LiteralPath $script:RosBox.NewExe -Destination $OutFile -Force
                }
            }

            function Get-Process {
                param($Name, $ErrorAction)
                if (-not $script:RosRunning) { return @() }
                $p = [pscustomobject]@{ Id = 4242; Path = $script:RosBox.Target
                                        HasExited = $false; Denied = $script:RosKillDenied }
                $p | Add-Member -MemberType ScriptMethod -Name Kill -Value {
                    if ($this.Denied) { throw 'Access is denied' }
                    $this.HasExited = $true
                }
                $p | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($ms) return $true }
                return @($p)
            }

            function Get-ItemProperty {
                param($LiteralPath, $ErrorAction)
                if ("$LiteralPath" -like 'HKCU:*') {
                    return [pscustomobject]@{ 'RoS-Tools Sidecar' = '"C:\elsewhere\RoSToolsSidecar.exe"' }
                }
                return $null
            }

            function Set-ItemProperty {
                param($LiteralPath, $Name, $Value)
                if ($script:RosCorrupt) {
                    # Something else mangles the exe after the swap. The catch
                    # must refuse to launch what it can no longer identify.
                    [IO.File]::WriteAllBytes($script:RosBox.Target, [byte[]] (1..16 | ForEach-Object { 0xFF }))
                }
                if ($script:RosRunKeyThrows -or $script:RosCorrupt) {
                    throw 'Requested registry access is not allowed.'
                }
            }

            function Start-Process {
                param([string] $FilePath)
                $script:RosStarts.Add($FilePath)
                if ($script:RosStartThrows) { throw 'This app has been blocked by your system administrator.' }
            }

            Invoke-Expression $Text
        } $script:SidecarText 6>&1 5>&1 4>&1 3>&1 | Out-String
    }
    finally {
        Remove-Item Env:\ROSTOOLS_SIDECAR_PATH  -ErrorAction SilentlyContinue
        Remove-Item Env:\ROSTOOLS_SIDECAR_FORCE -ErrorAction SilentlyContinue
    }
    return $out
}

function Get-TargetState {
    param($Box)
    if (-not (Test-Path -LiteralPath $Box.Target)) { return 'missing' }
    $h = (Get-FileHash -LiteralPath $Box.Target -Algorithm SHA256).Hash
    if ($h -eq $Box.OldHash) { return 'old' }
    if ($h -eq $Box.NewHash) { return 'new' }
    return 'fragment'
}

function Get-InstallDirName {
    param($Box)
    return @(Get-ChildItem -LiteralPath (Split-Path -Parent $Box.Target) -Force | ForEach-Object { $_.Name } | Sort-Object)
}

# --- 1. it still works ------------------------------------------------------
$box = New-SidecarSandbox 'sc-happy'
$out = Invoke-SidecarInstaller -Box $box -Running
Assert-That 'a clean run installs the new build and starts it' `
    ((Get-TargetState -Box $box) -eq 'new' -and $script:RosStarts.Count -eq 1) `
    -Detail ("target=" + (Get-TargetState -Box $box) + " starts=" + $script:RosStarts.Count + "`n" + $out)

Assert-That 'and leaves no .new sibling behind' `
    (((Get-InstallDirName -Box $box) -join ',') -eq 'RoSToolsSidecar.exe') `
    -Detail ((Get-InstallDirName -Box $box) -join ', ')

# --- 2. a cross-volume transfer that runs out of space ----------------------
# Move-Item from %TEMP% to another volume is copy-then-delete. Cut short, it
# left a TRUNCATED exe where the working one had been, $moved false -- and the
# catch saw Test-Path succeed and launched the fragment.
$box = New-SidecarSandbox 'sc-nospace'
$out = Invoke-SidecarInstaller -Box $box -Running -TruncateAt 100000
$state = Get-TargetState -Box $box

Assert-That 'a transfer that runs out of space does not touch the installed exe' `
    ($state -eq 'old') -Detail ("target is '" + $state + "'`n" + $out)

Assert-That 'and the sidecar that was stopped is started again' `
    ($script:RosStarts.Count -eq 1 -and $script:RosStarts[0] -eq $box.Target) `
    -Detail ("starts=" + $script:RosStarts.Count)

Assert-That 'and no half-written sibling is left lying around' `
    (((Get-InstallDirName -Box $box) -join ',') -eq 'RoSToolsSidecar.exe') `
    -Detail ((Get-InstallDirName -Box $box) -join ', ')

Assert-That 'and the message says nothing was overwritten' `
    ($out -match 'Nothing was overwritten') -Detail $out

# --- 3. failures AFTER the swap ---------------------------------------------
# `if ($wasRunning -and -not $moved)` skipped the restart for every one of
# these, which is the same "left stopped" outcome the block exists to prevent.
$box = New-SidecarSandbox 'sc-runkey'
$out = Invoke-SidecarInstaller -Box $box -Running -RunKeyThrows
Assert-That 'a Run-key failure after the swap still restarts the sidecar' `
    ((Get-TargetState -Box $box) -eq 'new' -and $script:RosStarts.Count -eq 1) `
    -Detail ("target=" + (Get-TargetState -Box $box) + " starts=" + $script:RosStarts.Count + "`n" + $out)

$box = New-SidecarSandbox 'sc-startblocked'
$out = Invoke-SidecarInstaller -Box $box -Running -StartThrows
Assert-That 'a blocked Start-Process says to start it by hand, and names it' `
    ($out -match 'start it by hand' -and $out.Contains($box.Target)) -Detail $out

# --- 4. a kill that was denied ----------------------------------------------
# $wasRunning is already true when Kill() was refused, so the old restart
# launched a second instance beside a survivor -- and Program.cs's mutex is
# Local\, so it would not catch one in another Terminal Services session.
$box = New-SidecarSandbox 'sc-killdenied'
$out = Invoke-SidecarInstaller -Box $box -Running -KillDenied -RunKeyThrows
Assert-That 'a denied kill means no second instance is launched' `
    ($script:RosStarts.Count -eq 0) -Detail ("starts=" + $script:RosStarts.Count + "`n" + $out)

Assert-That 'and the maintainer is told why, and how to start it' `
    ($out -match 'may still be running' -and $out -match 'start it by hand') -Detail $out

# --- 5. refuse to launch a binary we cannot identify ------------------------
$box = New-SidecarSandbox 'sc-corrupt'
$out = Invoke-SidecarInstaller -Box $box -Running -CorruptOnRunKey
Assert-That 'a file matching neither build is never launched' `
    ((Get-TargetState -Box $box) -eq 'fragment' -and $script:RosStarts.Count -eq 0) `
    -Detail ("target=" + (Get-TargetState -Box $box) + " starts=" + $script:RosStarts.Count + "`n" + $out)

Assert-That 'and the user is told not to run it' `
    ($out -match 'do NOT run it') -Detail $out

Remove-Item -LiteralPath $script:SandboxRoot -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ''
if ($script:Failures -eq 0) {
    Write-Host "$($script:Total) assertions, all passed." -ForegroundColor Green
    exit 0
}
Write-Host "$($script:Failures) of $($script:Total) assertions FAILED." -ForegroundColor Red
exit 1
