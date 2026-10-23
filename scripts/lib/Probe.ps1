# Bovie Sim maintenance: facts about this PC, the clone and the Unity project. Read-only; every probe
# returns $null or an empty list instead of throwing, so status and check work on a half-set-up PC.

$script:RepoUrl = 'https://github.com/atate911/BovieSim'
$script:RequiredPackages = @('com.unity.render-pipelines.high-definition', 'com.unity.test-framework', 'com.unity.inputsystem')
$script:BovieVersionFile = 'unity/BovieSim/Assets/BovieSim/Core/BovieVersion.cs'
$script:SimAssetsFile = 'unity/BovieSim/Assets/BovieSim/Runtime/Rendering/SimAssets.cs'
$script:ProjectVersionFile = 'unity/BovieSim/ProjectSettings/ProjectVersion.txt'

function Get-DataDir { return (Join-Path $env:LOCALAPPDATA 'BovieSim') }

function Get-SettingsPath {
    # Unity's persistentDataPath for company "Bovie Sim", product "BovieSim"
    $base = $env:USERPROFILE; if (-not $base) { $base = $HOME }
    return (Join-Path $base 'AppData\LocalLow\Bovie Sim\BovieSim\settings.json')
}

function Get-EditorLogPath { return (Join-Path $env:LOCALAPPDATA 'Unity\Editor\Editor.log') }
function Get-PlayerLogPath { return (Join-Path (Split-Path -Parent (Get-SettingsPath)) 'Player.log') }

function Read-TextOrNull {
    param([string]$Path)
    try { if (Test-Path -LiteralPath $Path) { return [IO.File]::ReadAllText($Path) } } catch { }
    return $null
}

function Find-Git {
    $c = Get-Command git -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    foreach ($p in @("$env:ProgramFiles\Git\cmd\git.exe", "${env:ProgramFiles(x86)}\Git\cmd\git.exe", "$env:LOCALAPPDATA\Programs\Git\cmd\git.exe")) {
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }
    return $null
}

function Invoke-Git {
    param([string[]]$Arguments)
    if (-not $script:GitExe) { return New-Object PSObject -Property @{ Code = 127; Out = @('git is not installed'); Text = 'git is not installed' } }
    return Invoke-Native $script:GitExe (@('-C', $script:Root) + $Arguments)
}

function Get-GitLine {
    # first line of a git command's output, or $null when it failed
    param([string[]]$Arguments)
    $r = Invoke-Git $Arguments
    if ($r.Code -ne 0 -or $r.Out.Count -eq 0) { return $null }
    return $r.Out[0]
}

# ---------------------------------------------------------------- versions in files and commits

function Get-VersionFromText {
    param([string]$Text, [string]$Pattern)
    if (-not $Text) { return $null }
    $m = [regex]::Match($Text, $Pattern)
    if ($m.Success) { return $m.Groups[1].Value }
    return $null
}

function Get-FileAt {
    # a repo file as of REF (a commit, tag or origin/main), or the working copy when REF is empty
    param([string]$Ref, [string]$RelPath)
    if (-not $Ref) { return Read-TextOrNull (Join-Path $script:Root $RelPath) }
    $r = Invoke-Git @('show', "${Ref}:$RelPath")
    if ($r.Code -ne 0) { return $null }
    return ($r.Out -join "`n")
}

function Get-VersionsAt {
    # what a commit says about itself: app version, setup version, Unity editor
    param([string]$Ref)
    $pv = Get-FileAt $Ref $script:ProjectVersionFile
    return New-Object PSObject -Property @{
        App      = Get-VersionFromText (Get-FileAt $Ref $script:BovieVersionFile) 'Version\s*=\s*"([^"]+)"'
        Setup    = Get-VersionFromText (Get-FileAt $Ref $script:SimAssetsFile) 'CurrentSetupVersion\s*=\s*(\d+)'
        Unity    = Get-VersionFromText $pv 'm_EditorVersion:\s*(\S+)'
        Revision = Get-VersionFromText $pv 'm_EditorVersionWithRevision:\s*\S+\s*\((\w+)\)'
    }
}

# ---------------------------------------------------------------- the clone

function Get-RepoState {
    $s = [ordered]@{
        IsGit = $false; Branch = $null; Detached = $false; Head = $null; HeadSubject = ''; HeadDate = $null
        Origin = $null; OriginOk = $false; HasUpstream = $false; Ahead = 0; Behind = 0; LastFetch = $null
        Operation = $null; IndexLock = $false; Changed = @(); UnityChanged = @(); OtherChanged = @()
    }
    $gitDir = Join-Path $script:Root '.git'
    if (-not $script:GitExe -or -not (Test-Path -LiteralPath $gitDir)) { return New-Object PSObject -Property $s }
    $s.IsGit = $true
    $s.Head = Get-GitLine @('rev-parse', '--short', 'HEAD')
    $b = Get-GitLine @('symbolic-ref', '--quiet', '--short', 'HEAD')
    if ($b) { $s.Branch = $b } else { $s.Detached = $true }
    $s.HeadSubject = Get-GitLine @('log', '-1', '--format=%s')
    $d = Get-GitLine @('log', '-1', '--format=%ct')
    if ($d) { $s.HeadDate = [DateTimeOffset]::FromUnixTimeSeconds([int64]$d).LocalDateTime }
    $s.Origin = Get-GitLine @('remote', 'get-url', 'origin')
    if ($s.Origin) { $s.OriginOk = [bool]($s.Origin -match 'github\.com[:/]atate911/BovieSim(\.git)?/?$') }
    if (Get-GitLine @('rev-parse', '--verify', '--quiet', 'origin/main')) {
        $s.HasUpstream = $true
        $c = Get-GitLine @('rev-list', '--left-right', '--count', 'HEAD...origin/main')
        if ($c -match '^(\d+)\s+(\d+)$') { $s.Ahead = [int]$Matches[1]; $s.Behind = [int]$Matches[2] }
    }
    $fh = Join-Path $gitDir 'FETCH_HEAD'
    if (Test-Path -LiteralPath $fh) { $s.LastFetch = (Get-Item -LiteralPath $fh).LastWriteTime }
    foreach ($op in @(@('MERGE_HEAD', 'merge'), @('rebase-merge', 'rebase'), @('rebase-apply', 'rebase'), @('CHERRY_PICK_HEAD', 'cherry-pick'), @('REVERT_HEAD', 'revert'))) {
        if (Test-Path -LiteralPath (Join-Path $gitDir $op[0])) { $s.Operation = $op[1] }
    }
    $s.IndexLock = Test-Path -LiteralPath (Join-Path $gitDir 'index.lock')
    $st = Invoke-Git @('status', '--porcelain=v1')
    if ($st.Code -eq 0) {
        $s.Changed = @($st.Out | Where-Object { $_.Length -gt 3 })
        # Unity writes these itself when the project is opened (packages it installed, its settings files,
        # .meta files for every asset); they are expected on a PC that has opened the project
        $s.UnityChanged = @($s.Changed | Where-Object { Test-UnityManaged $_.Substring(3) })
        $s.OtherChanged = @($s.Changed | Where-Object { -not (Test-UnityManaged $_.Substring(3)) })
    }
    return New-Object PSObject -Property $s
}

function Test-UnityManaged {
    param([string]$Path)
    $p = $Path.Trim('"') -replace '\\', '/'
    return [bool]($p -match '^unity/BovieSim/(Packages/|ProjectSettings/|UserSettings/)' -or $p -match '\.meta$' -or $p -match '^unity/BovieSim/Assets/[^/]*\.meta$')
}

# ---------------------------------------------------------------- the Unity project

function Get-ProjectState {
    $dir = Join-Path $script:Root 'unity\BovieSim'
    $v = Get-VersionsAt ''
    $assetText = Read-TextOrNull (Join-Path $dir 'Assets\BovieSim\Resources\BovieSim\SimAssets.asset')
    $done = Get-VersionFromText $assetText 'setupVersion:\s*(\d+)'
    $manifest = Read-TextOrNull (Join-Path $dir 'Packages\manifest.json')
    $missing = @()
    foreach ($p in $script:RequiredPackages) { if (-not $manifest -or $manifest -notmatch [regex]::Escape('"' + $p + '"')) { $missing += $p } }
    return New-Object PSObject -Property @{
        Dir = $dir
        Exists = Test-Path -LiteralPath (Join-Path $dir 'Assets\BovieSim')
        App = $v.App; SetupRequired = $v.Setup; Unity = $v.Unity; Revision = $v.Revision
        SetupDone = $done
        SetupCurrent = [bool]($done -and $v.Setup -and ([int]$done -ge [int]$v.Setup))
        HasRegistry = [bool]$assetText
        HasScene = Test-Path -LiteralPath (Join-Path $dir 'Assets\BovieSim\Generated\Main.unity')
        HasLibrary = Test-Path -LiteralPath (Join-Path $dir 'Library')
        MissingPackages = $missing
    }
}

function Test-FileLocked {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return $false }
    try { $f = [IO.File]::Open($Path, 'Open', 'ReadWrite', 'None'); $f.Close(); return $false } catch { return $true }
}

function Get-UnityProcesses {
    # Unity editors that have THIS project open (their command line names the project folder)
    param([string]$ProjectDir)
    $found = @()
    $want = ($ProjectDir -replace '/', '\').TrimEnd('\').ToLowerInvariant()
    try {
        foreach ($p in @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" -ErrorAction Stop)) {
            $cl = ''; if ($p.CommandLine) { $cl = ($p.CommandLine -replace '/', '\').ToLowerInvariant() }
            if ($cl.Contains($want)) { $found += $p.ProcessId }
        }
    } catch { }
    return $found
}

function Test-UnityOpen {
    # true when a Unity editor has this project open: by its command line, or by the lock it holds
    param([string]$ProjectDir)
    if (@(Get-UnityProcesses $ProjectDir).Count -gt 0) { return $true }
    return (Test-FileLocked (Join-Path $ProjectDir 'Temp\UnityLockfile'))
}

# ---------------------------------------------------------------- Unity Hub and editors

function Find-UnityHub {
    foreach ($p in @("$env:ProgramFiles\Unity Hub\Unity Hub.exe", "$env:LOCALAPPDATA\Programs\Unity Hub\Unity Hub.exe")) {
        if ($p -and (Test-Path -LiteralPath $p)) { return $p }
    }
    foreach ($k in @('HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*')) {
        try {
            foreach ($e in @(Get-ItemProperty $k -ErrorAction SilentlyContinue | Where-Object { $_.PSObject.Properties['DisplayName'] -and $_.DisplayName -like 'Unity Hub*' })) {
                if ($e.PSObject.Properties['InstallLocation'] -and $e.InstallLocation) {
                    $exe = Join-Path $e.InstallLocation 'Unity Hub.exe'
                    if (Test-Path -LiteralPath $exe) { return $exe }
                }
            }
        } catch { }
    }
    return $null
}

function Get-EditorVersionFromPath {
    param([string]$Exe)
    $m = [regex]::Match($Exe, '(\d{4}\.\d+\.\d+[abfpx]\d+)')
    if ($m.Success) { return $m.Groups[1].Value }
    try {
        $pv = (Get-Item -LiteralPath $Exe).VersionInfo.ProductVersion
        $m = [regex]::Match($pv, '(\d{4}\.\d+\.\d+[abfpx]\d+)')
        if ($m.Success) { return $m.Groups[1].Value }
    } catch { }
    return $null
}

function Find-UnityEditors {
    # every Unity editor this PC has: where Unity Hub puts them, where it was told to, and BOVIE_UNITY
    $exes = New-Object System.Collections.ArrayList
    $roots = @("$env:ProgramFiles\Unity\Hub\Editor")
    $hubData = Join-Path $env:APPDATA 'UnityHub'
    $sec = Read-TextOrNull (Join-Path $hubData 'secondaryInstallPath.json')
    if ($sec) { $p = $sec.Trim().Trim('"') -replace '\\\\', '\'; if ($p) { $roots += $p } }
    foreach ($r in $roots) {
        if (-not $r -or -not (Test-Path -LiteralPath $r)) { continue }
        foreach ($d in @(Get-ChildItem -LiteralPath $r -Directory -ErrorAction SilentlyContinue)) {
            $exe = Join-Path $d.FullName 'Editor\Unity.exe'
            if (Test-Path -LiteralPath $exe) { [void]$exes.Add($exe) }
        }
    }
    foreach ($f in @('editors-v2.json', 'editors.json')) {
        $t = Read-TextOrNull (Join-Path $hubData $f)
        if (-not $t) { continue }
        foreach ($m in [regex]::Matches($t, '"([A-Za-z]:[^"]*?Unity\.exe)"')) {
            $p = $m.Groups[1].Value -replace '\\\\', '\'
            if (Test-Path -LiteralPath $p) { [void]$exes.Add($p) }
        }
    }
    foreach ($exe in @(Get-ChildItem -Path "$env:ProgramFiles\Unity*\Editor\Unity.exe" -ErrorAction SilentlyContinue)) { [void]$exes.Add($exe.FullName) }
    if ($env:BOVIE_UNITY -and (Test-Path -LiteralPath $env:BOVIE_UNITY)) { [void]$exes.Add($env:BOVIE_UNITY) }
    $seen = @{}; $list = @()
    foreach ($e in $exes) {
        $k = $e.ToLowerInvariant(); if ($seen.ContainsKey($k)) { continue }; $seen[$k] = $true
        $list += New-Object PSObject -Property @{ Path = $e; Version = Get-EditorVersionFromPath $e }
    }
    return $list
}

function Compare-UnityVersion {
    # -1, 0, 1 for 6000.6.7f1-style versions (f after b after a)
    param([string]$A, [string]$B)
    $pa = [regex]::Match($A, '^(\d+)\.(\d+)\.(\d+)([abfpx])(\d+)'); $pb = [regex]::Match($B, '^(\d+)\.(\d+)\.(\d+)([abfpx])(\d+)')
    if (-not $pa.Success -or -not $pb.Success) { return [string]::Compare($A, $B) }
    $rank = @{ 'x' = 0; 'a' = 1; 'b' = 2; 'f' = 3; 'p' = 4 }
    foreach ($i in 1, 2, 3) { $d = [int]$pa.Groups[$i].Value - [int]$pb.Groups[$i].Value; if ($d -ne 0) { return [Math]::Sign($d) } }
    $d = $rank[$pa.Groups[4].Value] - $rank[$pb.Groups[4].Value]; if ($d -ne 0) { return [Math]::Sign($d) }
    return [Math]::Sign([int]$pa.Groups[5].Value - [int]$pb.Groups[5].Value)
}

function Select-UnityEditor {
    # the editor to run this project with: the exact version it names, else the newest of the same
    # major.minor (Unity upgrades the project on open; Library is rebuilt). Returns @{Exe; Version; Exact}.
    param([string]$Wanted)
    $all = @(Find-UnityEditors)
    $exact = @($all | Where-Object { $_.Version -eq $Wanted })
    if ($exact.Count -gt 0) { return New-Object PSObject -Property @{ Exe = $exact[0].Path; Version = $Wanted; Exact = $true } }
    $mm = ($Wanted -split '\.')[0..1] -join '.'
    $near = @($all | Where-Object { $_.Version -and $_.Version.StartsWith($mm + '.') -and (Compare-UnityVersion $_.Version $Wanted) -gt 0 } | Sort-Object { $_.Version } -Descending)
    if ($near.Count -gt 0) { return New-Object PSObject -Property @{ Exe = $near[0].Path; Version = $near[0].Version; Exact = $false } }
    return $null
}

function Get-HubInstallLink {
    # unityhub:// link that opens Unity Hub's install dialog for exactly this editor
    param([string]$Version, [string]$Revision)
    if ($Revision) { return "unityhub://$Version/$Revision" }
    return $null
}

# ---------------------------------------------------------------- graphics

function Get-Gpus {
    # display adapters from the driver registry (it has the real VRAM size; WMI caps it at 4 GB)
    $class = 'HKLM:\SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}'
    $virtual = 'Basic Display|Basic Render|Remote|Virtual|Parsec|Citrix|Hyper-V|DisplayLink|Splashtop|Indirect|IddSample|Mirage|VNC'
    $list = @()
    try {
        foreach ($k in @(Get-ChildItem -Path $class -ErrorAction SilentlyContinue | Where-Object { $_.PSChildName -match '^\d{4}$' })) {
            $p = Get-ItemProperty -LiteralPath $k.PSPath -ErrorAction SilentlyContinue
            if (-not $p -or -not $p.PSObject.Properties['DriverDesc']) { continue }
            if ($p.DriverDesc -match $virtual) { continue }
            $vram = 0
            foreach ($n in @('HardwareInformation.qwMemorySize', 'HardwareInformation.MemorySize')) {
                if (-not $p.PSObject.Properties[$n]) { continue }
                $raw = $p.$n
                if ($raw -is [byte[]]) { if ($raw.Length -ge 8) { $raw = [BitConverter]::ToUInt64($raw, 0) } else { $raw = [BitConverter]::ToUInt32($raw, 0) } }
                if ([double]$raw -gt $vram) { $vram = [double]$raw }
            }
            $date = $null
            if ($p.PSObject.Properties['DriverDate']) {
                try { $date = [datetime]::ParseExact($p.DriverDate, 'M-d-yyyy', [Globalization.CultureInfo]::InvariantCulture) } catch { }
            }
            $ver = ''; if ($p.PSObject.Properties['DriverVersion']) { $ver = $p.DriverVersion }
            $list += New-Object PSObject -Property @{ Name = $p.DriverDesc.Trim(); VramMb = [int]($vram / 1MB); Driver = $ver; DriverDate = $date; Integrated = (Test-IntegratedGpu $p.DriverDesc) }
        }
    } catch { }
    if ($list.Count -eq 0) {
        try {
            foreach ($v in @(Get-CimInstance Win32_VideoController -ErrorAction Stop | Where-Object { $_.Name -notmatch $virtual })) {
                $list += New-Object PSObject -Property @{ Name = $v.Name; VramMb = [int]([double]$v.AdapterRAM / 1MB); Driver = $v.DriverVersion; DriverDate = $v.DriverDate; Integrated = (Test-IntegratedGpu $v.Name) }
            }
        } catch { }
    }
    return $list
}

function Test-IntegratedGpu {
    # the same rule the simulator uses (HardwareProfile.LooksIntegrated)
    param([string]$Name)
    $n = $Name.ToLowerInvariant()
    if ($n.Contains('intel') -and -not $n.Contains('arc')) { return $true }
    foreach ($s in @('radeon(tm) graphics', 'radeon graphics', 'vega 8', 'vega 11', '780m', '760m', '890m', '880m')) { if ($n.Contains($s)) { return $true } }
    return $false
}

function Get-ExpectedDetail {
    # the detail level the simulator will choose on Auto (HardwareProfile.Detect)
    param($Gpu)
    if (-not $Gpu) { return $null }
    $v = $Gpu.VramMb
    if ($Gpu.Integrated) { if ($v -ge 4000) { return 'Medium' } else { return 'Low' } }
    if ($v -ge 12000) { return 'Ultra' }
    if ($v -ge 7000) { return 'High' }
    if ($v -ge 3500) { return 'Medium' }
    return 'Low'
}

function Get-GpuPreference {
    # Windows Settings > Display > Graphics choice for one program: 0 let Windows decide, 1 power saving, 2 high performance
    param([string]$Exe)
    try {
        $p = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\DirectX\UserGpuPreferences' -ErrorAction Stop
        if ($p.PSObject.Properties[$Exe]) {
            $m = [regex]::Match([string]$p.$Exe, 'GpuPreference=(\d)')
            if ($m.Success) { return [int]$m.Groups[1].Value }
        }
    } catch { }
    return $null
}

# ---------------------------------------------------------------- disk and paths

function Get-FreeSpace {
    param([string]$Path)
    try {
        $root = [IO.Path]::GetPathRoot((Resolve-Path -LiteralPath $Path).Path)
        $d = New-Object IO.DriveInfo($root)
        return New-Object PSObject -Property @{ Drive = $root; Free = [double]$d.AvailableFreeSpace; Total = [double]$d.TotalSize; Type = "$($d.DriveType)" }
    } catch { return $null }
}

function Get-LongPathsEnabled {
    try { return ((Get-ItemProperty -Path 'HKLM:\SYSTEM\CurrentControlSet\Control\FileSystem' -ErrorAction Stop).LongPathsEnabled -eq 1) } catch { return $null }
}

function Test-UnderOneDrive {
    param([string]$Path)
    foreach ($o in @($env:OneDrive, $env:OneDriveConsumer, $env:OneDriveCommercial)) {
        if ($o -and $Path.ToLowerInvariant().StartsWith($o.ToLowerInvariant())) { return $true }
    }
    return [bool]($Path -match '\\OneDrive( - [^\\]+)?\\')
}

# ---------------------------------------------------------------- the simulator's own files

function Get-SimSettings {
    $path = Get-SettingsPath
    $t = Read-TextOrNull $path
    $obj = $null
    if ($t) { try { $obj = $t | ConvertFrom-Json } catch { } }
    return New-Object PSObject -Property @{ Path = $path; Exists = [bool]$t; Valid = [bool]$obj; Data = $obj }
}

function Get-LogErrors {
    # compile errors and Bovie Sim errors from a Unity log, newest last
    param([string]$Path, [int]$Max = 15)
    $t = Read-TextOrNull $Path
    if (-not $t) { return @() }
    $lines = $t -split "`r?`n"
    $hits = @($lines | Where-Object { $_ -match 'error CS\d{4}|\[Bovie Sim\].*(fail|could not|error)|Exception:|Aborting batchmode|Scripts have compiler errors' } | Select-Object -Unique)
    return @($hits | Select-Object -Last $Max)
}
