# Bovie Sim installer: makes a git clone of the project on this PC, so maintain.cmd can update it.
#   From a downloaded ZIP folder:  double-click install.cmd   (the clone takes the ZIP folder's place and
#                                   keeps Unity's files from it, so nothing is imported twice)
#   On a new PC, in PowerShell:    irm https://raw.githubusercontent.com/atate911/BovieSim/main/scripts/install.ps1 | iex
#
# Self-contained (it may run before the repo exists). Windows PowerShell 5.1 or later; ASCII-only source.

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2.0

$RepoUrl = 'https://github.com/atate911/BovieSim'
$Yes = $false
foreach ($a in $args) { if ("$a" -match '^(--yes|-y)$') { $Yes = $true } }
if ($env:BOVIE_YES) { $Yes = $true }

# ---------------------------------------------------------------- output (a small copy of lib\Common.ps1)

$Uni = [bool]($env:WT_SESSION -or $env:TERM_PROGRAM -or $env:BOVIE_UNICODE)
if ($Uni) { try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { } }
$M = @{
    Ok = $(if ($Uni) { [string][char]0x2714 } else { 'ok' }); Warn = $(if ($Uni) { [string][char]0x26A0 } else { '!!' })
    Bad = $(if ($Uni) { [string][char]0x2716 } else { 'XX' }); Todo = $(if ($Uni) { [string][char]0x2192 } else { '->' })
    H = $(if ($Uni) { [string][char]0x2550 } else { '=' })
}
$Color = -not $env:NO_COLOR
$Warnings = New-Object System.Collections.ArrayList
$Started = Get-Date

function Out2 { param([string]$T, [string]$Fg = '', [switch]$N) if ($Color -and $Fg) { Write-Host $T -ForegroundColor $Fg -NoNewline:$N } else { Write-Host $T -NoNewline:$N } }
function Say-Ok { param([string]$T) Out2 ("  $($M.Ok) ") Green -N; Out2 $T }
function Say-Warn { param([string]$T) Out2 ("  $($M.Warn) ") Yellow -N; Out2 $T Yellow; [void]$Warnings.Add($T) }
function Say-Bad { param([string]$T) Out2 ("  $($M.Bad) ") Red -N; Out2 $T Red }
function Say-Note { param([string]$T) Out2 ("     $T") DarkGray }
function Say-Todo { param([string]$T) Out2 ("  $($M.Todo) ") DarkGray -N; Out2 $T Cyan }
function Say-Head { param([string]$T) Write-Host ''; Out2 $T White }

function Ask {
    # yes/no with a default; --yes or BOVIE_YES takes the default
    param([string]$Q, [bool]$Default = $true)
    $h = $(if ($Default) { '[Y/n]' } else { '[y/N]' })
    if ($Yes) { Say-Note "$Q $h (--yes)"; return $Default }
    while ($true) {
        Out2 "  $Q " White -N; Out2 "$h " DarkGray -N
        $a = Read-Host
        if ($null -eq $a -or $a.Trim() -eq '') { return $Default }
        if ($a.Trim() -match '^(y|yes)$') { return $true }
        if ($a.Trim() -match '^(n|no)$') { return $false }
    }
}

function Finish {
    param([string]$State, [string]$Headline)
    if ($Warnings.Count -gt 0 -and $State -eq 'ok') { $State = 'warn' }
    Write-Host ''
    Out2 ($(if ($Uni) { [string][char]0x255A } else { '+' }) + ($M.H * 60) + $(if ($Uni) { [string][char]0x255D } else { '+' })) DarkCyan
    $badge = @{ ok = ' OK '; warn = 'WARN'; bad = 'FAIL' }[$State]
    $bg = @{ ok = 'DarkGreen'; warn = 'DarkYellow'; bad = 'DarkRed' }[$State]
    if ($Color) { Write-Host (" $badge ") -ForegroundColor White -BackgroundColor $bg -NoNewline } else { Write-Host "[$badge]" -NoNewline }
    $took = [int]((Get-Date) - $Started).TotalSeconds
    Out2 " $Headline" White -N
    if ($took -ge 3) { Out2 "  (${took}s)" DarkGray } else { Write-Host '' }
    if ($State -eq 'bad') { return 1 } else { return 0 }
}

function Run-Native {
    param([string]$Exe, [string[]]$A)
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { $o = & $Exe @A 2>&1 | ForEach-Object { "$_" }; $c = $LASTEXITCODE } catch { $o = @("$_"); $c = 127 } finally { $ErrorActionPreference = $old }
    return New-Object PSObject -Property @{ Code = $c; Out = @($o) }
}

# ---------------------------------------------------------------- finding things

function Find-Git {
    $c = Get-Command git -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in @("$env:ProgramFiles\Git\cmd\git.exe", "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe")) { if (Test-Path -LiteralPath $p) { return $p } }
    return $null
}

function Find-Hub {
    foreach ($p in @("$env:ProgramFiles\Unity Hub\Unity Hub.exe", "$env:LOCALAPPDATA\Programs\Unity Hub\Unity Hub.exe")) { if (Test-Path -LiteralPath $p) { return $p } }
    return $null
}

function Refresh-Path {
    # a program winget just installed is on the PATH of new windows only; read it again for this one
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}

function Install-WithWinget {
    param([string]$Id, [string]$Name)
    if (-not (Get-Command winget -ErrorAction SilentlyContinue)) { Say-Bad "winget is not available to install $Name."; return $false }
    Say-Note "winget install $Id (a Windows prompt may ask for permission)"
    $r = Run-Native 'winget' @('install', '-e', '--id', $Id, '--accept-source-agreements', '--accept-package-agreements')
    Refresh-Path
    if ($r.Code -eq 0) { Say-Ok "Installed $Name"; return $true }
    Say-Bad "Installing $Name failed (winget exit $($r.Code))"
    foreach ($l in ($r.Out | Select-Object -Last 5)) { Say-Note $l }
    return $false
}

function Test-Locked {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try { $f = [IO.File]::Open($Path, 'Open', 'ReadWrite', 'None'); $f.Close(); return $false } catch { return $true }
}

# ---------------------------------------------------------------- carrying Unity's files over from a ZIP folder

function Copy-UnityState {
    # what Unity made in the old folder and the clone does not have: its Library cache (moved: it is big),
    # .meta files, project settings, installed packages and the setup output
    param([string]$Old, [string]$New)
    $o = Join-Path $Old 'unity\BovieSim'; $n = Join-Path $New 'unity\BovieSim'
    if (-not (Test-Path -LiteralPath $o)) { return }
    $moved = 0; $copied = 0
    foreach ($d in @('Library', 'UserSettings')) {
        $src = Join-Path $o $d
        if ((Test-Path -LiteralPath $src) -and -not (Test-Path -LiteralPath (Join-Path $n $d))) {
            try { Move-Item -LiteralPath $src -Destination (Join-Path $n $d); $moved++ } catch { Say-Warn "Could not move $d (Unity rebuilds it): $_" }
        }
    }
    # files: everything under Assets, ProjectSettings and Packages that the clone lacks
    foreach ($top in @('Assets', 'ProjectSettings', 'Packages')) {
        $root = Join-Path $o $top
        if (-not (Test-Path -LiteralPath $root)) { continue }
        foreach ($f in @(Get-ChildItem -LiteralPath $root -Recurse -File -Force -ErrorAction SilentlyContinue)) {
            $rel = $f.FullName.Substring($o.Length).TrimStart('\', '/')
            $to = Join-Path $n $rel
            if (Test-Path -LiteralPath $to) { continue }
            # a .meta whose asset is gone in the new version would only make Unity complain
            if ($rel -match '\.meta$' -and $rel -notmatch '\\Generated|\\Downloads|SimAssets\.asset') {
                if (-not (Test-Path -LiteralPath ($to -replace '\.meta$', ''))) { continue }
            }
            New-Item -ItemType Directory -Path (Split-Path -Parent $to) -Force | Out-Null
            Copy-Item -LiteralPath $f.FullName -Destination $to
            $copied++
        }
    }
    # packages Unity installed into the old manifest (HDRP, Input System, Test Framework)
    $om = Join-Path $o 'Packages\manifest.json'; $nm = Join-Path $n 'Packages\manifest.json'
    if ((Test-Path -LiteralPath $om) -and (Test-Path -LiteralPath $nm)) {
        try {
            $oj = Get-Content -LiteralPath $om -Raw | ConvertFrom-Json
            $nj = Get-Content -LiteralPath $nm -Raw | ConvertFrom-Json
            $added = 0
            foreach ($p in $oj.dependencies.PSObject.Properties) {
                if (-not $nj.dependencies.PSObject.Properties[$p.Name]) { $nj.dependencies | Add-Member -NotePropertyName $p.Name -NotePropertyValue $p.Value; $added++ }
            }
            if ($added -gt 0) { [IO.File]::WriteAllText($nm, ($nj | ConvertTo-Json -Depth 10)); Say-Ok "Kept $added package(s) Unity had installed" }
        } catch { Say-Warn "Could not merge Packages\manifest.json (Unity reinstalls the packages on open): $_" }
    }
    if ($moved -gt 0) { Say-Ok "Moved Unity's Library cache over (no re-import)" }
    if ($copied -gt 0) { Say-Ok "Copied $copied file(s) Unity had made (.meta, settings, setup output)" }
}

# ---------------------------------------------------------------- the install

function Main {
    Write-Host ''
    Out2 ($(if ($Uni) { [string][char]0x2554 } else { '+' }) + $M.H + ' ') DarkCyan -N; Out2 'Bovie Sim' White -N; Out2 (' ' + $M.H + ' ') DarkCyan -N; Out2 'Install' Cyan -N; Out2 (' ' + ($M.H * 40)) DarkCyan

    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) { Say-Bad 'This installer is for Windows.'; return (Finish 'bad' 'Not installed') }

    # where it runs from: inside a ZIP download (convert it), inside a clone (nothing to do), or nowhere
    $from = $null
    if ($PSScriptRoot) {
        $here = Split-Path -Parent $PSScriptRoot
        if (Test-Path -LiteralPath (Join-Path $here '.git')) {
            Say-Ok "$here is already a git clone."
            Say-Todo 'maintain update   # keeps it up to date'
            return (Finish 'ok' 'Already installed')
        }
        if (Test-Path -LiteralPath (Join-Path $here 'unity\BovieSim')) { $from = $here }
    }

    Say-Head 'Tools'
    $git = Find-Git
    if ($git) { Say-Ok ('Git: ' + ((Run-Native $git @('--version')).Out -join ' ')) }
    else {
        Say-Warn 'Git is not installed; it is needed to download and update Bovie Sim.'
        if ((Ask 'Install Git for Windows now (winget)?' $true) -and (Install-WithWinget 'Git.Git' 'Git')) { $git = Find-Git }
        if (-not $git) { Say-Todo 'Install Git from https://git-scm.com/download/win, then run this again.'; return (Finish 'bad' 'Not installed') }
    }
    if (Find-Hub) { Say-Ok 'Unity Hub is installed' }
    else {
        Say-Warn 'Unity Hub is not installed (it installs and licenses the Unity editor).'
        if (Ask 'Install Unity Hub now (winget)?' $true) { [void](Install-WithWinget 'Unity.UnityHub' 'Unity Hub') }
    }

    Say-Head 'Folder'
    if ($from) {
        $dest = $from
        Say-Ok "Found a ZIP download at $from"
        Say-Note 'The clone will take its place; the ZIP folder is renamed and kept until you delete it.'
        if (Test-Locked (Join-Path $from 'unity\BovieSim\Temp\UnityLockfile')) { Say-Bad 'Unity has this project open. Close Unity, then run install again.'; return (Finish 'bad' 'Not installed') }
    } else {
        $dest = $(if (Test-Path 'E:\claude') { 'E:\claude\bovie-sim' } else { (Join-Path $env:SystemDrive 'dev\bovie-sim') })
        if (-not $Yes) {
            Out2 "  Clone into $dest " White -N; Out2 '(Enter = yes, or type another folder) ' DarkGray -N
            $a = Read-Host
            if ($a -and $a.Trim()) { $dest = [IO.Path]::GetFullPath($a.Trim().Trim('"')) }
        }
        if ((Test-Path -LiteralPath $dest) -and @(Get-ChildItem -LiteralPath $dest -Force).Count -gt 0) {
            if (Test-Path -LiteralPath (Join-Path $dest 'unity\BovieSim')) {
                if (Test-Path -LiteralPath (Join-Path $dest '.git')) { Say-Ok "$dest is already a clone."; Say-Todo "cd `"$dest`"; .\maintain update"; return (Finish 'ok' 'Already installed') }
                $from = $dest
                Say-Ok "Found a ZIP download at $dest; the clone will take its place."
            } else { Say-Bad "$dest is not empty."; Say-Todo 'Run again and choose an empty or new folder.'; return (Finish 'bad' 'Not installed') }
        }
    }
    foreach ($o in @($env:OneDrive, $env:OneDriveConsumer, $env:OneDriveCommercial)) {
        if ($o -and $dest.ToLowerInvariant().StartsWith($o.ToLowerInvariant())) { Say-Warn 'The folder is inside OneDrive; syncing Unity''s cache causes locked files. A folder outside OneDrive is better.' }
    }
    if ($dest.Length -gt 80) { Say-Warn "The path is $($dest.Length) characters long; Unity's cache paths can exceed Windows' limit. Shorter is safer." }
    try {
        $drive = New-Object IO.DriveInfo([IO.Path]::GetPathRoot($dest))
        if ($drive.AvailableFreeSpace -lt 8GB) { Say-Warn ('Only {0:0.0} GB free on {1}; Unity needs 5-10 GB for this project.' -f ($drive.AvailableFreeSpace / 1GB), $drive.Name) }
    } catch { }

    Write-Host ''
    if (-not (Ask "Clone $RepoUrl into $dest?" $true)) { return (Finish 'ok' 'Nothing changed') }

    Say-Head 'Installing'
    $old = $null
    if ($from) {
        $old = $from.TrimEnd('\') + '-zip-' + (Get-Date).ToString('yyyyMMdd-HHmm')
        try { Rename-Item -LiteralPath $from -NewName (Split-Path -Leaf $old); Say-Ok "Renamed the ZIP folder to $(Split-Path -Leaf $old)" }
        catch {
            Say-Bad "Could not rename $from ($_)."
            Say-Note 'Close Unity, Explorer windows and terminals that are inside that folder (including this one, if it was opened there), then run install again.'
            Say-Todo "or run install from another folder: powershell -ExecutionPolicy Bypass -File `"$PSCommandPath`""
            return (Finish 'bad' 'Not installed')
        }
    }
    $parent = Split-Path -Parent $dest
    if (-not (Test-Path -LiteralPath $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
    Say-Note 'Downloading (about 20 MB)...'
    $r = Run-Native $git @('-c', 'core.longpaths=true', 'clone', '--quiet', $RepoUrl, $dest)
    if ($r.Code -ne 0) {
        Say-Bad 'git clone failed:'
        foreach ($l in ($r.Out | Select-Object -Last 6)) { Say-Note $l }
        if ($old) { try { Rename-Item -LiteralPath $old -NewName (Split-Path -Leaf $from); Say-Ok 'Put the ZIP folder back where it was' } catch { Say-Warn "The ZIP folder is still at $old" } }
        return (Finish 'bad' 'Not installed')
    }
    [void](Run-Native $git @('-C', $dest, 'config', 'core.longpaths', 'true'))
    Say-Ok "Cloned into $dest"
    if ($old) { Copy-UnityState $old $dest }

    Say-Head 'Next'
    Say-Todo "cd `"$dest`""
    Say-Todo '.\maintain status   # what is installed and what is still needed'
    Say-Todo '.\maintain open   # opens the project in the Unity version it needs (offers the install if missing)'
    Say-Todo '.\maintain update   # later: brings in new versions'
    if ($old) { Say-Note "Once the clone works, delete the old folder: $old" }
    $code = Finish 'ok' 'Bovie Sim installed'
    if (Ask 'Show the status now?' $true) {
        & (Join-Path $dest 'maintain.cmd') status
    }
    return $code
}

$code = Main
# exit only when run as a file; under "irm | iex" exit would close the window
if ($PSCommandPath) { exit $code }
