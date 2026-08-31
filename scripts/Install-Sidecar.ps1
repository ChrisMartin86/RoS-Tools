# =============================================================================
#  RoS-Tools Sidecar -- install and update
#
#      irm https://raw.githubusercontent.com/ChrisMartin86/RoS-Tools/main/scripts/Install-Sidecar.ps1 | iex
#
#  Installs the sidecar if it is missing and updates it in place if it is not.
#  Finds the newest `sidecar-v*` release, compares it against the version of the
#  exe already on disk, verifies the download against the release checksum, stops
#  the running instance, swaps the exe and starts it again.
#
#  The sidecar is a MAINTAINER tool -- one or two machines, the addon maintainer
#  and the guild leader. Guildmates install the addon from CurseForge and their
#  roster stays current through Core/Sync.lua. Do not hand this out guild-wide.
#
#  Knobs, as environment variables -- `iex` leaves no way to pass parameters:
#
#      ROSTOOLS_SIDECAR_PATH     Where to install. A path ending in .exe is the
#                                exe itself; anything else is the folder.
#                                Default: the running instance's location, else
#                                the Run key's, else %LOCALAPPDATA%\RoS-Tools.
#      ROSTOOLS_SIDECAR_VERSION  Pin a version, e.g. '1.2.0'. Default: newest.
#                                A pin is allowed to move the version backwards.
#      ROSTOOLS_SIDECAR_FORCE    Any non-empty value reinstalls even if the
#                                installed version already matches.
#      ROSTOOLS_SIDECAR_NOSTART  Any non-empty value skips starting it afterward.
#
#  Rules this file has to keep (see CLAUDE.md): Windows PowerShell 5.1 as well as
#  7; no param() block; no $PSScriptRoot; the whole body inside & { } because iex
#  runs in the CALLER's scope; self-contained; TLS 1.2 explicit; `irm`, never
#  `iwr`; and never Join-Path a path whose drive may not exist.
# =============================================================================

& {
    Set-StrictMode -Version Latest
    $ErrorActionPreference = 'Stop'

    [Net.ServicePointManager]::SecurityProtocol =
        [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

    $repo        = 'ChrisMartin86/RoS-Tools'
    $exeName     = 'RoSToolsSidecar.exe'
    $processName = 'RoSToolsSidecar'
    $tagPrefix   = 'sidecar-v'

    # AutoStart.cs owns these two. Mirrored here only so a move can repair a Run
    # key that would otherwise point at an exe that is no longer there.
    $runKey       = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    $runValueName = 'RoS-Tools Sidecar'

    function Say  { param([string] $m) Write-Host "  $m" }
    function Good { param([string] $m) Write-Host "  $m" -ForegroundColor Green }
    function Warn { param([string] $m) Write-Host "  $m" -ForegroundColor Yellow }
    function Bad  { param([string] $m) Write-Host "  $m" -ForegroundColor Red }

    function Get-InstalledVersion {
        param([string] $Exe)
        if (-not (Test-Path -LiteralPath $Exe)) { return $null }
        try {
            $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Exe)
            # -p:Version=1.2.0 lands in ProductVersion, sometimes with a
            # +buildmetadata suffix. FileVersion is the 4-part fallback.
            $raw = $info.ProductVersion
            if (-not $raw) { $raw = $info.FileVersion }
            if (-not $raw) { return $null }
            $raw = ($raw -split '[+-]')[0].Trim()
            $parsed = $null
            if ([Version]::TryParse($raw, [ref] $parsed)) { return $parsed }
            return $null
        }
        catch { return $null }
    }

    # A 2-part and a 4-part version never compare equal to the 3-part tag even
    # when they mean the same build, so compare on major/minor/build only.
    function Get-Comparable {
        param($V)
        if ($null -eq $V) { return $null }
        $build = $V.Build
        if ($build -lt 0) { $build = 0 }
        return (New-Object Version($V.Major, $V.Minor, $build))
    }

    function Resolve-Target {
        if ($env:ROSTOOLS_SIDECAR_PATH) {
            $given = $env:ROSTOOLS_SIDECAR_PATH.Trim().TrimEnd('\')
            if ($given.ToLowerInvariant().EndsWith('.exe')) { return $given }
            return ($given + '\' + $exeName)
        }

        # Prefer wherever it already runs from -- that is the copy being used.
        $running = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
        foreach ($proc in $running) {
            $path = $null
            try { $path = $proc.Path } catch { $path = $null }
            if ($path) { return $path }
        }

        # Then the Run key, so start-with-Windows keeps pointing at what we update.
        $entry = Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue
        if ($entry -and $entry.PSObject.Properties[$runValueName]) {
            $value = [string] $entry.$runValueName
            if ($value) {
                $value = $value.Trim().Trim('"')
                if ($value.ToLowerInvariant().EndsWith('.exe')) { return $value }
            }
        }

        return (Join-Path $env:LOCALAPPDATA 'RoS-Tools') + '\' + $exeName
    }

    $temp = Join-Path ([IO.Path]::GetTempPath()) ('rostools-sidecar-' + [Guid]::NewGuid().ToString('N'))
    $progress = $ProgressPreference

    try {
        Write-Host ''
        Write-Host '  RoS-Tools Sidecar -- install / update' -ForegroundColor Cyan

        $target    = Resolve-Target
        $targetDir = Split-Path -Parent $target
        $installed = Get-InstalledVersion -Exe $target

        Say ("target    " + $target)
        if ($installed) { Say ("installed " + $installed) } else { Say 'installed none' }

        # --- work out which release we want -------------------------------------
        $headers = @{
            'Accept'     = 'application/vnd.github+json'
            'User-Agent' = 'RoS-Tools-Install-Sidecar'
        }

        if ($env:ROSTOOLS_SIDECAR_VERSION) {
            $wantTag = $tagPrefix + $env:ROSTOOLS_SIDECAR_VERSION.Trim().TrimStart('v')
            $release = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases/tags/$wantTag" -Headers $headers -UseBasicParsing
        }
        else {
            $all = Invoke-RestMethod -Uri "https://api.github.com/repos/$repo/releases?per_page=50" -Headers $headers -UseBasicParsing

            # Filter by tag prefix: this repo's other release stream must never
            # be mistaken for a sidecar build. Sort by parsed version rather than
            # trusting API order, so a re-cut older tag cannot win.
            $candidates =
                @($all) |
                Where-Object { $_.PSObject.Properties['tag_name'] -and $_.tag_name -clike "$tagPrefix*" } |
                ForEach-Object {
                    $v = $null
                    if ([Version]::TryParse(($_.tag_name.Substring($tagPrefix.Length) -split '[+-]')[0], [ref] $v)) {
                        [pscustomobject]@{ Version = $v; Release = $_ }
                    }
                } |
                Sort-Object -Property Version -Descending

            if (-not $candidates) {
                throw "No $tagPrefix* release found. Cut one, or set ROSTOOLS_SIDECAR_VERSION."
            }
            $release = @($candidates)[0].Release
        }

        $tag = [string] $release.tag_name
        $latestRaw = ($tag.Substring($tagPrefix.Length) -split '[+-]')[0]
        $latest = $null
        if (-not [Version]::TryParse($latestRaw, [ref] $latest)) {
            throw "Release tag '$tag' does not carry a version this script can read."
        }
        Say ("available " + $latest + "  (" + $tag + ")")

        # --- decide whether to do anything --------------------------------------
        $installedC = Get-Comparable $installed
        $latestC    = Get-Comparable $latest

        if (-not $env:ROSTOOLS_SIDECAR_FORCE -and $null -ne $installedC) {
            if ($installedC -eq $latestC) {
                Write-Host ''
                Good 'already up to date'
                Write-Host ''
                return
            }
            if ($installedC -gt $latestC -and -not $env:ROSTOOLS_SIDECAR_VERSION) {
                Write-Host ''
                Warn "installed $installed is newer than the newest release $latest -- leaving it alone"
                Say  'set ROSTOOLS_SIDECAR_FORCE=1 to overwrite it anyway.'
                Write-Host ''
                return
            }
        }

        # --- find the assets -----------------------------------------------------
        $assets = @($release.assets)
        $exeAsset = $assets | Where-Object { $_.name -clike '*.exe' } | Select-Object -First 1
        if (-not $exeAsset) { throw "Release $tag has no .exe asset attached." }
        $hashAsset = $assets | Where-Object { $_.name -ceq ($exeAsset.name + '.sha256') } | Select-Object -First 1

        New-Item -ItemType Directory -Path $temp -Force | Out-Null
        $staged = Join-Path $temp $exeName

        Say ("downloading " + $exeAsset.name)
        $ProgressPreference = 'SilentlyContinue'
        $download = @{
            Uri             = [string] $exeAsset.browser_download_url
            OutFile         = $staged
            UseBasicParsing = $true
        }
        Invoke-WebRequest @download
        $ProgressPreference = $progress

        # --- verify --------------------------------------------------------------
        if ($hashAsset) {
            $hashFile = Join-Path $temp 'expected.sha256'
            $getHash = @{
                Uri             = [string] $hashAsset.browser_download_url
                OutFile         = $hashFile
                UseBasicParsing = $true
            }
            $ProgressPreference = 'SilentlyContinue'
            Invoke-WebRequest @getHash
            $ProgressPreference = $progress

            # sha256sum format is "<hash>  <filename>"; take the first 64 hex chars.
            $expected = $null
            $match = [regex]::Match([IO.File]::ReadAllText($hashFile), '[0-9a-fA-F]{64}')
            if ($match.Success) { $expected = $match.Value }
            if (-not $expected) { throw "The published checksum for $tag is not readable." }

            $actual = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash
            if ($actual -ne $expected) {
                throw ("Checksum mismatch for " + $exeAsset.name + ". Expected " + $expected.ToLowerInvariant() +
                       ", got " + $actual.ToLowerInvariant() + ". Nothing was installed.")
            }
            Say 'checksum ok'
        }
        else {
            # Releases cut before the checksum was added to sidecar.yml have none.
            # The exe is unsigned, so say plainly that nothing was verified.
            Warn "release $tag publishes no .sha256 -- the download could not be verified"
        }

        # --- stop whatever is running -------------------------------------------
        # Program.cs holds a per-session mutex and a second launch only signals the
        # first, so the old instance must actually be gone before we start again.
        $wasRunning = $false
        $running = @(Get-Process -Name $processName -ErrorAction SilentlyContinue)
        if ($running.Count -gt 0) {
            $wasRunning = $true
            Say ("stopping " + $running.Count + " running instance(s)")
            foreach ($proc in $running) {
                try {
                    if (-not $proc.HasExited) { $proc.Kill() }
                    $null = $proc.WaitForExit(15000)
                }
                catch {
                    # Racing a process that exited on its own is fine and common.
                    # Anything else -- a denied kill, another user's session --
                    # is not, and must not be silent: the swap below would then
                    # fail on a locked file with a much less obvious message.
                    if (-not $proc.HasExited) {
                        Warn ("could not stop pid " + $proc.Id + ": " + $_.Exception.Message)
                    }
                }
            }
        }

        # --- swap ----------------------------------------------------------------
        if (-not (Test-Path -LiteralPath $targetDir)) {
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        }

        # A just-exited process, an AV scanner or Explorer can hold the old exe for
        # a moment. Retry rather than failing the whole run on a transient lock.
        $moved = $false
        foreach ($attempt in 1..10) {
            try {
                Move-Item -LiteralPath $staged -Destination $target -Force
                $moved = $true
                break
            }
            catch {
                if ($attempt -eq 10) { throw }
                Start-Sleep -Milliseconds 500
            }
        }
        if (-not $moved) { throw "Could not replace $target." }

        # --- repair a Run key that we just moved out from under ------------------
        # Only ever rewrite an entry that already exists: whether to start with
        # Windows is the user's choice, made in the app, not here.
        $entry = Get-ItemProperty -LiteralPath $runKey -ErrorAction SilentlyContinue
        if ($entry -and $entry.PSObject.Properties[$runValueName]) {
            $current = ([string] $entry.$runValueName).Trim()
            $wanted  = '"' + $target + '"'
            if ($current -ne $wanted) {
                Set-ItemProperty -LiteralPath $runKey -Name $runValueName -Value $wanted
                Say 'repointed the start-with-Windows entry at the new location'
            }
        }

        # --- start ---------------------------------------------------------------
        $started = $false
        if (-not $env:ROSTOOLS_SIDECAR_NOSTART) {
            Start-Process -FilePath $target
            $started = $true
        }

        Write-Host ''
        if ($installed) { Good ("updated " + $installed + " -> " + $latest) }
        else            { Good ("installed " + $latest) }
        Say ("at " + $target)
        if ($started)         { Say 'started -- look for the tray icon' }
        elseif ($wasRunning)  { Warn 'it was running and was stopped; ROSTOOLS_SIDECAR_NOSTART left it stopped' }
        else                  { Say 'not started (ROSTOOLS_SIDECAR_NOSTART)' }
        Write-Host ''
    }
    catch {
        $ProgressPreference = $progress
        Write-Host ''
        Bad $_.Exception.Message
        if ($_.Exception.Message -match 'Access to the path|is denied|UnauthorizedAccess') {
            Bad 'Re-run in an elevated window, or set ROSTOOLS_SIDECAR_PATH somewhere writable.'
        }
        Write-Host ''
    }
    finally {
        $ProgressPreference = $progress
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
