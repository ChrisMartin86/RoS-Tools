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
    # Only for the discovery pass in Resolve-Target, where no target is known
    # yet and the default name is all there is to go on. Once a target has been
    # resolved, the process name is derived from IT -- ROSTOOLS_SIDECAR_PATH may
    # name a renamed exe, and this constant would then find nothing.
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

    # Pure: everything Get-InstalledVersion does to a version STRING, split out
    # so Tools/release-selection.Tests.ps1 can drive it. FileVersionInfo cannot
    # be reached from the test host, and this half is the half that misfires.
    #
    # A '-rc1' suffix is REMEMBERED, not merely stripped. An RC build reports
    # ProductVersion '1.4.0-rc1'; discarding the suffix made it compare EQUAL to
    # the stable 1.4.0 that follows it, so the stable release was reported as
    # "already up to date" and the hand-installed RC was never replaced.
    #
    # But SemVer build metadata starts at the first '+' and MAY CONTAIN '-'.
    # Testing the whole string for '-' flagged a perfectly stable
    # '1.4.0+build-123' as a pre-release, ranking it 1.4.0.0 against the
    # release's 1.4.0.1 -- "out of date" forever, and 50 MB down the wire on
    # every run, which is exactly the failure CLAUDE.md's normaliser exists to
    # prevent. Only a '-' BEFORE the '+' is a prerelease marker.
    function ConvertTo-InstalledVersion {
        param([string] $Raw)
        if (-not $Raw) { return $null }
        $Raw  = $Raw.Trim()
        $core = ($Raw -split '\+', 2)[0]
        $isPrerelease = $core.Contains('-')
        $number = ($core -split '-', 2)[0].Trim()
        $parsed = $null
        if (-not [Version]::TryParse($number, [ref] $parsed)) { return $null }
        return [pscustomobject]@{
            Version      = $parsed
            IsPrerelease = [bool] $isPrerelease
        }
    }

    function Get-InstalledVersion {
        param([string] $Exe)
        # -ErrorAction SilentlyContinue is load-bearing, and this line sits
        # OUTSIDE the try below: Test-Path raises "Cannot find drive" for an
        # unmounted letter, which $ErrorActionPreference = 'Stop' makes
        # terminating. Resolve-Target hands back a Run-key value verbatim, and a
        # Run key outlives the drive it points at, so a sidecar installed to a
        # now-missing E: would kill the run before a byte was downloaded. "Not
        # installed" is the honest answer for a path we cannot reach.
        if (-not (Test-Path -LiteralPath $Exe -ErrorAction SilentlyContinue)) { return $null }
        try {
            $info = [Diagnostics.FileVersionInfo]::GetVersionInfo($Exe)
            # -p:Version=1.2.0 lands in ProductVersion, sometimes with a
            # +buildmetadata suffix. FileVersion is the 4-part fallback.
            $raw = $info.ProductVersion
            if (-not $raw) { $raw = $info.FileVersion }
            return (ConvertTo-InstalledVersion -Raw $raw)
        }
        catch { return $null }
    }

    # A 2-part and a 4-part version never compare equal to the 3-part tag even
    # when they mean the same build, so compare on major/minor/build only. The
    # revision slot carries prerelease-vs-release rank instead: 1.4.0-rc1 becomes
    # 1.4.0.0 and the stable 1.4.0 becomes 1.4.0.1, so the stable release sorts
    # strictly above the RC and actually replaces it.
    function Get-Comparable {
        param($V, [switch] $Prerelease)
        if ($null -eq $V) { return $null }
        $build = $V.Build
        if ($build -lt 0) { $build = 0 }
        $rank = 1
        if ($Prerelease) { $rank = 0 }
        return (New-Object Version($V.Major, $V.Minor, $build, $rank))
    }

    # A release tag must be exactly <prefix>MAJOR.MINOR.PATCH -- no '-rc1', no
    # '+sha'. The old code stripped the suffix with -split '[+-]', which made
    # 'sidecar-v1.4.0-rc1' parse as 1.4.0: it sorted above the stable 1.3.0 and
    # was installed by everyone running the one-liner, and then the later stable
    # 'sidecar-v1.4.0' compared EQUAL to it, so the RC was reported as up to date
    # and never replaced. A suffix a Version cannot represent is rejected, not
    # discarded.
    function Get-TagVersion {
        param([string] $Tag, [string] $Prefix)
        if (-not $Tag) { return $null }
        if (-not $Tag.StartsWith($Prefix, [StringComparison]::Ordinal)) { return $null }
        $rest = $Tag.Substring($Prefix.Length).Trim()
        # Exactly three parts. The looser '{1,3}' this used to allow accepted a
        # 4-part 'sidecar-v1.4.0.7', and Get-Comparable drops the revision slot
        # to carry prerelease rank -- so it collapsed to the same comparable as
        # 'sidecar-v1.4.0' and the two releases compared EQUAL. That is the
        # revision-slot collision the version work was done to kill, and the
        # error text and the comment above both already promise MAJOR.MINOR.PATCH.
        if ($rest -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { return $null }
        $parsed = $null
        if ([Version]::TryParse($rest, [ref] $parsed)) { return $parsed }
        return $null
    }

    # StrictMode turns a missing property into a throw, and the GitHub payload
    # for a release cut by another tool may not carry every field.
    function Test-ReleaseFlag {
        param($Release, [string] $Name)
        if ($null -eq $Release) { return $false }
        if (-not $Release.PSObject.Properties[$Name]) { return $false }
        $value = $Release.$Name
        if ($null -eq $value) { return $false }
        # [bool] 'false' is $TRUE in PowerShell -- every non-empty string is.
        # A payload whose flags arrive as strings rather than JSON booleans
        # therefore marked every release both a draft AND a prerelease, and the
        # script reported "No stable sidecar-v* release found" against a repo
        # full of them. It fails safe, but only by accident, and the accident
        # is a maintainer machine that silently stops updating.
        if ($value -is [string]) {
            return ($value.Trim() -notin @('', '0', 'false', 'no', 'null'))
        }
        return [bool] $value
    }

    # Pure: given whatever the releases endpoint returned, pick the newest
    # STABLE sidecar build, or $null when there is none. Deliberately separate
    # from the web call so Tools/release-selection.Tests.ps1 can drive it with a
    # fabricated release list -- the ordering here is the whole bug.
    function Select-SidecarRelease {
        param($Releases, [string] $Prefix)

        # Filter by tag prefix: this repo's other release stream must never be
        # mistaken for a sidecar build. Drafts and prereleases are not candidates
        # either -- a draft is not published, and an RC must never be handed to a
        # maintainer machine by the unattended one-liner. Sort by parsed version
        # rather than trusting API order, so a re-cut older tag cannot win.
        #
        # A plain foreach, not `@($Releases) | Where-Object`: the caller hands
        # this a System.Collections.Generic.List[object] built from the paged
        # API response, and splatting one of those into the pipeline throws
        # "Argument types do not match". The unit tests only ever passed a
        # PowerShell array, so nothing noticed. foreach handles a list, an
        # array, a single object and $null alike.
        $candidates = @()
        foreach ($item in $Releases) {
            if ($null -eq $item) { continue }
            if (-not $item.PSObject.Properties['tag_name']) { continue }

            $tag = [string] $item.tag_name
            if (-not $tag.StartsWith($Prefix, [StringComparison]::Ordinal)) { continue }
            if (Test-ReleaseFlag $item 'draft') { continue }
            if (Test-ReleaseFlag $item 'prerelease') { continue }

            $v = Get-TagVersion -Tag $tag -Prefix $Prefix
            if ($v) { $candidates += [pscustomobject]@{ Version = $v; Release = $item } }
        }

        if ($candidates.Count -eq 0) { return $null }
        return @($candidates | Sort-Object -Property Version -Descending)[0].Release
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

    # Declared out here because the catch reads them, and StrictMode makes a
    # reference to a never-assigned variable a throw of its own.
    $target     = $null
    $wasRunning = $false
    $killDenied = $false
    $moved      = $false
    $started    = $false
    $incoming   = $null
    $beforeHash = $null
    $stagedHash = $null

    try {
        Write-Host ''
        Write-Host '  RoS-Tools Sidecar -- install / update' -ForegroundColor Cyan

        $target    = Resolve-Target
        $targetDir = Split-Path -Parent $target
        $installed = Get-InstalledVersion -Exe $target

        Say ("target    " + $target)
        if ($installed) {
            $suffix = ''
            if ($installed.IsPrerelease) { $suffix = '  (a pre-release build)' }
            Say ("installed " + $installed.Version + $suffix)
        }
        else { Say 'installed none' }

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
            # GitHub caps per_page at 100 and pages the remainder. Walk the pages
            # rather than hoping one is enough: releases from this repo's other
            # streams share the list, so a run of addon releases could otherwise
            # push every sidecar-v tag off the first page and the script would
            # report "No sidecar-v* release found" on a repo that has plenty.
            # Stop at the first short page; the page cap keeps a pathological
            # release list from spinning forever.
            $perPage  = 100
            $pageCap  = 10
            $all      = New-Object System.Collections.Generic.List[object]
            foreach ($page in 1..$pageCap) {
                $uri   = "https://api.github.com/repos/$repo/releases?per_page=$perPage&page=$page"
                $batch = @(Invoke-RestMethod -Uri $uri -Headers $headers -UseBasicParsing)
                foreach ($item in $batch) { $all.Add($item) }
                if ($batch.Count -lt $perPage) { break }
            }

            $release = Select-SidecarRelease -Releases $all -Prefix $tagPrefix
            if (-not $release) {
                throw "No stable $tagPrefix* release found. Cut one, or set ROSTOOLS_SIDECAR_VERSION."
            }
        }

        $tag = [string] $release.tag_name
        if ((Test-ReleaseFlag $release 'draft') -or (Test-ReleaseFlag $release 'prerelease')) {
            throw "Release $tag is marked draft or prerelease. Refusing to install it."
        }
        $latest = Get-TagVersion -Tag $tag -Prefix $tagPrefix
        if (-not $latest) {
            throw ("Release tag '" + $tag + "' does not carry a version this script can read. " +
                   "It must be exactly " + $tagPrefix + "MAJOR.MINOR.PATCH -- a pre-release or " +
                   "build-metadata suffix is rejected rather than stripped, because a stripped " +
                   "suffix makes an RC compare equal to the stable release that follows it.")
        }
        Say ("available " + $latest + "  (" + $tag + ")")

        # --- decide whether to do anything --------------------------------------
        $installedC = $null
        if ($installed) {
            $installedC = Get-Comparable -V $installed.Version -Prerelease:$installed.IsPrerelease
        }
        # A tag that reached this point is always a stable release: prereleases
        # are filtered out above and a suffixed tag is rejected outright.
        $latestC = Get-Comparable -V $latest

        if (-not $env:ROSTOOLS_SIDECAR_FORCE -and $null -ne $installedC) {
            if ($installedC -eq $latestC) {
                Write-Host ''
                Good 'already up to date'
                Write-Host ''
                return
            }
            if ($installedC -gt $latestC -and -not $env:ROSTOOLS_SIDECAR_VERSION) {
                Write-Host ''
                Warn ("installed " + $installed.Version + " is newer than the newest release " + $latest + " -- leaving it alone")
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
        #
        # The name comes from the RESOLVED TARGET, not from the $processName
        # constant: Resolve-Target honours any .exe filename given in
        # ROSTOOLS_SIDECAR_PATH, and against a renamed exe the constant found
        # nothing, left $wasRunning false, and let the live process keep its file
        # lock -- so the retry loop below burned five seconds and then failed on
        # a locked file with no hint that the tray app had to be closed.
        $targetProcName = [IO.Path]::GetFileNameWithoutExtension($target)
        $running = @(
            Get-Process -Name $targetProcName -ErrorAction SilentlyContinue |
            Where-Object {
                # Match on the image path where the OS lets us read it, so a
                # same-named instance running from somewhere else is left alone.
                # A path we cannot read (elevated, or another user) is KEPT --
                # it may well be the process holding the lock, and stopping it
                # is the entire point of this step.
                $path = $null
                try { $path = $_.Path } catch { $path = $null }
                (-not $path) -or ($path -eq $target)
            }
        )
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
                    # It is remembered as well as reported: the catch must not
                    # "restart" the sidecar next to an instance that never died.
                    # Program.cs's mutex is Local\, so it would not notice a
                    # survivor in another Terminal Services session.
                    if (-not $proc.HasExited) {
                        $killDenied = $true
                        Warn ("could not stop pid " + $proc.Id + ": " + $_.Exception.Message)
                    }
                }
            }
        }

        # --- swap ----------------------------------------------------------------
        # -ErrorAction SilentlyContinue is load-bearing here too, and this site
        # is the worse of the two: it runs AFTER the kill loop above. With
        # ROSTOOLS_SIDECAR_PATH on a drive that is not mounted, the unguarded
        # Test-Path threw "Cannot find drive" once the running sidecar had
        # already been stopped -- leaving the machine that seeds the guild's peer
        # sync stopped with nothing installed. It now falls through to New-Item,
        # whose failure is caught below and restarts what was running.
        if (-not (Test-Path -LiteralPath $targetDir -ErrorAction SilentlyContinue)) {
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        }

        # What is on disk right now, and what should be there afterwards. The
        # catch below uses these to tell "the exe that was running is still
        # intact, restart it" from "this file is a half-written copy, do not".
        if (Test-Path -LiteralPath $target -ErrorAction SilentlyContinue) {
            $beforeHash = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash
        }
        $stagedHash = (Get-FileHash -LiteralPath $staged -Algorithm SHA256).Hash
        $stagedLen  = (Get-Item -LiteralPath $staged).Length

        # A Move-Item from %TEMP% to a target on ANOTHER VOLUME is copy-then-
        # delete, not a rename -- and ROSTOOLS_SIDECAR_PATH pointing at a second
        # drive is the normal reason to set it at all. When that copy runs out
        # of space or the volume goes away part-way, Move-Item leaves a
        # TRUNCATED file at $target: the good old exe is gone, $moved is false,
        # and the catch below saw Test-Path $target succeed and launched the
        # corrupt binary. The retry loop then repeated the truncating copy nine
        # more times first.
        #
        # So: copy to a sibling ON THE DESTINATION VOLUME, verify it byte for
        # byte against what was downloaded, and only then rename it over the
        # target -- a rename within one volume, which cannot half-succeed.
        # $target holds either the old exe or the new one, never a fragment.
        $incoming = $target + '.new'
        Remove-Item -LiteralPath $incoming -Force -ErrorAction SilentlyContinue

        # A just-exited process, an AV scanner or Explorer can hold the old exe for
        # a moment. Retry rather than failing the whole run on a transient lock.
        foreach ($attempt in 1..10) {
            try {
                Copy-Item -LiteralPath $staged -Destination $incoming -Force

                $landedLen = (Get-Item -LiteralPath $incoming).Length
                if ($landedLen -ne $stagedLen) {
                    throw ("only " + $landedLen + " of " + $stagedLen + " bytes reached " + $incoming +
                           " -- the destination is out of space, or the volume went away mid-copy.")
                }
                if ((Get-FileHash -LiteralPath $incoming -Algorithm SHA256).Hash -ne $stagedHash) {
                    throw ("the copy at " + $incoming + " does not match the verified download.")
                }

                Move-Item -LiteralPath $incoming -Destination $target -Force
                $moved = $true
                break
            }
            catch {
                Remove-Item -LiteralPath $incoming -Force -ErrorAction SilentlyContinue
                if ($attempt -eq 10) {
                    throw ("Could not replace " + $target + " after 10 tries. " +
                           "If it is locked, close " + $targetProcName + " (right-click its tray icon, " +
                           "Exit) and re-run. Nothing was overwritten. " +
                           "Last error: " + $_.Exception.Message)
                }
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
        # $started is declared beside $moved above, not here: the catch reads it
        # to decide whether the machine that seeds the guild's peer sync is
        # sitting stopped, and a variable declared inside this try does not
        # exist yet when the throw happens before this line.
        if (-not $env:ROSTOOLS_SIDECAR_NOSTART) {
            Start-Process -FilePath $target
            $started = $true
        }

        Write-Host ''
        if ($installed) { Good ("updated " + $installed.Version + " -> " + $latest) }
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

        # We stopped the sidecar and it is not running now. Start it again: this
        # is very often the single machine seeding the guild's peer sync, and
        # leaving it stopped is a strictly worse outcome than not updating.
        # ROSTOOLS_SIDECAR_NOSTART is deliberately not honoured here -- it means
        # "do not start the new build", not "leave the old one dead".
        #
        # The condition is `-not $started`, not `-not $moved`. Everything after
        # the swap can still throw -- Set-ItemProperty on a policy-managed Run
        # key, and Start-Process itself against SmartScreen or AppLocker on what
        # is an unsigned binary by design -- and each of those exited through
        # here having killed the sidecar, installed the new build and started
        # nothing, printing only the raw exception. That is the exact "left
        # stopped" outcome this block exists to prevent.
        if ($wasRunning -and -not $started) {
            $onDisk = $null
            if ($target -and (Test-Path -LiteralPath $target -ErrorAction SilentlyContinue)) {
                try { $onDisk = (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash }
                catch { $onDisk = $null }
            }
            # Only ever launch a binary we can still identify as one of the two
            # builds we know about. Anything else is a fragment.
            $known = ($null -ne $onDisk) -and
                     ((($null -ne $beforeHash) -and ($onDisk -eq $beforeHash)) -or
                      (($null -ne $stagedHash) -and ($onDisk -eq $stagedHash)))

            if ($killDenied) {
                Warn 'an instance could not be stopped and may still be running -- not starting a second one.'
                Say  ('if the tray icon is gone, start it by hand: ' + $target)
            }
            elseif ($null -eq $onDisk) {
                Bad 'the sidecar was stopped and its exe is no longer where it was -- reinstall before relying on peer sync'
            }
            elseif (-not $known) {
                Bad ('the file at ' + $target + ' matches neither the build that was running nor the one that was downloaded.')
                Bad  'do NOT run it -- it is most likely a half-written copy. Re-run this installer instead.'
            }
            else {
                try {
                    Start-Process -FilePath $target
                    if ($moved) { Warn 'the new build is installed but did not start on its own -- it has been started now' }
                    else        { Warn 'nothing was installed -- the instance that was running has been started again' }
                }
                catch {
                    Bad ('the sidecar was stopped and could not be started again: ' + $_.Exception.Message)
                    Bad ('start it by hand: ' + $target)
                }
            }
        }
        Write-Host ''
    }
    finally {
        $ProgressPreference = $progress
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
        # The verified copy never became the target, so it is throwaway. (After
        # a successful swap it has been renamed away and this does nothing.)
        if ($incoming) { Remove-Item -LiteralPath $incoming -Force -ErrorAction SilentlyContinue }
    }
}
