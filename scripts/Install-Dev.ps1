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
    function Warn { param([string] $m) Write-Host "  $m" -ForegroundColor Yellow }
    function Bad  { param([string] $m) Write-Host "  $m" -ForegroundColor Red }

    # A just-exited game client, an AV scanner or the Windows indexer can hold a
    # directory for a moment, and every rename below is destructive if it fails.
    # Install-Sidecar.ps1 retries its one swap for exactly this reason; these
    # retried nothing at all, so a transient lock cost the install.
    function Move-WithRetry {
        param([string] $From, [string] $To, [int] $Attempts = 5)
        foreach ($attempt in 1..$Attempts) {
            try {
                Move-Item -LiteralPath $From -Destination $To -Force
                return
            }
            catch {
                if ($attempt -eq $Attempts) { throw }
                Start-Sleep -Milliseconds 400
            }
        }
    }

    # '<addon>.new-<guid>' and '<addon>.old-<guid>' are created NEXT TO the
    # install so the swap is a rename rather than a cross-volume copy. A crash,
    # a Ctrl-C or a handle held on a subfolder leaves one behind, and the GUID
    # is fresh every run, so nothing ever reclaims it: an AddOns folder could
    # silently grow a full copy of the addon per interrupted run, and the
    # Remove-Item that was supposed to clean up carried
    # -ErrorAction SilentlyContinue and said nothing when it failed.
    #
    # An '.old-' directory while NOTHING is installed is not garbage -- it is
    # the rescue copy from a run that died between the two renames, and it holds
    # the only surviving Data\GuildData.lua. That case is reported and returned,
    # never swept, and the caller keeps it out of the end-of-run sweep too: the
    # user has had no chance to take anything out of it yet.
    function Clear-InstallLeftover {
        param([string] $Root, [string] $Name, [string] $Dest, [string[]] $Keep = @())

        $installed = Test-Path -LiteralPath $Dest -ErrorAction SilentlyContinue
        $stale = @(
            Get-ChildItem -LiteralPath $Root -Directory -Force -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like ($Name + '.new-*') -or $_.Name -like ($Name + '.old-*') }
        )
        $rescued = New-Object System.Collections.Generic.List[string]

        foreach ($item in $stale) {
            if ($Keep -contains $item.FullName) { continue }

            if (-not $installed -and $item.Name -like ($Name + '.old-*')) {
                Warn ($Name + ' is not installed, but an earlier run left its copy of it at:')
                Warn ('    ' + $item.FullName)
                Warn  'that folder still holds that install''s Data\GuildData.lua. Nothing here deletes it.'
                $rescued.Add($item.FullName)
                continue
            }

            Remove-Item -LiteralPath $item.FullName -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $item.FullName -ErrorAction SilentlyContinue) {
                Warn ('could not remove a leftover from an earlier run -- delete it by hand:')
                Warn ('    ' + $item.FullName)
            }
            else {
                Say ('removed a leftover from an earlier run: ' + $item.Name)
            }
        }

        return $rescued.ToArray()
    }

    function Resolve-AddOnsPath {
        if ($env:ROSTOOLS_ADDONS_PATH) {
            # -ErrorAction SilentlyContinue is load-bearing: Test-Path raises
            # "Cannot find drive" for an unmounted letter, and with
            # $ErrorActionPreference = 'Stop' that terminates the run with a
            # nonsense message instead of reaching the friendly throw below --
            # which is the single most likely thing to be wrong with this value.
            if (-not (Test-Path -LiteralPath $env:ROSTOOLS_ADDONS_PATH -ErrorAction SilentlyContinue)) {
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

    # The rescued roster is a SIBLING of $work, never a child: the finally below
    # removes $work unconditionally, and staging the only surviving copy of the
    # user's roster inside something we are about to delete is how it used to be
    # lost for good. It is removed only once the install has actually landed.
    $keep = "$work-keep-GuildData.lua"

    # Set once the AddOns folder is known. Both live NEXT TO the destination, on
    # the same volume, so the swap is a rename rather than a cross-volume copy.
    # $dest is declared here too: the finally reads it, and StrictMode turns a
    # reference to a never-assigned variable into a throw of its own if
    # Resolve-AddOnsPath is what failed.
    $dest     = $null
    $stage    = $null
    $backup   = $null
    $finished = $false
    $rescued  = @()

    try {
        Write-Host ''
        Write-Host "  RoS-Tools -- development install" -ForegroundColor Cyan

        $addons = Resolve-AddOnsPath
        $dest   = Join-Path $addons $addon
        Say "ref     $ref"
        Say "into    $dest"

        # Before anything else, reclaim what earlier runs left behind. Whatever
        # it refuses to remove comes back here so the end-of-run sweep leaves it
        # alone as well.
        $rescued = @(Clear-InstallLeftover -Root $addons -Name $addon -Dest $dest)

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

        # Validate the WHOLE payload before anything destructive happens. A ref
        # that predates Data\ used to get as far as deleting the installed copy
        # and only then discover the folder was missing, leaving an empty
        # RoS-Tools folder behind and nothing to put back.
        $sources = New-Object System.Collections.Generic.List[string]
        foreach ($item in $payload) {
            $from = Join-Path $src.FullName $item
            if (-not (Test-Path -LiteralPath $from -ErrorAction SilentlyContinue)) {
                throw "'$item' is missing from the archive for '$ref'. Nothing was changed."
            }
            $sources.Add($from)
        }

        # Optionally hold on to the roster that is already installed. Staged to
        # a sibling of $work, not into it -- the finally removes $work.
        $keeping = $false
        if ($env:ROSTOOLS_KEEP_DATA) {
            $current = Join-Path $dest 'Data\GuildData.lua'
            if (Test-Path -LiteralPath $current -ErrorAction SilentlyContinue) {
                Copy-Item -LiteralPath $current -Destination $keep -Force
                $keeping = $true
                Say 'keeping the roster already installed (ROSTOOLS_KEEP_DATA)'
            } else {
                Say 'ROSTOOLS_KEEP_DATA is set, but nothing is installed yet -- using the ref''s roster'
            }
        }

        # Assemble the complete new folder beside the destination, then swap it
        # in with a rename. Same volume as $dest, so the move is a rename and
        # not a cross-volume copy that could half-succeed. The installed copy is
        # replaced only once every single item has been copied, so a failure
        # anywhere above leaves the existing install exactly as it was.
        $stage  = Join-Path $addons ($addon + '.new-' + [Guid]::NewGuid().ToString('N'))
        $backup = Join-Path $addons ($addon + '.old-' + [Guid]::NewGuid().ToString('N'))

        New-Item -ItemType Directory -Path $stage -Force | Out-Null
        foreach ($from in $sources) {
            Copy-Item -LiteralPath $from -Destination $stage -Recurse -Force
        }

        if ($keeping) {
            $stagedData = Join-Path $stage 'Data'
            if (-not (Test-Path -LiteralPath $stagedData -ErrorAction SilentlyContinue)) {
                New-Item -ItemType Directory -Path $stagedData -Force | Out-Null
            }
            Copy-Item -LiteralPath $keep -Destination (Join-Path $stagedData 'GuildData.lua') -Force
        }

        # Wipe and rewrite, so a file deleted or renamed in source does not
        # linger in the installed copy. Settings live in WTF\, never here.
        $movedAside = $false
        if (Test-Path -LiteralPath $dest -ErrorAction SilentlyContinue) {
            Move-WithRetry -From $dest -To $backup
            $movedAside = $true
        }
        try {
            Move-WithRetry -From $stage -To $dest
            $stage = $null
        }
        catch {
            # Hold on to the ORIGINAL failure. The restore below is itself a
            # rename that can fail, and an unguarded one threw its own exception
            # from inside this catch: that replaced the error explaining why the
            # swap failed, skipped the reassuring message, and -- because
            # `$backup = $null` never ran -- left the backup looking superseded
            # to the cleanup below, which then deleted the only surviving copy.
            $swapError = $_

            if ($movedAside -and -not (Test-Path -LiteralPath $dest -ErrorAction SilentlyContinue)) {
                try {
                    Move-WithRetry -From $backup -To $dest
                    $backup = $null
                    Say 'the swap failed -- the previous install was put back'
                }
                catch {
                    # $backup deliberately stays non-null and undeleted: it is
                    # now the user's only copy of the addon. The finally names
                    # it and says how to put it back by hand.
                    Bad ('the previous install could not be put back either: ' + $_.Exception.Message)
                }
            }

            throw $swapError
        }
        $finished = $true

        # Report what actually landed, not what was supposed to.
        $entries = 0
        $stamp   = 'unknown'
        $data    = Join-Path $dest 'Data\GuildData.lua'
        if (Test-Path -LiteralPath $data -ErrorAction SilentlyContinue) {
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
        # This block runs on Ctrl-C as well as on success and on a caught
        # failure -- PowerShell runs finally on a stopped pipeline but does NOT
        # run catch -- so nothing here may assume the catch above has spoken.
        $ProgressPreference = $progress
        Remove-Item -LiteralPath $zip  -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue

        # The staged copy was never installed, so it is throwaway either way.
        if ($stage -and (Test-Path -LiteralPath $stage -ErrorAction SilentlyContinue)) {
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
            if (Test-Path -LiteralPath $stage -ErrorAction SilentlyContinue) {
                Warn ('could not remove the staged copy -- delete it by hand: ' + $stage)
            }
        }

        # The backup is superseded ONLY once the new install has actually
        # landed. Deleting it unconditionally destroyed the user's last copy on
        # every failure path -- including Ctrl-C -- and said nothing at all.
        if ($backup -and (Test-Path -LiteralPath $backup -ErrorAction SilentlyContinue)) {
            if ($finished) {
                Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
                if (Test-Path -LiteralPath $backup -ErrorAction SilentlyContinue) {
                    Warn ('could not remove the previous install -- delete it by hand: ' + $backup)
                }
            }
            elseif (-not ($dest -and (Test-Path -LiteralPath $dest -ErrorAction SilentlyContinue))) {
                Write-Host ''
                Bad ($addon + ' is NOT installed right now.')
                Bad  'your previous install was moved aside and is still on disk at:'
                Bad ('    ' + $backup)
                Bad ('rename that folder to ''' + $addon + ''' to get it back, including Data\GuildData.lua.')
            }
            else {
                Say ('the previous install was left at ' + $backup)
                Say  'delete it once you are happy; the next run sweeps it up.'
            }
        }

        # Reclaim anything else an interrupted run left lying around, but only
        # once the install has landed -- while it has not, an '.old-' directory
        # is a rescue copy, not garbage, and Clear-InstallLeftover reports it
        # rather than removing it.
        if ($finished -and $dest) {
            # Anything already named above is kept out: reporting it twice under
            # an "earlier run" label would be misleading, and a rescue copy must
            # survive the run that reported it -- the user has not had a chance
            # to take their roster out of it yet.
            $reported = @(@($backup, $stage) + $rescued | Where-Object { $_ })
            $null = Clear-InstallLeftover -Root (Split-Path -Parent $dest) -Name $addon -Dest $dest -Keep $reported
            foreach ($path in $rescued) {
                Warn ('the interrupted run''s copy is still at ' + $path)
                Warn  'take what you need from it, then delete it -- the next run sweeps it up.'
            }
        }

        # The rescued roster is deleted only once it is safely installed. If the
        # run failed it is the user's only copy, so say where it is -- from HERE
        # rather than from the catch, which Ctrl-C never reaches.
        if ($finished) {
            Remove-Item -LiteralPath $keep -Force -ErrorAction SilentlyContinue
        }
        elseif (Test-Path -LiteralPath $keep -ErrorAction SilentlyContinue) {
            Say ('the roster that was installed was rescued to ' + $keep)
        }
    }
}
