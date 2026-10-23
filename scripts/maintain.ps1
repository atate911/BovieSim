# Bovie Sim maintenance for the PC it runs on: keeps the clone up to date, checks the PC and the Unity
# project, runs Unity's setup, tests and build from the command line, and collects a report when
# something goes wrong. Run it through maintain.cmd in the repo folder:  maintain help
#
# Windows PowerShell 5.1 or later. ASCII-only source.

$ErrorActionPreference = 'Stop'
if (-not $env:LOCALAPPDATA) { $env:LOCALAPPDATA = [Environment]::GetFolderPath('LocalApplicationData') }
if (-not $env:APPDATA) { $env:APPDATA = [Environment]::GetFolderPath('ApplicationData') }
if (-not $env:USERPROFILE) { $env:USERPROFILE = $HOME }

. (Join-Path $PSScriptRoot 'lib\Common.ps1')
. (Join-Path $PSScriptRoot 'lib\Probe.ps1')

$script:Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$script:ProjectDir = Join-Path $script:Root 'unity\BovieSim'
$script:GitExe = Find-Git

# ---------------------------------------------------------------- arguments

$script:Opt = @{ All = $false; Online = $false; Errors = $false; Generated = $false; Dxdiag = $false; NoColor = $false; Ascii = $false; Help = $false }
$positional = New-Object System.Collections.ArrayList
foreach ($a in $args) {
    switch -regex ([string]$a) {
        '^(--yes|-y|/y)$' { $script:Run.Yes = $true; continue }
        '^(--dry-run|-n)$' { $script:Run.DryRun = $true; continue }
        '^(--all|-a)$' { $script:Opt.All = $true; continue }
        '^--online$' { $script:Opt.Online = $true; continue }
        '^--errors$' { $script:Opt.Errors = $true; continue }
        '^--generated$' { $script:Opt.Generated = $true; continue }
        '^--dxdiag$' { $script:Opt.Dxdiag = $true; continue }
        '^--no-colou?r$' { $script:Opt.NoColor = $true; continue }
        '^--ascii$' { $script:Opt.Ascii = $true; continue }
        '^(--help|-h|/\?|-\?)$' { $script:Opt.Help = $true; continue }
        '^-' { $script:Unknown = [string]$a; continue }
        default { [void]$positional.Add([string]$a) }
    }
}
$script:Command = 'help'
if ($positional.Count -gt 0) { $script:Command = $positional[0].ToLowerInvariant(); $positional.RemoveAt(0) }
$script:Rest = @($positional)
Initialize-Output -NoColor $script:Opt.NoColor -Ascii $script:Opt.Ascii

# ---------------------------------------------------------------- shared pieces

function Get-Prop {
    # a JSON field that may be missing (strict mode throws on missing properties)
    param($Obj, [string]$Name, $Default = $null)
    if ($null -ne $Obj -and $Obj.PSObject.Properties[$Name]) { return $Obj.$Name }
    return $Default
}

function Assert-Clone {
    $r = Get-RepoState
    if (-not $script:GitExe) {
        Stop-Run 'Git is not installed, so this folder cannot be updated.' @('winget install -e --id Git.Git   # then open a new window', 'install   # turns a downloaded folder into a clone')
    }
    if (-not $r.IsGit) {
        Stop-Run 'This folder is not a git clone (it came from a ZIP download), so it cannot be updated in place.' @('install   # clones a fresh copy and carries over your Unity files')
    }
    return $r
}

function Assert-UnityClosed {
    param([string]$Doing)
    if (Test-UnityOpen $script:ProjectDir) {
        if ($script:Run.DryRun) { Write-Warn "Unity has this project open; close it before $Doing for real."; return }
        Stop-Run "Unity has this project open. Close Unity (File > Exit) before $Doing." @("maintain $script:Command   # again, once Unity is closed")
    }
}

function Get-Editor {
    # the Unity editor for this project, or stop with how to install it
    param($Project)
    $e = Select-UnityEditor $Project.Unity
    if ($e) {
        if (-not $e.Exact) { Write-Warn ("Unity " + $Project.Unity + " is not installed; using " + $e.Version + " (same release line). Unity will upgrade the project's Library on first open.") }
        return $e
    }
    $todo = @()
    $link = Get-HubInstallLink $Project.Unity $Project.Revision
    if ($link) { $todo += "Start-Process $link   # opens Unity Hub's install dialog for $($Project.Unity)" }
    $todo += "or in Unity Hub: Installs > Install Editor > Archive, version $($Project.Unity)"
    $todo += 'set BOVIE_UNITY to the full path of Unity.exe if it is installed somewhere unusual'
    Stop-Run ("Unity " + $Project.Unity + " (the version this project uses) is not installed.") $todo
}

function New-RunStamp { return (Get-Date).ToString('yyyyMMdd-HHmmss') }

function Get-BatchLogDir {
    $d = Join-Path (Get-DataDir) 'logs'
    if (-not (Test-Path -LiteralPath $d)) { New-Item -ItemType Directory -Path $d -Force | Out-Null }
    # keep the newest 40 files (about 20 runs)
    @(Get-ChildItem -LiteralPath $d -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -Skip 40) | Remove-Item -Force -ErrorAction SilentlyContinue
    return $d
}

function Quote-Arg {
    param([string]$A)
    if ($A -match '[\s"]') { return '"' + ($A -replace '"', '\"') + '"' }
    return $A
}

function Invoke-UnityBatch {
    # run Unity without a window; show its Bovie Sim lines and errors as they come; return the exit code
    param($Editor, [string]$Label, [string[]]$UnityArgs, [string]$LogFile)
    $all = @('-batchmode', '-projectPath', $script:ProjectDir, '-logFile', $LogFile) + $UnityArgs
    $line = ($all | ForEach-Object { Quote-Arg $_ }) -join ' '
    Write-Log "unity: $($Editor.Exe) $line"
    if ($script:Run.DryRun) { Write-Line 'off' "would run Unity $($Editor.Version): $Label" 'DarkGray'; return 0 }
    Write-Line '' "$Label (Unity $($Editor.Version), no window; the first run on a PC can take 10 minutes or more)" 'DarkGray'
    $t0 = Get-Date
    $p = Start-Process -FilePath $Editor.Exe -ArgumentList $line -PassThru -WindowStyle Hidden
    $null = $p.Handle   # needed so ExitCode is readable after exit
    $read = 0; $lastBeat = Get-Date
    while (-not $p.HasExited) {
        Start-Sleep -Milliseconds 1500
        $read = Show-NewLogLines $LogFile $read
        if (((Get-Date) - $lastBeat).TotalSeconds -ge 60) {
            Write-Note ("still running (" + (Format-Duration ((Get-Date) - $t0).TotalSeconds) + ")")
            $lastBeat = Get-Date
        }
    }
    $p.WaitForExit()
    $read = Show-NewLogLines $LogFile $read
    $code = $p.ExitCode
    Write-Log "unity exit $code after $([int]((Get-Date) - $t0).TotalSeconds)s"
    return $code
}

function Show-NewLogLines {
    param([string]$LogFile, [int]$From)
    if (-not (Test-Path -LiteralPath $LogFile)) { return $From }
    try {
        $fs = [IO.File]::Open($LogFile, 'Open', 'Read', 'ReadWrite')
        $sr = New-Object IO.StreamReader($fs)
        $lines = @(); $n = 0
        while ($null -ne ($l = $sr.ReadLine())) { if ($n -ge $From) { $lines += $l }; $n++ }
        $sr.Close()
    } catch { return $From }
    foreach ($l in $lines) {
        if ($l -match '^\[Bovie Sim\]') { Write-Note ($l -replace '^\[Bovie Sim\]\s*', '') }
        elseif ($l -match 'error CS\d{4}|No valid Unity Editor license|Aborting batchmode') { Write-Line 'bad' $l 'Red' }
    }
    return $n
}

function Show-BatchFailure {
    param([string]$LogFile, [int]$Code)
    $t = Read-TextOrNull $LogFile
    if ($t -and $t -match 'No valid Unity Editor license|License is not active|Error during license') {
        Write-Bad 'Unity could not find a licence. Open Unity Hub, sign in, and make sure a licence is active (Hub > Preferences > Licenses).'
    }
    $errs = @(Get-LogErrors $LogFile 12)
    if ($errs.Count -gt 0) { Write-Note 'From the Unity log:'; foreach ($e in $errs) { Write-Note ('  ' + $e.Trim()) } }
    Write-Note "Full log: $LogFile"
}

function Invoke-Prepare {
    # install missing Unity packages from the command line (they need Unity to restart before they compile)
    param($Editor, [string]$Stamp)
    for ($i = 1; $i -le 3; $i++) {
        $log = Join-Path (Get-BatchLogDir) "prepare-$Stamp-$i.log"
        $code = Invoke-UnityBatch $Editor 'Checking Unity packages' @('-quit', '-executeMethod', 'BovieSim.Bootstrap.BatchEntry.Prepare') $log
        if ($code -eq 0 -or $script:Run.DryRun) { return $true }
        if ($code -eq 3) { Write-Ok 'Installed packages / set input handling; starting Unity again to compile them'; continue }
        Write-Bad "Unity stopped with code $code while installing packages"
        Show-BatchFailure $log $code
        return $false
    }
    return $false
}

function Invoke-Setup {
    # Bovie Sim > Set Up Project, from the command line; true when it finished
    param($Editor)
    $stamp = New-RunStamp
    for ($i = 1; $i -le 3; $i++) {
        $log = Join-Path (Get-BatchLogDir) "setup-$stamp-$i.log"
        $code = Invoke-UnityBatch $Editor 'Running Set Up Project' @('-quit', '-executeMethod', 'BovieSim.Bootstrap.BatchEntry.Setup') $log
        if ($script:Run.DryRun) { return $true }
        switch ($code) {
            0 { Write-Ok 'Set Up Project finished'; return $true }
            3 { Write-Ok 'Installed Unity packages; starting Unity again to compile them'; continue }
            4 { Write-Bad 'The Bovie Sim editor scripts did not compile, so setup could not run'; Show-BatchFailure $log $code; return $false }
            default { Write-Bad "Set Up Project failed (Unity exit code $code)"; Show-BatchFailure $log $code; return $false }
        }
    }
    Write-Bad 'Unity kept asking to restart; open the project in Unity once and run Bovie Sim > Set Up Project there.'
    return $false
}

function Get-ProjectPaths {
    # tracked and untracked paths with a local change, as repo-relative forward-slash paths
    param($Repo)
    $tracked = @(); $untracked = @()
    foreach ($l in $Repo.Changed) {
        $p = $l.Substring(3).Trim('"')
        if ($p -match ' -> ') { $p = ($p -split ' -> ')[1].Trim('"') }
        if ($l.StartsWith('??')) { $untracked += $p } else { $tracked += $p }
    }
    return New-Object PSObject -Property @{ Tracked = $tracked; Untracked = $untracked }
}

function Get-Collisions {
    # local changes that moving to TARGET would overwrite: edited tracked files it changes, and
    # untracked files where it adds one (git refuses to switch over either)
    param($Repo, [string]$Target)
    $incoming = @((Invoke-Git @('diff', '--name-only', 'HEAD', $Target)).Out)
    $local = Get-ProjectPaths $Repo
    $hits = @()
    foreach ($p in $local.Tracked) { if ($incoming -contains $p) { $hits += New-Object PSObject -Property @{ Path = $p; Tracked = $true } } }
    foreach ($u in $local.Untracked) {
        foreach ($i in $incoming) {
            if ($i -eq $u -or ($u.EndsWith('/') -and $i.StartsWith($u))) { $hits += New-Object PSObject -Property @{ Path = $i; Tracked = $false } }
        }
    }
    return $hits
}

function Move-Aside {
    # copy colliding files to a backup folder, then give git the paths back. Returns the folder, or $null.
    param([object[]]$Hits, [string]$Why)
    if ($Hits.Count -eq 0) { return $null }
    $dest = Join-Path (Get-DataDir) ('backups\' + (New-RunStamp) + '-' + $Why)
    $ok = Invoke-Change ("Set aside $($Hits.Count) local file(s) the $Why touches, in $dest") {
        foreach ($h in $Hits) {
            $src = Join-Path $script:Root ($h.Path -replace '/', '\')
            if (Test-Path -LiteralPath $src) {
                $to = Join-Path $dest ($h.Path -replace '/', '\')
                New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
                Copy-Item -LiteralPath $src -Destination $to -Force
                if (-not $h.Tracked) { Remove-Item -LiteralPath $src -Force }
            }
        }
        $tracked = @($Hits | Where-Object { $_.Tracked } | ForEach-Object { $_.Path })
        if ($tracked.Count -gt 0) { return (Invoke-Git (@('checkout', 'HEAD', '--') + $tracked)) }
        return $true
    }
    if (-not $ok) { Stop-Run 'Could not set the local files aside; nothing was updated.' @('maintain doctor') }
    return $dest
}

function Add-History {
    param([string]$What, [string]$From, [string]$To)
    try {
        $f = Join-Path (Get-DataDir) 'history.txt'
        Add-Content -LiteralPath $f -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm') + "`t$What`t$From`t$To") -Encoding UTF8
    } catch { }
}

function Get-LastHistoryFrom {
    $t = Read-TextOrNull (Join-Path (Get-DataDir) 'history.txt')
    if (-not $t) { return $null }
    $last = @($t -split "`r?`n" | Where-Object { $_ -match '\t' })
    if ($last.Count -eq 0) { return $null }
    return ($last[-1] -split "`t")[2]
}

function Get-ChangelogSince {
    # changelog headings (and their first lines) newer than VERSION, as of REF
    param([string]$Ref, [string]$Since)
    $t = Get-FileAt $Ref 'CHANGELOG.md'
    if (-not $t) { return @() }
    $out = @(); $take = $false; $bullets = 0
    foreach ($l in ($t -split "`r?`n")) {
        if ($l -match '^## (\S+)') {
            if ($Since -and $Matches[1].TrimEnd(':') -eq $Since) { break }
            $take = $true; $bullets = 0
            $out += ('## ' + ($l -replace '^##\s*', ''))
            continue
        }
        if ($take -and $l -match '^- (.*)' -and $bullets -lt 6) {
            $b = $Matches[1] -replace '\*\*', ''
            $dot = $b.IndexOf('. ')
            if ($dot -gt 0 -and $dot -lt 90) { $b = $b.Substring(0, $dot + 1) }
            elseif ($b.Length -gt 90) { $cut = $b.LastIndexOf(' ', 86); if ($cut -lt 40) { $cut = 86 }; $b = $b.Substring(0, $cut).TrimEnd(',', ';', ':') + ' ...' }
            $out += $b; $bullets++
        }
    }
    return $out
}

function Show-VersionChange {
    # what moving from the working copy to REF changes for the person, and what they must do after
    param($Now, $Next, $Project)
    $from = $Now.App; if (-not $from) { $from = 'a version before 0.4.0' }
    $to = $Next.App; if (-not $to) { $to = 'a version before 0.4.0' }
    Write-KV 'Bovie Sim' ($(if ($Now.App -eq $Next.App) { $from + ' (same version, other commits)' } else { $from + '  ->  ' + $to }))
    $setupNeeded = $false
    if ($Next.Setup -and $Now.Setup -and [int]$Next.Setup -lt [int]$Now.Setup) {
        Write-KV 'Setup' ("must be re-run afterwards with maintain setup (generated assets v$($Now.Setup) -> v$($Next.Setup)); the older version cannot tell its assets are newer") 'warn'
        $setupNeeded = $true
    } elseif ($Next.Setup -and $Now.Setup -and $Next.Setup -ne $Now.Setup) {
        Write-KV 'Setup' ("must be re-run afterwards (generated assets v$($Now.Setup) -> v$($Next.Setup)); Unity offers it when the project opens") 'warn'
        $setupNeeded = $true
    } elseif ($Project.SetupDone -and $Next.Setup -and [int]$Project.SetupDone -lt [int]$Next.Setup) {
        Write-KV 'Setup' 'still needs running on this PC (it was out of date before)' 'warn'
        $setupNeeded = $true
    } else { Write-KV 'Setup' 'not needed' 'ok' }
    $unityNeeded = $null
    if ($Next.Unity -and $Next.Unity -ne $Now.Unity) {
        $have = @(Find-UnityEditors | Where-Object { $_.Version -eq $Next.Unity }).Count -gt 0
        if ($have) { Write-KV 'Unity' ("moves to $($Next.Unity) (installed)") 'ok' }
        else { Write-KV 'Unity' ("moves to $($Next.Unity), which is not installed yet") 'warn'; $unityNeeded = $Next }
    } else { Write-KV 'Unity' "$($Next.Unity) (unchanged)" 'ok' }
    return New-Object PSObject -Property @{ Setup = $setupNeeded; Unity = $unityNeeded }
}

function Show-NextSteps {
    param($Effects)
    Write-After 'Next'
    if ($Effects.Unity) {
        $link = Get-HubInstallLink $Effects.Unity.Unity $Effects.Unity.Revision
        if ($link) { Write-Todo "Start-Process $link   # install Unity $($Effects.Unity.Unity) with Unity Hub" }
    }
    if ($Effects.Setup) { Write-Todo 'maintain setup   # rebuilds the generated assets (or choose Set up when Unity asks)' }
    Write-Todo 'maintain open   # opens the project in the right Unity; then open Assets/BovieSim/Generated/Main.unity and press Play'
}

# ---------------------------------------------------------------- help

function Cmd-Help {
    Write-Banner 'Help' 'maintain COMMAND [options]'
    $sections = @(
        @('Look (changes nothing)', @(
            @('status', 'version, updates waiting, Unity, setup, graphics card, disk: one screen'),
            @('check', 'every check, with what to do about each problem  (--all for more detail, --online to ask GitHub)'),
            @('logs', 'where the logs are and their recent errors; logs editor|player|maintain|batch [N] [--errors]'))),
        @('Update', @(
            @('update', 'shows what is new and what it needs, asks, then updates from GitHub  (--dry-run, --yes)'),
            @('rollback', 'rollback list | VERSION (e.g. 0.3.0) | previous | latest: go back to an earlier version'))),
        @('Unity', @(
            @('open', 'open the project in the Unity version it needs'),
            @('setup', 'run Bovie Sim > Set Up Project without opening Unity (installs packages first if needed)'),
            @('test', 'run the EditMode tests and list any failures'),
            @('build', 'build the Windows app:  build [FOLDER]  (default Builds\BovieSim-VERSION)'))),
        @('Fix', @(
            @('doctor', 'find common problems and offer the safe fix for each  (--dry-run, --yes)'),
            @('clean', 'delete Unity''s Library and Temp caches (rebuilt on next open)  (--generated: setup output too)'),
            @('reset-settings', 'return the simulator''s settings to defaults (keeps a backup)'),
            @('report', 'collect logs and PC facts into a zip to send with a problem report  (--dxdiag adds DirectX details)')))
    )
    foreach ($s in $sections) {
        Write-Section $s[0]
        foreach ($c in $s[1]) {
            Write-Out '  ' -NoNewline; Write-Out ($c[0].PadRight(16)) -Fg Cyan -NoNewline
            $first = $true
            foreach ($l in (Split-Wrap $c[1] ((Get-UiWidth) - 18))) { if ($first) { Write-Out $l } else { Write-Out ((' ' * 18) + $l) }; $first = $false }
        }
    }
    Write-Section 'Options'
    Write-Para '--yes  take the default answer at every question.   --dry-run  show what would be done and change nothing.   --no-color, --ascii  plain output (also NO_COLOR).'
    Write-Section 'Where things are'
    Write-KV 'This clone' $script:Root
    Write-KV 'Transcript' (Join-Path (Get-DataDir) 'maintain.log')
    Write-KV 'Settings' (Get-SettingsPath)
    Write-Out ''
    Write-Para 'Double-click maintain.cmd for a menu. Run from a Command Prompt or PowerShell window in this folder: maintain status'
    return 0
}

# ---------------------------------------------------------------- status

function Cmd-Status {
    Write-Banner 'Status' $script:Root
    $r = Get-RepoState; $p = Get-ProjectState
    Write-Section 'Bovie Sim'
    $ver = $p.App; if (-not $ver) { $ver = 'before 0.4.0 (no version number)' }
    Write-KV 'Version' $ver
    if ($r.IsGit) {
        $where = $(if ($r.Detached) { 'rolled back (detached at ' + $r.Head + ')' } else { $r.Branch + ' at ' + $r.Head })
        $when = ''; if ($r.HeadDate) { $when = ', ' + (Format-Ago $r.HeadDate) }
        Write-KV 'Commit' ($where + ': ' + $r.HeadSubject + $when) $(if ($r.Detached) { 'warn' } else { '' })
        if ($r.HasUpstream) {
            $checked = 'never checked'; if ($r.LastFetch) { $checked = 'checked ' + (Format-Ago $r.LastFetch) }
            if ($r.Behind -gt 0) { Write-KV 'Updates' ("$($r.Behind) new commit(s) on GitHub ($checked): maintain update") 'warn' }
            else { Write-KV 'Updates' "up to date ($checked; maintain check --online asks GitHub now)" 'ok' }
            if ($r.Ahead -gt 0) { Write-KV 'Local commits' "$($r.Ahead) commit(s) not on GitHub" 'warn' }
        }
        if ($r.OtherChanged.Count -gt 0) { Write-KV 'Edited files' ("$($r.OtherChanged.Count) file(s) changed by hand (maintain check lists them)") 'warn' }
        else { Write-KV 'Edited files' $(if ($r.UnityChanged.Count -gt 0) { "none (plus $($r.UnityChanged.Count) Unity-written file(s), expected)" } else { 'none' }) 'ok' }
    } elseif (-not $script:GitExe) {
        Write-KV 'Clone' 'git is not installed: updates need it (maintain check says how)' 'warn'
    } else {
        Write-KV 'Clone' 'not a git clone (ZIP download): run install to switch to a clone' 'warn'
    }

    Write-Section 'Unity'
    $e = Select-UnityEditor $p.Unity
    if ($e -and $e.Exact) { Write-KV 'Editor' ("$($p.Unity) installed") 'ok' }
    elseif ($e) { Write-KV 'Editor' ("needs $($p.Unity); $($e.Version) is installed and will be used") 'warn' }
    else { Write-KV 'Editor' ("needs $($p.Unity), which is not installed (maintain check says how)") 'bad' }
    if ($p.MissingPackages.Count -gt 0) { Write-KV 'Setup' 'project not opened in Unity yet (packages not installed)' 'warn' }
    elseif ($p.SetupCurrent) { Write-KV 'Setup' "done (v$($p.SetupDone))" 'ok' }
    elseif ($p.SetupDone) { Write-KV 'Setup' "out of date (v$($p.SetupDone), needs v$($p.SetupRequired)): maintain setup" 'warn' }
    else { Write-KV 'Setup' 'not run yet: maintain setup, or Bovie Sim > Set Up Project in Unity' 'warn' }
    Write-KV 'Running' $(if (Test-UnityOpen $script:ProjectDir) { 'Unity has the project open' } else { 'no' })

    Write-Section 'This PC'
    $gpus = @(Get-Gpus)
    $main = @($gpus | Where-Object { -not $_.Integrated } | Sort-Object VramMb -Descending) + @($gpus | Where-Object { $_.Integrated })
    if ($main.Count -gt 0) {
        $g = $main[0]
        Write-KV 'Graphics' ("$($g.Name), $([Math]::Round($g.VramMb / 1024.0, 1)) GB; Auto detail: $(Get-ExpectedDetail $g)")
    } else { Write-KV 'Graphics' 'not detected' 'off' }
    $s = Get-SimSettings
    if ($s.Valid) { Write-KV 'Detail setting' ([string](Get-Prop $s.Data 'detail' 'Auto')) }
    elseif ($s.Exists) { Write-KV 'Settings' 'settings.json is damaged: maintain reset-settings' 'warn' }
    else { Write-KV 'Settings' 'defaults (no settings saved yet)' }
    $d = Get-FreeSpace $script:Root
    if ($d) {
        $st = ''; if ($d.Free -lt 3GB) { $st = 'bad' } elseif ($d.Free -lt 15GB) { $st = 'warn' }
        Write-KV 'Disk' ("$(Format-Size $d.Free) free on $($d.Drive)") $st
    }
    if ($script:Run.Failed) { return (Complete-Run 'bad' 'Needs attention: maintain check says what to do') }
    if ($script:Run.Warned.Count -gt 0) { return (Complete-Run 'warn' 'Mostly ready') }
    return (Complete-Run 'ok' 'Ready')
}

# ---------------------------------------------------------------- check

function Cmd-Check {
    Write-Banner 'Check' $(if ($script:Opt.Online) { 'with GitHub' } else { 'this PC only' })

    Write-Section 'Clone'
    if (-not $script:GitExe) {
        Write-KV 'Git' 'not installed' 'bad'
        Write-Note 'Install it with: winget install -e --id Git.Git   (or from git-scm.com), then open a new window.'
    } else {
        $gv = (Invoke-Native $script:GitExe @('--version')).Text -replace '^git version\s*', ''
        Write-KV 'Git' $gv 'ok'
    }
    $r = Get-RepoState
    if ($script:GitExe -and -not $r.IsGit) {
        Write-KV 'Clone' 'not a git clone (ZIP download): updates cannot be applied in place' 'warn'
        Write-Note 'Run install from this folder: it clones a fresh copy next to it and carries over your Unity files.'
    }
    if ($r.IsGit) {
        if (-not $r.Origin) { Write-KV 'GitHub' 'no "origin" remote' 'bad'; Write-Note 'maintain doctor can add it.' }
        elseif (-not $r.OriginOk) { Write-KV 'GitHub' ("origin is $($r.Origin), not $script:RepoUrl") 'warn' }
        else { Write-KV 'GitHub' $r.Origin 'ok' }
        if ($r.Operation) { Write-KV 'Git state' ("a $($r.Operation) was left unfinished") 'bad'; Write-Note 'maintain doctor can undo it.' }
        if ($r.IndexLock) { Write-KV 'Git lock' '.git\index.lock exists (a git command crashed, or one is running)' 'warn'; Write-Note 'maintain doctor removes it when no git is running.' }
        if ($r.Detached) { Write-KV 'Version' ("rolled back to $($r.Head); maintain rollback latest returns") 'warn' }
        elseif ($r.Branch -ne 'main') { Write-KV 'Branch' ("on '$($r.Branch)', not main: update only follows main") 'warn' }
        else { Write-KV 'Branch' 'main' 'ok' }
        if ($script:Opt.Online -and $r.Origin) {
            $ls = Invoke-Git @('ls-remote', 'origin', 'refs/heads/main')
            if ($ls.Code -ne 0) { Write-KV 'Online' ('could not reach GitHub: ' + (@($ls.Out) | Select-Object -Last 1)) 'warn' }
            else {
                $remote = (@($ls.Out)[0] -split '\s+')[0]
                $known = Get-GitLine @('rev-parse', 'origin/main')
                $headFull = Get-GitLine @('rev-parse', 'HEAD')
                if ($remote -eq $headFull) { Write-KV 'Updates' 'up to date with GitHub' 'ok' }
                elseif ($remote -eq $known) { Write-KV 'Updates' "$($r.Behind) new commit(s) waiting: maintain update" $(if ($r.Behind -gt 0) { 'warn' } else { 'ok' }) }
                else { Write-KV 'Updates' 'GitHub has newer commits: maintain update shows them' 'warn' }
            }
        } elseif ($r.HasUpstream) {
            $checked = 'never'; if ($r.LastFetch) { $checked = Format-Ago $r.LastFetch }
            if ($r.Behind -gt 0) { Write-KV 'Updates' "$($r.Behind) new commit(s) (last checked $checked): maintain update" 'warn' }
            else { Write-KV 'Updates' "none known (last checked $checked; --online asks GitHub)" 'ok' }
        }
        if ($r.Ahead -gt 0) { Write-KV 'Local commits' "$($r.Ahead) commit(s) on this PC that GitHub does not have; update cannot move past them" 'warn' }
        if ($r.OtherChanged.Count -gt 0) {
            Write-KV 'Edited files' "$($r.OtherChanged.Count) file(s) differ from the repo" 'warn'
            foreach ($l in ($r.OtherChanged | Select-Object -First 12)) { Write-Note $l }
            if ($r.OtherChanged.Count -gt 12) { Write-Note "... and $($r.OtherChanged.Count - 12) more (git status lists them)" }
            Write-Note 'Update keeps them unless it changes the same file; then it sets yours aside in a backup folder first.'
        } else { Write-KV 'Edited files' 'none' 'ok' }
        if ($r.UnityChanged.Count -gt 0) {
            Write-KV 'Unity files' "$($r.UnityChanged.Count) written by Unity (packages, settings, .meta): expected after opening" 'ok'
            if ($script:Opt.All) { foreach ($l in ($r.UnityChanged | Select-Object -First 20)) { Write-Note $l } }
        }
        if ($script:Opt.All) {
            Write-Note 'Recent commits:'
            foreach ($l in (Invoke-Git @('log', '-5', '--format=%h  %ad  %s', '--date=short')).Out) { Write-Note ('  ' + $l) }
        }
    }

    Write-Section 'Folder'
    Write-KV 'Path' $script:Root
    $lp = Get-LongPathsEnabled
    if ($script:Root.Length -gt 80 -and $lp -ne $true) { Write-KV 'Path length' ("$($script:Root.Length) characters; Unity's cache paths can pass Windows' 260 limit. A shorter folder (e.g. E:\claude\bovie-sim) is safer.") 'warn' }
    else { Write-KV 'Path length' ("$($script:Root.Length) characters" + $(if ($lp) { ', long paths enabled' } else { '' })) 'ok' }
    if (Test-UnderOneDrive $script:Root) { Write-KV 'OneDrive' 'the project is inside OneDrive: syncing Unity''s Library causes locked files and slow imports. Move it outside OneDrive.' 'warn' }
    $d = Get-FreeSpace $script:Root
    if ($d) {
        if ($d.Type -eq 'Network') { Write-KV 'Drive' 'network drive: Unity is slow and unreliable there; use a local disk' 'warn' }
        if ($d.Free -lt 3GB) { Write-KV 'Free space' ("$(Format-Size $d.Free) on $($d.Drive): too little for Unity's Library (3-6 GB)") 'bad' }
        elseif ($d.Free -lt 15GB) { Write-KV 'Free space' ("$(Format-Size $d.Free) on $($d.Drive): enough, but builds and the Library need room") 'warn' }
        else { Write-KV 'Free space' ("$(Format-Size $d.Free) on $($d.Drive)") 'ok' }
    }

    Write-Section 'Unity'
    $p = Get-ProjectState
    if (-not $p.Exists) { Write-KV 'Project' "unity\BovieSim is missing from this folder" 'bad' }
    $hub = Find-UnityHub
    if ($hub) { Write-KV 'Unity Hub' 'installed' 'ok' } else { Write-KV 'Unity Hub' 'not found (winget install -e --id Unity.UnityHub)' 'warn' }
    $all = @(Find-UnityEditors)
    $e = Select-UnityEditor $p.Unity
    if ($e -and $e.Exact) { Write-KV 'Editor' "$($p.Unity) installed" 'ok' }
    else {
        if ($e) { Write-KV 'Editor' ("needs $($p.Unity); only $($e.Version) of that line is installed (it works, but install the exact one when you can)") 'warn' }
        else { Write-KV 'Editor' ("needs $($p.Unity), which is not installed") 'bad' }
        $link = Get-HubInstallLink $p.Unity $p.Revision
        if ($link) { Write-Note "Install: Start-Process $link   (opens Unity Hub's install dialog)" }
    }
    if ($script:Opt.All) { foreach ($x in $all) { Write-Note ("found " + $x.Version + "  " + $x.Path) } }
    if ($p.MissingPackages.Count -gt 0) { Write-KV 'Packages' ("not installed yet: " + ($p.MissingPackages -join ', ') + ". Opening the project or maintain setup installs them.") 'warn' }
    else { Write-KV 'Packages' 'HDRP, Input System and Test Framework present' 'ok' }
    if ($p.SetupCurrent) { Write-KV 'Setup' "done (v$($p.SetupDone))" 'ok' }
    elseif ($p.SetupDone) { Write-KV 'Setup' "out of date (v$($p.SetupDone), this version needs v$($p.SetupRequired)): maintain setup" 'warn' }
    else { Write-KV 'Setup' 'not run yet: maintain setup' 'warn' }
    if ($p.HasRegistry -and -not $p.HasScene) { Write-KV 'Scene' 'Main.unity is missing although setup ran: maintain setup rebuilds it' 'warn' }
    $open = Test-UnityOpen $script:ProjectDir
    Write-KV 'Running' $(if ($open) { 'Unity has the project open (close it before update, setup or clean)' } else { 'not open' })
    if (-not $open -and (Test-Path -LiteralPath (Join-Path $script:ProjectDir 'Temp\UnityLockfile'))) { Write-Note 'A lock file from an earlier Unity session is left in Temp; it is harmless (doctor removes it).' }
    $errs = @(Get-LogErrors (Get-EditorLogPath) 8)
    $own = @($errs | Where-Object { $_ -match 'BovieSim|Bovie Sim' })
    if ($own.Count -gt 0) {
        Write-KV 'Last session' "Unity reported $($own.Count) Bovie Sim error line(s)" 'warn'
        foreach ($l in $own) { Write-Note ('  ' + $l.Trim()) }
    } elseif (Test-Path -LiteralPath (Get-EditorLogPath)) { Write-KV 'Last session' 'no Bovie Sim errors in Editor.log' 'ok' }
    if ($script:Opt.All -and $p.HasLibrary) {
        $size = 0; try { $size = (Get-ChildItem -LiteralPath (Join-Path $script:ProjectDir 'Library') -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum } catch { }
        Write-KV 'Library' ("$(Format-Size $size) (Unity's cache; maintain clean deletes it)")
    }

    Write-Section 'Graphics'
    $gpus = @(Get-Gpus)
    if ($gpus.Count -eq 0) { Write-KV 'Graphics' 'could not read the display adapters' 'off' }
    foreach ($g in $gpus) {
        $age = ''; $st = 'ok'
        if ($g.DriverDate) {
            $months = [int](((Get-Date) - $g.DriverDate).TotalDays / 30.4)
            $age = ', driver ' + $g.DriverDate.ToString('yyyy-MM-dd')
            if ($months -gt 12) { $st = 'warn'; $age = $age + " ($months months old: update it from the card maker's site)" }
        }
        $kind = $(if ($g.Integrated) { 'integrated' } else { 'graphics card' })
        Write-KV $kind ("$($g.Name), $([Math]::Round($g.VramMb / 1024.0, 1)) GB$age") $st
        if ($g.VramMb -gt 0 -and $g.VramMb -lt 1500) { Write-KV 'Too small' 'under 1.5 GB of video memory: the simulator will refuse to start on this adapter' 'bad' }
    }
    $discrete = @($gpus | Where-Object { -not $_.Integrated })
    if ($discrete.Count -gt 0) { Write-KV 'Auto detail' (Get-ExpectedDetail ($discrete | Sort-Object VramMb -Descending | Select-Object -First 1)) }
    if ($discrete.Count -gt 0 -and @($gpus | Where-Object { $_.Integrated }).Count -gt 0) {
        foreach ($exe in @(Get-PreferenceTargets $p)) {
            $pref = Get-GpuPreference $exe
            if ($pref -eq 2) { Write-KV 'GPU choice' ("high performance for " + (Split-Path -Leaf (Split-Path -Parent (Split-Path -Parent $exe))) + '\' + (Split-Path -Leaf $exe)) 'ok' }
            else { Write-KV 'GPU choice' ("Windows may run $exe on the integrated graphics: maintain doctor sets it to the graphics card") 'warn' }
        }
    }

    Write-Section 'Simulator'
    $s = Get-SimSettings
    if (-not $s.Exists) { Write-KV 'Settings' 'none saved yet (defaults)' 'ok' }
    elseif (-not $s.Valid) { Write-KV 'Settings' 'settings.json cannot be read: maintain reset-settings' 'warn' }
    else { Write-KV 'Settings' ("detail " + (Get-Prop $s.Data 'detail' 'Auto') + ", saved format v" + (Get-Prop $s.Data 'version' '?')) 'ok' }
    if ($script:Opt.All) {
        Write-KV 'Settings file' $s.Path
        $pe = @(Get-LogErrors (Get-PlayerLogPath) 6)
        if ($pe.Count -gt 0) { Write-KV 'Built app' "last run logged $($pe.Count) error line(s)" 'warn'; foreach ($l in $pe) { Write-Note ('  ' + $l.Trim()) } }
    }

    if ($script:Run.Failed) { return (Complete-Run 'bad' 'Problems found: the red lines above say what to do') }
    return (Complete-Run 'ok' 'Everything checked')
}

function Get-PreferenceTargets {
    # programs that should run on the graphics card: the project's Unity editor and any build in Builds\
    param($Project)
    $t = @()
    $e = Select-UnityEditor $Project.Unity
    if ($e) { $t += $e.Exe }
    foreach ($b in @(Get-ChildItem -Path (Join-Path $script:Root 'Builds\*\BovieSim.exe') -ErrorAction SilentlyContinue)) { $t += $b.FullName }
    return $t
}

# ---------------------------------------------------------------- update and rollback

function Cmd-Update {
    Write-Banner 'Update' $script:Root
    $r = Assert-Clone
    if ($r.Operation) { Stop-Run "A $($r.Operation) was left unfinished in this clone." @('maintain doctor   # offers to undo it') }
    Assert-UnityClosed 'updating (it would reimport files mid-update)'
    if (-not $r.Origin) { Stop-Run 'This clone has no "origin" remote to update from.' @('maintain doctor   # adds it') }
    if (-not $r.OriginOk) { Write-Warn "origin is $($r.Origin), not $script:RepoUrl; updating from it anyway." }

    $f = Invoke-Git @('fetch', '--prune', '--tags', '--quiet', 'origin', 'main')
    if ($f.Code -ne 0) { Stop-Run ('Could not reach GitHub: ' + (@($f.Out) | Select-Object -Last 1)) @('check the internet connection, then: maintain update') }
    Write-Ok 'Checked GitHub for updates'
    $r = Get-RepoState

    if (-not $r.Detached -and $r.Branch -ne 'main') {
        Stop-Run "This clone is on branch '$($r.Branch)', and update follows main." @('git checkout main   # then: maintain update')
    }
    if ($r.Ahead -gt 0 -and -not $r.Detached) {
        Stop-Run "main has $($r.Ahead) commit(s) made on this PC that GitHub does not have, so it cannot simply move forward." @('git log origin/main..main   # shows them; ask whoever made them before discarding')
    }
    $headFull = Get-GitLine @('rev-parse', 'HEAD'); $target = Get-GitLine @('rev-parse', 'origin/main')
    if ($headFull -eq $target -and -not $r.Detached) {
        $p = Get-ProjectState
        Write-Ok "Already up to date: Bovie Sim $($p.App) ($($r.Head))"
        if (-not $p.SetupCurrent -and $p.MissingPackages.Count -eq 0) { Write-Warn 'Setup has not been run for this version: maintain setup' }
        return (Complete-Run 'ok' 'Nothing to update')
    }

    $now = Get-VersionsAt ''; $next = Get-VersionsAt 'origin/main'; $project = Get-ProjectState
    if ($r.Detached) { Write-Line 'warn' "This clone was rolled back to $($r.Head) ($($now.App)); update returns it to the latest." 'Yellow' }

    Write-Section "What's new"
    $log = @((Invoke-Git @('log', '--format=%h  %s  (%cr)', 'HEAD..origin/main')).Out)
    if ($log.Count -eq 0 -and $r.Detached) { Write-Note 'No newer commits than the rolled-back one: only the switch back to main.' }
    foreach ($l in ($log | Select-Object -First 12)) { Write-Note $l }
    if ($log.Count -gt 12) { Write-Note "... and $($log.Count - 12) more" }
    if ($now.App -ne $next.App) {
        $cl = @(Get-ChangelogSince 'origin/main' $now.App)
        if ($cl.Count -gt 0) {
            Write-Out ''
            foreach ($l in $cl) { if ($l.StartsWith('## ')) { Write-Line '' $l.Substring(3) 'White' } else { Write-Note ($script:S.Dot + ' ' + $l) } }
        }
    }

    Write-Section 'What it means for this PC'
    $effects = Show-VersionChange $now $next $project
    $hits = @(Get-Collisions $r 'origin/main')
    if (@($hits | Where-Object { $_.Path -match '\.meta$' }).Count -gt 0 -and -not $effects.Setup) {
        Write-KV 'Setup' 'should be re-run afterwards: the update replaces .meta files Unity made on this PC' 'warn'
        $effects.Setup = $true
    }
    if ($hits.Count -gt 0) {
        Write-KV 'Your files' "$($hits.Count) local file(s) are changed by this update; they will be copied to a backup folder first:" 'warn'
        foreach ($h in ($hits | Select-Object -First 10)) { Write-Note ('  ' + $h.Path + $(if (Test-UnityManaged $h.Path) { '  (Unity rewrites this one by itself)' } else { '' })) }
    } elseif ($r.OtherChanged.Count -gt 0) { Write-KV 'Your files' "$($r.OtherChanged.Count) edited file(s) are not touched by the update and stay as they are" 'ok' }
    else { Write-KV 'Your files' 'no local edits in the way' 'ok' }

    Write-Out ''
    if (-not (Confirm-Step "Update to $($next.App) ($($target.Substring(0, 7))) now?" $true)) {
        if ($script:Run.DryRun) { Show-NextSteps $effects; return (Complete-Run 'ok' 'Dry run') }
        Write-Line 'off' 'Not updated.' 'DarkGray'
        return (Complete-Run 'ok' 'Left as it was')
    }

    Write-Section 'Updating'
    $backup = Move-Aside $hits 'update'
    Add-History 'update' $headFull $target
    if ($r.Detached) {
        $hasMain = Get-GitLine @('rev-parse', '--verify', '--quiet', 'refs/heads/main')
        if ($hasMain) { $ok = Invoke-Change 'Returned to main' { Invoke-Git @('checkout', '--quiet', 'main') } }
        else { $ok = Invoke-Change 'Recreated main' { Invoke-Git @('checkout', '--quiet', '-b', 'main', '--track', 'origin/main') } }
        if (-not $ok) { Stop-Run 'Could not switch back to main.' @('maintain doctor') }
    }
    $m = Invoke-Change "Updated to $($next.App)" { Invoke-Git @('merge', '--ff-only', '--quiet', 'origin/main') }
    if (-not $m) { Stop-Run 'The update did not apply; the clone is as it was.' @('maintain doctor', 'maintain report   # and send the zip') }
    if ($backup) { Write-Note "Your earlier copies of the changed files are in $backup" }
    Write-Note ("To go back: maintain rollback previous   (returns to $($r.Head))")
    Show-NextSteps $effects
    return (Complete-Run 'ok' "Bovie Sim updated to $($next.App)")
}

# Versions from before the version number lived in BovieVersion.cs (all on main on GitHub)
$script:EarlyVersions = [ordered]@{
    '0.1.0' = 'd50f2b3886e4d7f0715292831a02d745c44e70d7'
    '0.2.0' = '409a051fa41d6a881da7a0a5fd7aec989917d332'
    '0.3.0' = '3ed73c240edeaf0e40d3ab65a6bbdfa877aff71a'
}

function Get-VersionPoints {
    # every released version and the commit to use for it: its tag if there is one, otherwise the
    # last commit before the version number changed again
    $points = @()
    $log = Invoke-Git @('log', '--reverse', '--format=@@%H', '-p', '--unified=0', '-G', 'Version\s*=\s*"', '--', $script:BovieVersionFile)
    $commit = $null
    foreach ($l in $log.Out) {
        if ($l.StartsWith('@@') -and -not $l.StartsWith('@@ ')) { $commit = $l.Substring(2); continue }
        $m = [regex]::Match($l, '^\+.*Version\s*=\s*"([^"]+)"')
        if ($m.Success -and $commit) { $points += New-Object PSObject -Property @{ Version = $m.Groups[1].Value; Introduced = $commit } }
    }
    $list = @()
    foreach ($v in $script:EarlyVersions.Keys) {
        if (@($points | Where-Object { $_.Version -eq $v }).Count -gt 0) { continue }
        $c = Get-GitLine @('rev-parse', '--verify', '--quiet', "refs/tags/v$v^{commit}")
        $how = "tag v$v"
        if (-not $c) { $c = Get-GitLine @('rev-parse', '--verify', '--quiet', ($script:EarlyVersions[$v] + '^{commit}')); $how = 'release commit' }
        if ($c) { $list += New-Object PSObject -Property @{ Version = $v; Commit = $c; How = $how; Date = (Get-GitLine @('log', '-1', '--format=%ad', '--date=short', $c)) } }
    }
    for ($i = 0; $i -lt $points.Count; $i++) {
        $v = $points[$i].Version
        $tag = Get-GitLine @('rev-parse', '--verify', '--quiet', "refs/tags/v$v^{commit}")
        if ($tag) { $c = $tag; $how = "tag v$v" }
        elseif ($i + 1 -lt $points.Count) { $c = Get-GitLine @('rev-parse', ($points[$i + 1].Introduced + '^')); $how = 'last commit of ' + $v }
        else { $c = Get-GitLine @('rev-parse', 'origin/main'); if (-not $c) { $c = Get-GitLine @('rev-parse', 'HEAD') }; $how = 'latest' }
        $date = Get-GitLine @('log', '-1', '--format=%ad', '--date=short', $c)
        $list += New-Object PSObject -Property @{ Version = $v; Commit = $c; How = $how; Date = $date }
    }
    return $list
}

function Cmd-Rollback {
    $what = 'list'; if ($script:Rest.Count -gt 0) { $what = $script:Rest[0] }
    if ($what -eq 'latest') { return (Cmd-Update) }
    Write-Banner 'Rollback' $what
    $r = Assert-Clone
    # release tags made on GitHub since the last update (quietly; works offline too)
    [void](Invoke-Git @('fetch', '--tags', '--quiet', 'origin'))
    $points = @(Get-VersionPoints)
    if ($what -eq 'list') {
        Write-Section 'Versions'
        $here = Get-GitLine @('rev-parse', 'HEAD')
        foreach ($p in ($points | Sort-Object { [version]($_.Version -replace '[^\d.]', '') } -Descending)) {
            $mark = $(if ($p.Commit -eq $here) { 'ok' } else { '' })
            Write-KV $p.Version ("$($p.Date)  $($p.Commit.Substring(0, 7))  ($($p.How))" + $(if ($mark) { '  <- this PC' } else { '' })) $mark
        }
        $prev = Get-LastHistoryFrom
        if ($prev) { Write-KV 'previous' ("$($prev.Substring(0, 7)) (where the last update or rollback started)") }
        Write-After 'Use'
        Write-Todo 'maintain rollback 0.3.0   # go to a version'
        Write-Todo 'maintain rollback previous   # undo the last update or rollback'
        Write-Todo 'maintain rollback latest   # back to the newest'
        return (Complete-Run 'ok' "$($points.Count) version(s) available")
    }

    if ($r.Operation) { Stop-Run "A $($r.Operation) was left unfinished in this clone." @('maintain doctor') }
    Assert-UnityClosed 'switching versions'
    $target = $null; $label = $what
    if ($what -eq 'previous') {
        $target = Get-LastHistoryFrom
        if (-not $target) { Stop-Run 'No earlier update or rollback is recorded on this PC.' @('maintain rollback list') }
        $label = 'previous (' + $target.Substring(0, 7) + ')'
    } else {
        $v = $what.TrimStart('v', 'V')
        $hit = @($points | Where-Object { $_.Version -eq $v })
        if ($hit.Count -gt 0) { $target = $hit[0].Commit; $label = $v }
        else { $target = Get-GitLine @('rev-parse', '--verify', '--quiet', "$what^{commit}") }
        if (-not $target) { Stop-Run "No version or commit called '$what'." @('maintain rollback list') }
    }
    $headFull = Get-GitLine @('rev-parse', 'HEAD')
    if ($target -eq $headFull) { Write-Ok "Already at $label"; return (Complete-Run 'ok' 'Nothing to change') }

    $now = Get-VersionsAt ''; $next = Get-VersionsAt $target; $project = Get-ProjectState
    Write-Section 'What it means for this PC'
    $effects = Show-VersionChange $now $next $project
    Write-Note 'Your settings.json is kept; an older version ignores settings it does not know.'
    $hits = @(Get-Collisions $r $target)
    if ($hits.Count -gt 0) { Write-KV 'Your files' "$($hits.Count) local file(s) would be overwritten; they go to a backup folder first" 'warn' }

    Write-Out ''
    if (-not (Confirm-Step "Switch to $label now?" $true)) {
        if ($script:Run.DryRun) { return (Complete-Run 'ok' 'Dry run') }
        return (Complete-Run 'ok' 'Left as it was')
    }
    Write-Section 'Switching'
    $backup = Move-Aside $hits 'rollback'
    Add-History 'rollback' $headFull $target
    if ($r.Branch -eq 'main' -and $target -eq (Get-GitLine @('rev-parse', 'origin/main'))) {
        $ok = Invoke-Change "Switched to $label" { Invoke-Git @('merge', '--ff-only', '--quiet', 'origin/main') }
    } else {
        $ok = Invoke-Change "Switched to $label" { Invoke-Git @('checkout', '--quiet', '--detach', $target) }
    }
    if (-not $ok) { Stop-Run 'The switch did not apply; the clone is as it was.' @('maintain doctor') }
    if ($backup) { Write-Note "Your earlier copies are in $backup" }
    Write-Note 'maintain update or maintain rollback latest returns to the newest version.'
    Show-NextSteps $effects
    return (Complete-Run 'ok' "Now at $label")
}

# ---------------------------------------------------------------- Unity commands

function Cmd-Open {
    Write-Banner 'Open' $script:ProjectDir
    $p = Get-ProjectState
    if (Test-UnityOpen $script:ProjectDir) { Write-Ok 'Unity already has this project open (check the taskbar).'; return (Complete-Run 'ok' 'Already open') }
    $e = Get-Editor $p
    if (-not $p.SetupCurrent -and $p.MissingPackages.Count -eq 0) { Write-Note 'Setup is out of date: choose Set up when Unity asks (or run maintain setup first).' }
    if ($p.MissingPackages.Count -gt 0) { Write-Note 'First open: Unity installs HDRP and the other packages, recompiles, then offers Set Up Project. Allow 10-20 minutes.' }
    $ok = Invoke-Change "Started Unity $($e.Version)" {
        Start-Process -FilePath $e.Exe -ArgumentList ('-projectPath ' + (Quote-Arg $script:ProjectDir)) | Out-Null
        return $true
    }
    Write-Note 'Then open Assets/BovieSim/Generated/Main.unity (Project window) and press Play.'
    if ($ok) { return (Complete-Run 'ok' 'Unity is starting') }
    return (Complete-Run 'bad' 'Unity did not start')
}

function Cmd-Setup {
    Write-Banner 'Setup' 'Bovie Sim > Set Up Project, without a window'
    $p = Get-ProjectState
    Assert-UnityClosed 'running setup from here (or run Bovie Sim > Set Up Project in the open editor)'
    $e = Get-Editor $p
    if ($p.SetupCurrent) { Write-Note "Setup is already current (v$($p.SetupDone)); running it again rebuilds the generated assets." }
    Write-Note 'It downloads a CC0 HDRI and fabric texture if the internet is available (procedural fallbacks otherwise).'
    if (-not (Invoke-Setup $e)) { return (Complete-Run 'bad' 'Setup did not finish') }
    $p = Get-ProjectState
    if ($p.SetupCurrent) { Write-Ok "Generated assets are v$($p.SetupDone)" } else { Write-Warn 'Setup reported success but the registry does not show the new version.' }
    Write-After 'Next'
    Write-Todo 'maintain open   # then open Assets/BovieSim/Generated/Main.unity and press Play'
    return (Complete-Run 'ok' 'Project set up')
}

function Cmd-Test {
    Write-Banner 'Test' 'EditMode tests'
    $p = Get-ProjectState
    Assert-UnityClosed 'running tests from here (or use Window > General > Test Runner in the editor)'
    $e = Get-Editor $p
    $stamp = New-RunStamp
    if ($p.MissingPackages.Count -gt 0) { if (-not (Invoke-Prepare $e $stamp)) { return (Complete-Run 'bad' 'Packages could not be installed') } }
    $dir = Get-BatchLogDir
    $xml = Join-Path $dir "tests-$stamp.xml"; $log = Join-Path $dir "tests-$stamp.log"
    $code = Invoke-UnityBatch $e 'Running EditMode tests' @('-runTests', '-testPlatform', 'EditMode', '-testResults', $xml) $log
    if ($script:Run.DryRun) { return (Complete-Run 'ok' 'Dry run') }
    if (-not (Test-Path -LiteralPath $xml)) {
        Write-Bad "Unity produced no test results (exit code $code)"
        Show-BatchFailure $log $code
        return (Complete-Run 'bad' 'Tests did not run')
    }
    $x = [xml](Get-Content -LiteralPath $xml -Raw)
    $run = $x.SelectSingleNode('//test-run')
    $total = $run.GetAttribute('total'); $passed = $run.GetAttribute('passed'); $failed = $run.GetAttribute('failed'); $skipped = $run.GetAttribute('skipped')
    Write-KV 'Tests' "$total run: $passed passed, $failed failed, $skipped skipped" $(if ([int]$failed -gt 0) { 'bad' } else { 'ok' })
    foreach ($c in @($x.SelectNodes("//test-case[@result='Failed']"))) {
        Write-Line 'bad' $c.GetAttribute('fullname') 'Red'
        $msg = $c.SelectSingleNode('failure/message')
        if ($msg) { foreach ($l in (($msg.InnerText.Trim() -split "`r?`n") | Select-Object -First 4)) { Write-Note $l } }
    }
    Write-Note "Results: $xml"
    if ([int]$failed -gt 0 -or ($code -ne 0 -and $code -ne 2)) { return (Complete-Run 'bad' 'Tests failed') }
    return (Complete-Run 'ok' 'All tests passed')
}

function Cmd-Build {
    $p = Get-ProjectState
    $dir = Join-Path $script:Root ('Builds\BovieSim-' + $p.App)
    if ($script:Rest.Count -gt 0) { $dir = [IO.Path]::GetFullPath($script:Rest[0]) }
    Write-Banner 'Build' $dir
    Assert-UnityClosed 'building from here (or use File > Build Profiles in the editor)'
    $e = Get-Editor $p
    if (-not $p.SetupCurrent) {
        Write-Warn 'Setup has not been run for this version; the build needs it.'
        if (-not (Confirm-Step 'Run setup first?' $true)) { return (Complete-Run 'bad' 'Not built: setup required') }
        if (-not (Invoke-Setup $e)) { return (Complete-Run 'bad' 'Setup did not finish') }
    }
    if ((Test-Path -LiteralPath (Join-Path $dir 'BovieSim.exe')) -and -not (Confirm-Step "Replace the build already in $dir?" $true)) { return (Complete-Run 'ok' 'Left as it was') }
    $log = Join-Path (Get-BatchLogDir) ('build-' + (New-RunStamp) + '.log')
    $code = Invoke-UnityBatch $e 'Building the Windows app' @('-quit', '-executeMethod', 'BovieSim.Bootstrap.BatchEntry.BuildWindows', '-buildPath', $dir) $log
    if ($script:Run.DryRun) { return (Complete-Run 'ok' 'Dry run') }
    if ($code -ne 0) {
        if ($code -eq 5) { Write-Bad 'Unity says setup is still required: run maintain setup.' } else { Write-Bad "The build failed (Unity exit code $code)" }
        Show-BatchFailure $log $code
        return (Complete-Run 'bad' 'Not built')
    }
    $size = (Get-ChildItem -LiteralPath $dir -Recurse -File | Measure-Object Length -Sum).Sum
    Write-KV 'App' (Join-Path $dir 'BovieSim.exe') 'ok'
    Write-KV 'Size' (Format-Size $size)
    Write-After 'Next'
    Write-Todo 'Copy the whole folder to another PC and run BovieSim.exe there (settings are kept per user, outside the folder).'
    Write-Todo 'maintain doctor   # on a laptop with two graphics chips, makes Windows use the graphics card for it'
    return (Complete-Run 'ok' 'Built')
}

# ---------------------------------------------------------------- logs

function Cmd-Logs {
    $which = ''; $n = 40
    foreach ($a in $script:Rest) { $num = 0; if ([int]::TryParse($a, [ref]$num)) { $n = $num } else { $which = $a.ToLowerInvariant() } }
    $batch = @(Get-ChildItem -LiteralPath (Get-BatchLogDir) -Filter '*.log' -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending)
    $logs = [ordered]@{
        editor   = Get-EditorLogPath
        player   = Get-PlayerLogPath
        maintain = Join-Path (Get-DataDir) 'maintain.log'
        batch    = $(if ($batch.Count -gt 0) { $batch[0].FullName } else { Join-Path (Get-BatchLogDir) '(none yet)' })
    }
    if (-not $which) {
        Write-Banner 'Logs'
        foreach ($k in $logs.Keys) {
            $path = $logs[$k]
            if (Test-Path -LiteralPath $path) {
                $i = Get-Item -LiteralPath $path
                $errs = @(Get-LogErrors $path 50)
                $st = $(if ($errs.Count -gt 0 -and $k -ne 'maintain') { 'warn' } else { 'ok' })
                Write-KV $k ("$(Format-Size $i.Length), $(Format-Ago $i.LastWriteTime)" + $(if ($errs.Count -gt 0) { ", $($errs.Count) error line(s)" } else { '' })) $st
                Write-Note $path
            } else { Write-KV $k 'none yet' 'off'; Write-Note $path }
        }
        Write-After 'Show one'
        Write-Todo 'maintain logs editor 100   # last 100 lines of Unity''s editor log'
        Write-Todo 'maintain logs player --errors   # only the errors from the built app'
        return (Complete-Run 'ok' 'Logs listed')
    }
    if (-not $logs.Contains($which)) { Stop-Run "No log called '$which'." @('maintain logs   # lists them') }
    $path = $logs[$which]
    Write-Banner ('Logs: ' + $which) $path
    if (-not (Test-Path -LiteralPath $path)) { Write-Line 'off' 'This log does not exist yet.' 'DarkGray'; return (Complete-Run 'ok' 'Nothing to show') }
    if ($script:Opt.Errors) { $lines = @(Get-LogErrors $path $n) } else { $lines = @(Get-Content -LiteralPath $path -Tail $n) }
    foreach ($l in $lines) {
        $fg = ''
        if ($l -match 'error|exception|fail') { $fg = 'Red' } elseif ($l -match 'warn') { $fg = 'Yellow' } elseif ($l -match '\[Bovie Sim\]') { $fg = 'Cyan' }
        Write-Out ('  ' + $l) -Fg $fg
    }
    return (Complete-Run 'ok' "$($lines.Count) line(s)")
}

# ---------------------------------------------------------------- doctor

function Cmd-Doctor {
    Write-Banner 'Doctor' $script:Root
    $fixed = 0; $left = 0
    $r = Get-RepoState
    Write-Section 'Clone'
    if (-not $script:GitExe) {
        Write-Bad 'Git is not installed.'
        if ((Get-Command winget -ErrorAction SilentlyContinue) -and (Confirm-Step 'Install Git for Windows with winget now?' $true)) {
            if (Invoke-Change 'Installed Git' { Invoke-Native 'winget' @('install', '-e', '--id', 'Git.Git', '--accept-source-agreements', '--accept-package-agreements') }) {
                $script:Run.Failed = $false; $fixed++
                $script:GitExe = Find-Git
                Write-Note 'Open a new window so git is on the PATH everywhere.'
            }
        } else { $left++; Write-Todo 'winget install -e --id Git.Git' }
    }
    if ($script:GitExe -and -not $r.IsGit) { Write-Warn 'Not a git clone (ZIP download).'; Write-Todo 'install   # makes a clone next to this folder and carries over your Unity files'; $left++ }
    if ($r.IsGit) {
        $gitRunning = @(Get-Process git -ErrorAction SilentlyContinue).Count -gt 0
        if ($r.IndexLock) {
            if ($gitRunning) { Write-Warn '.git\index.lock exists while git is running; leaving it.'; $left++ }
            elseif (Confirm-Step 'Remove the stale .git\index.lock left by a crashed git command?' $true) {
                if (Invoke-Change 'Removed .git\index.lock' { Remove-Item -LiteralPath (Join-Path $script:Root '.git\index.lock') -Force; $true }) { $fixed++ }
            } else { $left++ }
        }
        if ($r.Operation) {
            Write-Warn "A $($r.Operation) was left unfinished."
            if (Confirm-Step "Undo the unfinished $($r.Operation) (returns the files to before it started)?" $true) {
                if (Invoke-Change "Undid the $($r.Operation)" { Invoke-Git @($r.Operation, '--abort') }) { $fixed++ } else { $left++ }
            } else { $left++ }
        }
        if (-not $r.Origin -or -not $r.OriginOk) {
            $q = $(if ($r.Origin) { "Point origin at $script:RepoUrl (now $($r.Origin))?" } else { "Add origin = $script:RepoUrl?" })
            if (Confirm-Step $q (-not $r.Origin)) {
                $verb = $(if ($r.Origin) { 'set-url' } else { 'add' })
                if (Invoke-Change 'Set origin' { Invoke-Git @('remote', $verb, 'origin', $script:RepoUrl) }) { $fixed++ }
            } else { $left++ }
        }
        if ((Get-GitLine @('config', '--get', 'core.longpaths')) -ne 'true') {
            if (Invoke-Change 'Allowed long paths in this clone (git core.longpaths)' { Invoke-Git @('config', 'core.longpaths', 'true') }) { $fixed++ }
        }
        if (-not $r.HasUpstream -and $r.Origin) {
            if (Invoke-Change 'Fetched origin/main' { Invoke-Git @('fetch', '--quiet', 'origin', 'main') }) { $fixed++ }
        }
        if ($r.Detached) { Write-Line 'warn' "Rolled back to $($r.Head); maintain rollback latest returns to the newest version." 'Yellow' }
        if ($fixed -eq 0 -and $left -eq 0) { Write-Ok 'Clone is healthy' }
    }

    Write-Section 'Unity project'
    $p = Get-ProjectState
    $open = Test-UnityOpen $script:ProjectDir
    if ($open) { Write-Line 'off' 'Unity has the project open: project fixes are skipped. Close Unity and run doctor again for them.' 'DarkGray' }
    else {
        $lock = Join-Path $script:ProjectDir 'Temp\UnityLockfile'
        if (Test-Path -LiteralPath $lock) {
            if (Invoke-Change 'Removed the lock file left by an earlier Unity session' { Remove-Item -LiteralPath $lock -Force; $true }) { $fixed++ }
        }
        $e = Select-UnityEditor $p.Unity
        if (-not $e) {
            Write-Bad "Unity $($p.Unity) is not installed."
            $link = Get-HubInstallLink $p.Unity $p.Revision
            if ($link -and (Find-UnityHub) -and (Confirm-Step "Open Unity Hub's install dialog for $($p.Unity)?" $true)) {
                Invoke-Change 'Opened Unity Hub' { Start-Process $link; $true } | Out-Null
                Write-Note 'Install it there (add no modules unless you want them), then run doctor again.'
            } elseif (-not (Find-UnityHub)) { Write-Todo 'winget install -e --id Unity.UnityHub   # then sign in and install the editor' }
            $left++
        } elseif ($p.MissingPackages.Count -gt 0 -or -not $p.SetupCurrent) {
            $why = $(if ($p.MissingPackages.Count -gt 0) { 'Packages are not installed and setup has not run' } elseif ($p.SetupDone) { "Setup is out of date (v$($p.SetupDone), needs v$($p.SetupRequired))" } else { 'Setup has not run' })
            Write-Warn $why
            if (Confirm-Step 'Run setup now from the command line (several minutes)?' $true) {
                if (Invoke-Setup $e) { $fixed++ } else { $left++ }
            } else { $left++; Write-Todo 'maintain setup' }
        } else { Write-Ok "Setup is current (v$($p.SetupDone))" }
        $errs = @(Get-LogErrors (Get-EditorLogPath) 8 | Where-Object { $_ -match 'error CS\d{4}' -and $_ -match 'BovieSim' })
        if ($errs.Count -gt 0) {
            Write-Warn 'The last Unity session had compile errors in Bovie Sim scripts:'
            foreach ($l in $errs) { Write-Note ('  ' + $l.Trim()) }
            if ($r.IsGit -and $r.Behind -gt 0) { Write-Todo 'maintain update   # a newer version may fix them' } else { Write-Todo 'maintain report   # and send the zip' }
            $left++
        }
    }

    Write-Section 'Graphics'
    $gpus = @(Get-Gpus)
    $hybrid = @($gpus | Where-Object { -not $_.Integrated }).Count -gt 0 -and @($gpus | Where-Object { $_.Integrated }).Count -gt 0
    if ($hybrid) {
        foreach ($exe in @(Get-PreferenceTargets $p)) {
            if ((Get-GpuPreference $exe) -eq 2) { continue }
            if (Confirm-Step "Make Windows run $(Split-Path -Leaf $exe) on the graphics card (Settings > Display > Graphics)?" $true) {
                if (Invoke-Change "Set high-performance graphics for $exe" {
                        $k = 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences'
                        if (-not (Test-Path $k)) { New-Item -Path $k -Force | Out-Null }
                        New-ItemProperty -Path $k -Name $exe -Value 'GpuPreference=2;' -PropertyType String -Force | Out-Null
                        $true
                    }) { $fixed++ }
            } else { $left++ }
        }
        Write-Ok 'Graphics card preference checked'
    } else { Write-Ok $(if ($gpus.Count -gt 0) { 'One graphics adapter: nothing to choose' } else { 'No adapters read (nothing to fix)' }) }
    $old = @($gpus | Where-Object { $_.DriverDate -and ((Get-Date) - $_.DriverDate).TotalDays -gt 365 })
    foreach ($g in $old) { Write-Warn "$($g.Name): driver from $($g.DriverDate.ToString('yyyy-MM-dd')). Update it from the maker's site (AMD Adrenalin, NVIDIA App, Intel)."; $left++ }

    Write-Section 'Simulator'
    $s = Get-SimSettings
    if ($s.Exists -and -not $s.Valid) {
        Write-Warn 'settings.json is damaged (the simulator falls back to defaults each start).'
        if (Confirm-Step 'Replace it with defaults (keeping a backup)?' $true) { if (Reset-SimSettings $s) { $fixed++ } } else { $left++ }
    } else { Write-Ok 'Settings readable' }
    $d = Get-FreeSpace $script:Root
    if ($d -and $d.Free -lt 3GB) { Write-Warn "Only $(Format-Size $d.Free) free on $($d.Drive)."; Write-Todo 'maintain clean   # deletes Unity''s Library cache (rebuilt on next open)'; $left++ }

    $headline = "$fixed fixed, $left left"
    if ($script:Run.DryRun) { $headline = "$fixed would be fixed, $left left for you" }
    if ($script:Run.Failed -or $left -gt 0) { return (Complete-Run 'warn' $headline) }
    return (Complete-Run 'ok' $(if ($fixed -gt 0) { $headline } else { 'Nothing needed fixing' }))
}

# ---------------------------------------------------------------- clean and settings

function Cmd-Clean {
    Write-Banner 'Clean' $script:ProjectDir
    Assert-UnityClosed 'cleaning'
    $targets = @('Library', 'Temp', 'Logs', 'obj')
    if ($script:Opt.Generated) {
        $targets += @('Assets\BovieSim\Generated', 'Assets\BovieSim\Generated.meta', 'Assets\BovieSim\Downloads', 'Assets\BovieSim\Downloads.meta',
            'Assets\BovieSim\Resources\BovieSim\SimAssets.asset', 'Assets\BovieSim\Resources\BovieSim\SimAssets.asset.meta')
    }
    $present = @($targets | Where-Object { Test-Path -LiteralPath (Join-Path $script:ProjectDir $_) })
    if ($present.Count -eq 0) { Write-Ok 'Nothing to delete'; return (Complete-Run 'ok' 'Already clean') }
    foreach ($t in $present) {
        $full = Join-Path $script:ProjectDir $t
        $size = 0; try { $size = (Get-ChildItem -LiteralPath $full -Recurse -File -Force -ErrorAction SilentlyContinue | Measure-Object Length -Sum).Sum } catch { }
        if (-not $size) { $size = 0 }
        Write-KV $t (Format-Size $size)
    }
    Write-Note 'Unity rebuilds the Library on the next open (10 minutes or more).'
    if ($script:Opt.Generated) { Write-Note 'Setup must run again afterwards (Unity offers it, or maintain setup).' }
    if (-not (Confirm-Step 'Delete these?' $true)) { return (Complete-Run 'ok' 'Left as it was') }
    foreach ($t in $present) {
        $full = Join-Path $script:ProjectDir $t
        Invoke-Change "Deleted $t" { Remove-Item -LiteralPath $full -Recurse -Force; $true } | Out-Null
    }
    return (Complete-Run 'ok' 'Cleaned')
}

function Reset-SimSettings {
    param($S)
    $bak = $S.Path + '.' + (New-RunStamp) + '.bak'
    return (Invoke-Change "Reset settings (old file kept as $(Split-Path -Leaf $bak))" { Move-Item -LiteralPath $S.Path -Destination $bak -Force; $true })
}

function Cmd-ResetSettings {
    Write-Banner 'Reset settings' (Get-SettingsPath)
    $s = Get-SimSettings
    if (-not $s.Exists) { Write-Ok 'No settings saved: the simulator already uses defaults.'; return (Complete-Run 'ok' 'Nothing to reset') }
    if ($s.Valid) { Write-KV 'Now' ("detail " + (Get-Prop $s.Data 'detail' 'Auto') + ', teaching moments ' + (Get-Prop $s.Data 'teachingMoments' '?') + ', tip gauge ' + (Get-Prop $s.Data 'tipGauge' '?')) }
    Write-Note 'Close the simulator first; it saves its settings when they change.'
    if (-not (Confirm-Step 'Return to default settings?' $true)) { return (Complete-Run 'ok' 'Left as it was') }
    if (Reset-SimSettings $s) { return (Complete-Run 'ok' 'Defaults on next start') }
    return (Complete-Run 'bad' 'Could not reset')
}

# ---------------------------------------------------------------- report

function Cmd-Report {
    Write-Banner 'Report' 'logs and PC facts for a problem report'
    $stamp = New-RunStamp
    $work = Join-Path (Get-DataDir) "reports\BovieSim-report-$stamp"
    $zip = $work + '.zip'
    if ($script:Run.DryRun) { Write-Line 'off' "would collect into $zip" 'DarkGray'; return (Complete-Run 'ok' 'Dry run') }
    New-Item -ItemType Directory -Path $work -Force | Out-Null

    # the full check, as plain text
    $facts = Join-Path $work 'check.txt'
    $saved = @{ Color = $script:Run.Color; Unicode = $script:Run.Unicode; Warned = $script:Run.Warned; Failed = $script:Run.Failed; Started = $script:Run.Started }
    $script:Run.Color = $false; $script:Run.Unicode = $false; $script:Run.Warned = New-Object System.Collections.ArrayList
    $script:Run.Capture = New-Object System.Text.StringBuilder
    $script:Opt.All = $true
    $script:S = $null; Initialize-Output -NoColor $true -Ascii $true
    try { Cmd-Check | Out-Null } catch { [void]$script:Run.Capture.AppendLine("check stopped: $_") }
    $text = $script:Run.Capture.ToString()
    $script:Run.Capture = $null
    foreach ($k in $saved.Keys) { $script:Run[$k] = $saved[$k] }
    Initialize-Output -NoColor (-not $saved.Color) -Ascii (-not $saved.Unicode)
    $head = @("Bovie Sim report $stamp", "PowerShell $($PSVersionTable.PSVersion) on $([Environment]::OSVersion.VersionString)", '')
    [IO.File]::WriteAllText($facts, (($head -join [Environment]::NewLine) + [Environment]::NewLine + $text))
    Write-Ok 'Ran every check (check.txt)'

    $copies = @(
        @((Get-EditorLogPath), 'Editor.log'), @(((Get-EditorLogPath) -replace 'Editor\.log$', 'Editor-prev.log'), 'Editor-prev.log'),
        @((Get-PlayerLogPath), 'Player.log'), @(((Get-PlayerLogPath) -replace 'Player\.log$', 'Player-prev.log'), 'Player-prev.log'),
        @((Get-SettingsPath), 'settings.json'), @((Join-Path (Get-DataDir) 'maintain.log'), 'maintain.log'), @((Join-Path (Get-DataDir) 'history.txt'), 'history.txt'),
        @((Join-Path $script:ProjectDir 'Packages\manifest.json'), 'manifest.json'), @((Join-Path $script:ProjectDir 'Packages\packages-lock.json'), 'packages-lock.json'),
        @((Join-Path $script:ProjectDir 'Assets\BovieSim\Generated\download-log.txt'), 'setup-download-log.txt')
    )
    $n = 0
    foreach ($c in $copies) { if (Test-Path -LiteralPath $c[0]) { Copy-Item -LiteralPath $c[0] -Destination (Join-Path $work $c[1]) -Force; $n++ } }
    foreach ($b in @(Get-ChildItem -LiteralPath (Get-BatchLogDir) -File -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 6)) {
        Copy-Item -LiteralPath $b.FullName -Destination (Join-Path $work ('batch-' + $b.Name)) -Force; $n++
    }
    Write-Ok "Copied $n log and settings file(s)"
    if ($script:GitExe -and (Test-Path -LiteralPath (Join-Path $script:Root '.git'))) {
        $g = @('## git status') + (Invoke-Git @('status', '--short', '--branch')).Out + @('', '## git log') + (Invoke-Git @('log', '-15', '--format=%h %ad %s', '--date=iso')).Out
        Set-Content -LiteralPath (Join-Path $work 'git.txt') -Value $g -Encoding UTF8
        Write-Ok 'Recorded the clone''s state (git.txt)'
    }
    if ($script:Opt.Dxdiag) {
        $dx = Join-Path $work 'dxdiag.txt'
        Invoke-Change 'Collected DirectX diagnostics (dxdiag.txt)' {
            $p = Start-Process -FilePath 'dxdiag.exe' -ArgumentList ('/t ' + (Quote-Arg $dx)) -PassThru -WindowStyle Hidden
            $null = $p.WaitForExit(120000)
            Test-Path -LiteralPath $dx
        } | Out-Null
    }
    Invoke-Change "Packed $(Split-Path -Leaf $zip)" { Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -Force; $true } | Out-Null
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
    Write-KV 'Report' $zip 'ok'
    Write-Note 'It holds logs, settings and PC facts (graphics card, Unity, folder paths, your Windows user name in those paths); no patient data or personal files.'
    try { Start-Process explorer.exe -ArgumentList ('/select,' + (Quote-Arg $zip)) } catch { }
    Write-After 'Next'
    Write-Todo "Attach the zip to a GitHub issue ($script:RepoUrl/issues/new/choose) or send it with a description of what happened."
    return (Complete-Run 'ok' 'Report ready')
}

# ---------------------------------------------------------------- menu (double-clicked maintain.cmd)

function Cmd-Menu {
    Write-Banner 'Menu' $script:Root
    $items = @(
        @('status', 'Status: one screen'), @('check', 'Check everything'), @('update', 'Update from GitHub'),
        @('open', 'Open in Unity'), @('setup', 'Run setup'), @('test', 'Run the tests'), @('build', 'Build the Windows app'),
        @('doctor', 'Find and fix problems'), @('report', 'Make a problem report'), @('help', 'All commands')
    )
    $c = Read-Choice 'What would you like to do?' $items
    if (-not $c) { return 0 }
    $script:Command = $c
    return (Invoke-Command2 $c)
}

function Invoke-Command2 {
    param([string]$Name)
    switch ($Name) {
        'help' { return (Cmd-Help) }
        'status' { return (Cmd-Status) }
        'check' { return (Cmd-Check) }
        'update' { return (Cmd-Update) }
        'upgrade' { return (Cmd-Update) }
        'pull' { return (Cmd-Update) }
        'rollback' { return (Cmd-Rollback) }
        'open' { return (Cmd-Open) }
        'setup' { return (Cmd-Setup) }
        'test' { return (Cmd-Test) }
        'tests' { return (Cmd-Test) }
        'build' { return (Cmd-Build) }
        'logs' { return (Cmd-Logs) }
        'log' { return (Cmd-Logs) }
        'doctor' { return (Cmd-Doctor) }
        'clean' { return (Cmd-Clean) }
        'reset-settings' { return (Cmd-ResetSettings) }
        'report' { return (Cmd-Report) }
        'menu' { return (Cmd-Menu) }
        default {
            Write-Banner 'Help'
            Write-Bad "Unknown command '$Name'."
            Write-Todo 'maintain help   # lists the commands'
            return 2
        }
    }
}

# ---------------------------------------------------------------- run

Start-Transcript2 (Join-Path (Get-DataDir) 'maintain.log') ((@($script:Command) + $script:Rest + @($args | Where-Object { "$_".StartsWith('-') })) -join ' ')
$code = 0
try {
    if ($script:Opt.Help) { $script:Command = 'help' }
    if (Get-Variable -Name Unknown -Scope Script -ErrorAction SilentlyContinue) { Write-Warn "Ignored unknown option $script:Unknown (maintain help lists the options)." }
    $result = Invoke-Command2 $script:Command
    $code = [int](@($result)[-1])
} catch {
    $msg = "$_"
    if ($msg -match '^BOVIE_STOP:(\d+)$') { $code = [int]$Matches[1] }
    else {
        Write-Bad ("Unexpected error: " + $msg)
        $where = $_.InvocationInfo.PositionMessage
        if ($where) { foreach ($l in ($where -split "`r?`n" | Select-Object -First 2)) { Write-Note $l } }
        Write-Note ('Transcript: ' + (Join-Path (Get-DataDir) 'maintain.log') + '   maintain report collects it with the other logs.')
        Write-Log ($_.ScriptStackTrace)
        $code = 1
    }
}
exit $code
