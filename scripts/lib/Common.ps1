# Bovie Sim maintenance: output, prompts, transcript and running other programs.
# Dot-sourced by maintain.ps1. Written for Windows PowerShell 5.1 (the one built into Windows) and later:
# no ?? or ?:, no && or ||, ASCII-only source (symbols are built from [char] codes).

Set-StrictMode -Version 2.0

$script:Run = @{
    Yes       = $false      # --yes: take the default answer at every question
    DryRun    = $false      # --dry-run: show what would be done, change nothing
    Color     = $true
    Unicode   = $true
    Warned    = New-Object System.Collections.ArrayList
    Started   = Get-Date
    Embedded  = $false      # one command running inside another: its last word is one line
    LastState = 'ok'
    Failed    = $false
    LogFile   = $null
    Indent    = ''
    Capture   = $null       # a StringBuilder while output is being collected as text
}

function Initialize-Output {
    param([bool]$NoColor = $false, [bool]$Ascii = $false)
    $redirected = $false
    try { $redirected = [Console]::IsOutputRedirected } catch { }
    $script:Run.Color = -not ($NoColor -or $env:NO_COLOR -or $redirected)
    # The classic console window draws box and tick characters as "?" under most code pages; Windows
    # Terminal, VS Code and other modern terminals do not.
    $modern = [bool]($env:WT_SESSION -or $env:TERM_PROGRAM -or $env:ConEmuANSI -eq 'ON' -or $env:BOVIE_UNICODE)
    if (-not (Test-IsWindows)) { $modern = $true }
    $script:Run.Unicode = $modern -and -not ($Ascii -or $redirected)
    if ($script:Run.Unicode) { try { [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { } }

    $u = $script:Run.Unicode
    $script:S = @{
        Ok    = $(if ($u) { [string][char]0x2714 } else { 'ok' })
        Warn  = $(if ($u) { [string][char]0x26A0 } else { '!!' })
        Bad   = $(if ($u) { [string][char]0x2716 } else { 'XX' })
        Off   = $(if ($u) { [string][char]0x00B7 } else { '--' })
        Todo  = $(if ($u) { [string][char]0x2192 } else { '->' })
        Dot   = $(if ($u) { [string][char]0x2022 } else { '*' })
        H     = $(if ($u) { [string][char]0x2550 } else { '=' })
        TL    = $(if ($u) { [string][char]0x2554 } else { '+' })
        TR    = $(if ($u) { [string][char]0x2557 } else { '+' })
        BL    = $(if ($u) { [string][char]0x255A } else { '+' })
        BR    = $(if ($u) { [string][char]0x255D } else { '+' })
        Box   = $(if ($u) { [string][char]0x25A0 } else { '#' })
        Ell   = $(if ($u) { [string][char]0x2026 } else { '...' })
    }
}

function Test-IsWindows { return [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT }

# ---------------------------------------------------------------- writing

function Write-Out {
    # one piece of a line, coloured when colour is on
    param([string]$Text, [string]$Fg = '', [string]$Bg = '', [switch]$NoNewline)
    if (-not $script:Run.Unicode -and $Text -match '[^\x00-\x7F]') {
        # text from files (changelog, commit subjects) on a console that cannot draw it
        $Text = $Text -replace ([string][char]0x00B7), '-' -replace ('[' + [char]0x2013 + [char]0x2014 + ']'), '-' -replace ([string][char]0x00D7), 'x' -replace '[^\x00-\x7F]', '?'
    }
    if ($null -ne $script:Run.Capture) {
        # collecting plain text for a report instead of drawing on the screen
        [void]$script:Run.Capture.Append($Text)
        if (-not $NoNewline) { [void]$script:Run.Capture.Append([Environment]::NewLine) }
        return
    }
    $p = @{ Object = $Text; NoNewline = [bool]$NoNewline }
    if ($script:Run.Color -and $Fg) { $p.ForegroundColor = $Fg }
    if ($script:Run.Color -and $Bg) { $p.BackgroundColor = $Bg }
    Write-Host @p
}

function Get-UiWidth {
    $w = 80
    try { $w = $Host.UI.RawUI.WindowSize.Width } catch { }
    if (-not $w -or $w -lt 40) { $w = 80 }
    return [Math]::Min(88, [Math]::Max(40, $w - 1))
}

function Split-Wrap {
    # TEXT broken at spaces into lines of at most WIDTH characters
    param([string]$Text, [int]$Width)
    $lines = New-Object System.Collections.ArrayList
    foreach ($para in ($Text -split "`n")) {
        $line = ''
        foreach ($word in ($para -split ' ')) {
            if ($line.Length -eq 0) { $line = $word }
            elseif ($line.Length + 1 + $word.Length -le $Width) { $line = $line + ' ' + $word }
            else { [void]$lines.Add($line); $line = $word }
        }
        [void]$lines.Add($line)
    }
    return $lines
}

function Write-Log {
    param([string]$Line)
    if (-not $script:Run.LogFile) { return }
    try { Add-Content -LiteralPath $script:Run.LogFile -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + '  ' + $Line) -Encoding UTF8 } catch { }
}

function Start-Transcript2 {
    # a plain-text record of every run, kept under 1 MB (the previous one becomes maintain.log.1)
    param([string]$Path, [string]$CommandLine)
    try {
        $dir = Split-Path -Parent $Path
        if (-not (Test-Path -LiteralPath $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
        if ((Test-Path -LiteralPath $Path) -and (Get-Item -LiteralPath $Path).Length -gt 1MB) {
            Move-Item -LiteralPath $Path -Destination ($Path + '.1') -Force
        }
        $script:Run.LogFile = $Path
        Write-Log ('---- maintain ' + $CommandLine)
    } catch { $script:Run.LogFile = $null }
}

function Write-Banner {
    # one line, like the title bar of an old DOS window: what is running, and where on the right
    param([string]$Title, [string]$Detail = '')
    $script:Run.Indent = '  '
    if ($script:Run.Embedded) { Write-Out ''; Write-Out $Title -Fg White; Write-Log "== $Title $Detail"; return }
    $S = $script:S; $width = Get-UiWidth
    $left = $S.TL + $S.H + '[' + $S.Box + ']' + $S.H + ' '
    $base = $left.Length + 'Bovie Sim'.Length + 3 + $Title.Length + 1
    if ($Detail) {
        $room = $width - $base - 4
        if ($Detail.Length -gt $room) { $Detail = $S.Ell + $Detail.Substring($Detail.Length - [Math]::Max(1, $room - $S.Ell.Length)) }
        $right = $Detail.Length + 4
    } else { $right = 2 }
    $fill = [Math]::Max(1, $width - $base - $right)
    Write-Out ''
    Write-Out $left -Fg DarkCyan -NoNewline
    Write-Out 'Bovie Sim' -Fg White -NoNewline
    Write-Out (' ' + $S.H + ' ') -Fg DarkCyan -NoNewline
    Write-Out $Title -Fg Cyan -NoNewline
    Write-Out (' ' + ($S.H * $fill)) -Fg DarkCyan -NoNewline
    if ($Detail) {
        Write-Out (' ' + $Detail + ' ') -Fg DarkGray -NoNewline
        Write-Out ($S.H + $S.TR) -Fg DarkCyan
    } else { Write-Out ($S.H + $S.TR) -Fg DarkCyan }
    Write-Log "== $Title $Detail"
    if ($script:Run.DryRun) {
        Write-Out '  ' -NoNewline
        Write-Out '[ DRY RUN ]' -Fg Black -Bg Yellow -NoNewline
        Write-Out ' Nothing below is done; it only shows what would be.' -Fg White
    }
}

function Write-Section {
    param([string]$Title)
    $script:Run.Indent = '  '
    Write-Out ''
    Write-Out $Title -Fg White
    Write-Log "-- $Title"
}

function Write-Mark {
    param([string]$State)
    switch ($State) {
        'ok'   { Write-Out ($script:S.Ok + ' ') -Fg Green -NoNewline }
        'warn' { Write-Out ($script:S.Warn + ' ') -Fg Yellow -NoNewline }
        'bad'  { Write-Out ($script:S.Bad + ' ') -Fg Red -NoNewline }
        'off'  { Write-Out ($script:S.Off + ' ') -Fg DarkGray -NoNewline }
        default { Write-Out (' ' * ($script:S.Ok.Length + 1)) -NoNewline }
    }
}

function Write-KV {
    # one fact on a line, the labels lined up; a long value wraps under itself
    param([string]$Label, [string]$Value, [string]$State = '')
    $ind = $script:Run.Indent; if (-not $ind) { $ind = '  ' }
    $markW = $script:S.Ok.Length + 1
    $padW = $ind.Length + $markW + 15
    $lines = Split-Wrap $Value ([Math]::Max(20, (Get-UiWidth) - $padW))
    Write-Out $ind -NoNewline
    Write-Mark $State
    Write-Out ($Label.PadRight(14) + ' ') -Fg DarkGray -NoNewline
    $first = $true
    foreach ($l in $lines) {
        if (-not $first) { Write-Out (' ' * $padW) -NoNewline }
        $fg = ''
        if ($State -eq 'bad') { $fg = 'Red' } elseif ($State -eq 'warn') { $fg = 'Yellow' }
        Write-Out $l -Fg $fg
        $first = $false
    }
    Write-Log ("   ${Label}: $Value" + $(if ($State) { " [$State]" } else { '' }))
    if ($State -eq 'warn') { Add-Warning "${Label}: $Value" -Quiet }
    if ($State -eq 'bad') { $script:Run.Failed = $true }
}

function Write-Line {
    # a mark and a sentence, wrapped under itself
    param([string]$State, [string]$Text, [string]$Fg = '')
    $ind = $script:Run.Indent
    $markW = $script:S.Ok.Length + 1
    $lines = Split-Wrap $Text ([Math]::Max(20, (Get-UiWidth) - $ind.Length - $markW))
    $first = $true
    foreach ($l in $lines) {
        Write-Out $ind -NoNewline
        if ($first) { Write-Mark $State } else { Write-Out (' ' * $markW) -NoNewline }
        Write-Out $l -Fg $Fg
        $first = $false
    }
}

function Write-Ok { param([string]$Text) Write-Line 'ok' $Text; Write-Log "ok: $Text" }
function Write-Bad { param([string]$Text) Write-Line 'bad' $Text 'Red'; Write-Log "FAIL: $Text"; $script:Run.Failed = $true }
function Write-Warn {
    param([string]$Text)
    Write-Line 'warn' $Text 'Yellow'
    Add-Warning $Text -Quiet
}
function Add-Warning {
    param([string]$Text, [switch]$Quiet)
    [void]$script:Run.Warned.Add($Text)
    Write-Log "warn: $Text"
}
function Write-Note {
    # a quieter line: the reason behind something, not the thing itself
    param([string]$Text)
    $ind = $script:Run.Indent + (' ' * ($script:S.Ok.Length + 1))
    foreach ($l in (Split-Wrap $Text ([Math]::Max(20, (Get-UiWidth) - $ind.Length)))) { Write-Out ($ind + $l) -Fg DarkGray }
    Write-Log "   $Text"
}
function Write-Para {
    param([string]$Text)
    $ind = $script:Run.Indent; if (-not $ind) { $ind = '  ' }
    foreach ($l in (Split-Wrap $Text ([Math]::Max(20, (Get-UiWidth) - $ind.Length)))) { Write-Out ($ind + $l) }
    Write-Log "   $Text"
}

function Write-After {
    # what comes next, or what to do if it went wrong
    param([string]$Heading)
    $script:Run.Indent = '  '
    Write-Out ''
    Write-Out $Heading -Fg White
    Write-Log "== $Heading"
}

function Write-Todo {
    # a thing left for the person; a command they would type is cyan, its "# comment" dim
    param([string]$Text)
    $m = [regex]::Match($Text, '^(?<cmd>(maintain|install|git|cd|winget|powershell|Start-Process)\b[^#]*?)(?<rest>\s+#\s.*)?$')
    Write-Out ('  ' + $script:S.Todo + ' ') -Fg DarkGray -NoNewline
    if ($m.Success) {
        Write-Out $m.Groups['cmd'].Value -Fg Cyan -NoNewline
        Write-Out $m.Groups['rest'].Value -Fg DarkGray
    } else {
        $ind = '  ' + (' ' * ($script:S.Todo.Length + 1))
        $first = $true
        foreach ($l in (Split-Wrap $Text ([Math]::Max(20, (Get-UiWidth) - $ind.Length)))) {
            if ($first) { Write-Out $l } else { Write-Out ($ind + $l) }
            $first = $false
        }
    }
    Write-Log "todo: $Text"
}

function Format-Duration {
    param([double]$Seconds)
    $s = [int][Math]::Round($Seconds)
    if ($s -lt 60) { return "${s}s" }
    if ($s -lt 3600) { return ('{0}m {1:00}s' -f [int][Math]::Floor($s / 60), ($s % 60)) }
    return ('{0}h {1:00}m' -f [int][Math]::Floor($s / 3600), [int][Math]::Floor(($s % 3600) / 60))
}

function Format-Ago {
    param([datetime]$When)
    $n = ((Get-Date) - $When).TotalSeconds
    if ($n -lt 90) { return 'just now' }
    if ($n -lt 5400) { return ('{0} minutes ago' -f [int][Math]::Round($n / 60)) }
    if ($n -lt 129600) { return ('{0} hours ago' -f [int][Math]::Round($n / 3600)) }
    return ('{0} days ago' -f [int][Math]::Round($n / 86400))
}

function Format-Size {
    param([double]$Bytes)
    if ($Bytes -ge 1GB) { return ('{0:0.0} GB' -f ($Bytes / 1GB)) }
    if ($Bytes -ge 1MB) { return ('{0:0} MB' -f ($Bytes / 1MB)) }
    return ('{0:0} KB' -f ($Bytes / 1KB))
}

function Write-Recap {
    # every warning this run said, once more, so none is lost in the scroll
    if ($script:Run.Embedded -or $script:Run.Warned.Count -eq 0) { return }
    Write-Out ''
    Write-Out 'Warnings' -Fg White
    $seen = @{}
    foreach ($w in $script:Run.Warned) {
        if ($seen.ContainsKey($w)) { continue }
        $seen[$w] = $true
        $first = $true
        foreach ($l in (Split-Wrap $w ((Get-UiWidth) - 4))) {
            if ($first) { Write-Out ('  ' + $script:S.Warn + ' ') -Fg Yellow -NoNewline; Write-Out $l }
            else { Write-Out ('    ' + $l) }
            $first = $false
        }
    }
}

function Complete-Run {
    # the last word: how it went, with the time it took. Returns the exit code (1 on FAIL).
    param([string]$State, [string]$Headline)
    if ($script:Run.Failed -and $State -ne 'bad') { $State = 'bad' }
    if ($script:Run.DryRun -and $State -ne 'bad') { $State = 'ok'; $Headline = 'Dry run finished: nothing was changed' }
    $count = $script:Run.Warned.Count
    if ($State -eq 'ok' -and $count -gt 0 -and -not $script:Run.DryRun) { $State = 'warn' }
    if ($State -eq 'warn' -and $count -gt 0) { $Headline = $Headline + ', with ' + $count + ' warning' + $(if ($count -eq 1) { '' } else { 's' }) }
    $script:Run.LastState = $State
    Write-Log "== result: ${State}: $Headline"
    $code = 0; if ($State -eq 'bad') { $code = 1 }
    if ($script:Run.Embedded) {
        Write-Line $State $Headline
        return $code
    }
    Write-Recap
    $S = $script:S
    $fg = 'Green'; if ($State -eq 'warn') { $fg = 'Yellow' } elseif ($State -eq 'bad') { $fg = 'Red' }
    Write-Out ''
    Write-Out ($S.BL + ($S.H * ((Get-UiWidth) - 2)) + $S.BR) -Fg DarkCyan
    $badge = ' OK '; $bg = 'DarkGreen'
    if ($State -eq 'warn') { $badge = 'WARN'; $bg = 'DarkYellow' } elseif ($State -eq 'bad') { $badge = 'FAIL'; $bg = 'DarkRed' }
    if ($script:Run.Color) { Write-Out (' ' + $badge + ' ') -Fg White -Bg $bg -NoNewline } else { Write-Out ('[' + $badge + ']') -NoNewline }
    Write-Out (' ' + $Headline) -Fg White -NoNewline
    $took = ((Get-Date) - $script:Run.Started).TotalSeconds
    if ($took -ge 3) { Write-Out ('  (' + (Format-Duration $took) + ')') -Fg DarkGray } else { Write-Out '' }
    return $code
}

function Stop-Run {
    # an error the run cannot go on from: say it, say what to do, and leave with FAIL
    param([string]$Text, [string[]]$Todo = @())
    Write-Bad $Text
    if ($Todo.Count -gt 0) { Write-After 'What to do'; foreach ($t in $Todo) { Write-Todo $t } }
    $code = Complete-Run 'bad' 'Stopped'
    throw ('BOVIE_STOP:' + $code)   # caught by the dispatcher in maintain.ps1, which exits with the code
}

# ---------------------------------------------------------------- asking

function Confirm-Step {
    # a yes/no question; Enter takes the default. --yes takes the default without asking; a dry run says no.
    param([string]$Question, [bool]$Default = $true)
    $hint = $(if ($Default) { '[Y/n]' } else { '[y/N]' })
    if ($script:Run.DryRun) { Write-Note "(dry run) would ask: $Question $hint"; return $false }
    if ($script:Run.Yes) { Write-Note "$Question $hint  (--yes: $(if ($Default) { 'yes' } else { 'no' }))"; return $Default }
    $interactive = $true
    try { if ([Console]::IsInputRedirected) { $interactive = $false } } catch { }
    if (-not $interactive) { Write-Note "$Question $hint  (no keyboard: $(if ($Default) { 'yes' } else { 'no' }))"; return $Default }
    while ($true) {
        Write-Out ('  ' + $Question + ' ') -Fg White -NoNewline
        Write-Out ($hint + ' ') -Fg DarkGray -NoNewline
        $a = Read-Host
        Write-Log "ask: $Question -> '$a'"
        if ($null -eq $a -or $a.Trim() -eq '') { return $Default }
        switch -regex ($a.Trim()) {
            '^(y|yes)$' { return $true }
            '^(n|no)$' { return $false }
        }
    }
}

function Read-Choice {
    # a numbered menu; returns the chosen key, or '' for Enter / q
    param([string]$Title, [object[]]$Items)  # Items: @(key, label) pairs
    Write-Section $Title
    for ($i = 0; $i -lt $Items.Count; $i++) {
        Write-Out ('   ' + ($i + 1).ToString().PadLeft(2) + '  ') -Fg Cyan -NoNewline
        Write-Out $Items[$i][1]
    }
    Write-Out '    q  Quit' -Fg DarkGray
    $a = Read-Host '  Choose'
    if ($null -eq $a) { return '' }
    $a = $a.Trim()
    $n = 0
    if ([int]::TryParse($a, [ref]$n) -and $n -ge 1 -and $n -le $Items.Count) { return $Items[$n - 1][0] }
    return ''
}

# ---------------------------------------------------------------- running programs

function Invoke-Native {
    # run a program, capture stdout and stderr together; never throws for a non-zero exit
    # (Windows PowerShell 5.1 turns native stderr into errors under -ErrorAction Stop)
    param([string]$Exe, [string[]]$Arguments = @(), [string]$WorkingDirectory = '')
    $old = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $pushed = $false
    $enc = $null
    # git writes UTF-8; Windows PowerShell decodes program output with the console's code page
    try { $enc = [Console]::OutputEncoding; [Console]::OutputEncoding = New-Object System.Text.UTF8Encoding($false) } catch { $enc = $null }
    try {
        if ($WorkingDirectory) { Push-Location -LiteralPath $WorkingDirectory; $pushed = $true }
        $out = & $Exe @Arguments 2>&1 | ForEach-Object { "$_" }
        $code = $LASTEXITCODE
    } catch {
        $out = @("$_"); $code = 127
    } finally {
        if ($pushed) { Pop-Location }
        if ($enc) { try { [Console]::OutputEncoding = $enc } catch { } }
        $ErrorActionPreference = $old
    }
    if ($null -eq $out) { $out = @() }
    return New-Object PSObject -Property @{ Code = $code; Out = @($out); Text = (@($out) -join "`n").Trim() }
}

function Invoke-Change {
    # a step that changes something: shown and skipped in a dry run; a tick with its time, or a cross with its output
    # (internal names start with __ so the action's own variables from the caller are never hidden)
    param([string]$What, [scriptblock]$Action)
    if ($script:Run.DryRun) { Write-Line 'off' ("would: " + $What) 'DarkGray'; Write-Log "dry-run: $What"; return $true }
    $__t0 = Get-Date
    $__detail = ''
    try {
        $__r = & $Action
        if ($__r -is [array]) { $__r = $__r[-1] }
        $__ok = $true
        if ($__r -is [psobject] -and $__r.PSObject.Properties['Code']) {
            $__ok = ($__r.Code -eq 0)
            if (-not $__ok) { $__detail = $__r.Text }
        } elseif ($__r -is [bool]) { $__ok = $__r }
    } catch { $__ok = $false; $__detail = "$_" }
    $__took = ((Get-Date) - $__t0).TotalSeconds
    if ($__ok) {
        $__text = $What; if ($__took -ge 2) { $__text = $__text + '  (' + (Format-Duration $__took) + ')' }
        Write-Ok $__text
    } else {
        Write-Bad "$What failed"
        if ($__detail) { foreach ($__l in ($__detail -split "`n" | Select-Object -Last 12)) { Write-Note $__l } }
    }
    return $__ok
}
