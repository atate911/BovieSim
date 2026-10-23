# Checks the maintenance scripts the way CI runs them: they parse, stay ASCII and Windows PowerShell 5.1
# compatible, and every read-only or dry-run command runs to its verdict without an unexpected error.
#   powershell -NoProfile -File scripts\tests\check-scripts.ps1      (Windows PowerShell 5.1)
#   pwsh -NoProfile -File scripts/tests/check-scripts.ps1            (PowerShell 7, any OS)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$files = @(Get-ChildItem -Path (Join-Path $root 'scripts') -Filter '*.ps1' -Recurse | ForEach-Object { $_.FullName })
$bad = 0
function Fail { param([string]$T) Write-Host "FAIL $T"; $script:bad++ }

foreach ($f in $files) {
    $tokens = $null; $errors = $null
    $ast = [System.Management.Automation.Language.Parser]::ParseFile($f, [ref]$tokens, [ref]$errors)
    foreach ($e in $errors) { Fail ("${f}:" + $e.Extent.StartLineNumber + ': ' + $e.Message) }
    $bytes = [IO.File]::ReadAllBytes($f)
    for ($i = 0; $i -lt $bytes.Length; $i++) { if ($bytes[$i] -gt 127) { Fail "${f}: non-ASCII byte at offset $i (Windows PowerShell 5.1 misreads UTF-8 without a BOM)"; break } }
    foreach ($t in $tokens) {
        if (@('QuestionQuestion', 'QuestionQuestionEquals', 'QuestionDot', 'QuestionLBracket', 'AndAnd', 'OrOr') -contains "$($t.Kind)") { Fail ("${f}:" + $t.Extent.StartLineNumber + ": PowerShell 7-only operator $($t.Text)") }
    }
    foreach ($n in $ast.FindAll({ param($x) @('TernaryExpressionAst', 'PipelineChainAst') -contains $x.GetType().Name }, $true)) {
        Fail ("${f}:" + $n.Extent.StartLineNumber + ': PowerShell 7-only ' + $n.GetType().Name)
    }
}
Write-Host ("parsed {0} script(s)" -f $files.Count)

$exe = (Get-Process -Id $PID).Path
$maintain = Join-Path $root 'scripts\maintain.ps1'
$env:NO_COLOR = '1'
$runs = @(
    @(@('help'), @(0)), @(@('status'), @(0, 1)), @(@('check', '--all'), @(0, 1)), @(@('logs'), @(0)), @(@('rollback', 'list'), @(0)),
    @(@('update', '--dry-run'), @(0, 1)), @(@('doctor', '--dry-run'), @(0, 1)), @(@('clean', '--dry-run'), @(0)),
    @(@('build', '--dry-run'), @(0, 1)), @(@('bogus'), @(2))
)
foreach ($r in $runs) {
    $cmdArgs = $r[0]; $okCodes = $r[1]
    $out = & $exe -NoProfile -ExecutionPolicy Bypass -File $maintain @cmdArgs 2>&1 | ForEach-Object { "$_" }
    $code = $LASTEXITCODE
    $text = ($out -join "`n")
    $label = 'maintain ' + ($cmdArgs -join ' ')
    if ($text -match 'Unexpected error') { Fail "${label}: unexpected error`n$text" }
    elseif ($okCodes -notcontains $code) { Fail "${label}: exit $code`n$text" }
    elseif ($text -notmatch '\[( OK |WARN|FAIL)\]|Unknown command|Where things are') { Fail "${label}: no verdict line`n$text" }
    else { Write-Host "ok   $label (exit $code)" }
}
if ($bad -gt 0) { Write-Host "$bad problem(s)"; exit 1 }
Write-Host 'all script checks passed'
