# Release regression matrix (V2 plan §11, PR 0.4): runs the Explorer harness scenarios one after another and writes a
# Markdown report. It drives the real mouse and keyboard: run it only on an idle, unlocked desktop. Before each scenario
# it checks that nobody has touched the mouse or keyboard for a while and stops otherwise (the harness also refuses to
# inject when its window is covered or loses the foreground).
#
#   powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\WinTools.EndToEnd\Run-ReleaseMatrix.ps1
#       [-Harness <WinTools.EndToEnd.exe>]   default: the Release build next to this script
#       [-HostPath <WinTools.Host.exe>]      run the installed-artifact subset against this host (e.g. the installed copy)
#       [-Results <folder>]                  default: %TEMP%\wintools-e2e\matrix-<time>
#       [-Only <name,name>]                  run only these rows (names as in the report)
#
# Not automated here, by design: restarting Explorer (it is the user's shell), sign-in with "Start with Windows", and
# rubber-band selection (UI Automation cannot drive it reliably). Those are manual protocol items in
# docs/testing/release-matrix.md.
param(
    [string]$Harness = (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\win-x64\WinTools.EndToEnd.exe'),
    [string]$HostPath = '',
    [string]$Results = (Join-Path $env:TEMP ('wintools-e2e\matrix-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string[]]$Only = @(),
    [int]$IdleSeconds = 20
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Harness)) { throw "Harness not found: $Harness (build tests\WinTools.EndToEnd -c Release first)" }
New-Item -ItemType Directory -Force -Path $Results | Out-Null

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class MatrixIdle {
  [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
  public static double Seconds() { var i = new LASTINPUTINFO(); i.cbSize = 8; GetLastInputInfo(ref i); return ((uint)Environment.TickCount - i.dwTime) / 1000.0; }
}
'@

# name, harness arguments, what the row proves. Order matters: the first rows run on a fresh host and a cold worker.
$rows = @(
    @('first-use-png',        'png --cycles 3',                                                   'first operation after a fresh host (cycle 1), then warm'),
    @('photo-compress',       'photo --cycles 2',                                                 'compress a real-size photo: valid copy, saves >= 8 %'),
    @('photo-compress-high',  'photo --op compress.high --cycles 1',                              'High on a phone-like photo'),
    @('ctrl-a-2',             'batch --batch 2 --select ctrl-a --settle-ms 0 --cycles 2',         '2 files, Ctrl+A, drag at once'),
    @('ctrl-a-10',            'batch --batch 10 --select ctrl-a --settle-ms 0 --cycles 2',        '10 files, Ctrl+A, drag at once'),
    @('ctrl-a-100',           'batch --batch 100 --select ctrl-a --settle-ms 0 --cycles 1',       '100 files, Ctrl+A, drag at once'),
    @('ctrl-a-held-10',       'batch --batch 10 --select ctrl-a --hold ctrl --cycles 1',          'Ctrl kept down through drag, trigger and drop'),
    @('ctrl-click-held-2',    'batch --batch 2 --select ctrl-click --hold ctrl --cycles 2',       'Ctrl-click 2, Ctrl held (owner report)'),
    @('ctrl-click-held-10',   'batch --batch 10 --select ctrl-click --hold ctrl --cycles 1',      'Ctrl-click 10, Ctrl held'),
    @('ctrl-click-released',  'batch --batch 10 --select ctrl-click --settle-ms 0 --cycles 1',    'Ctrl-click 10, released, drag at once'),
    @('shift-range-held-10',  'batch --batch 10 --select shift-range --hold shift --cycles 2',    'Shift-click range 10, Shift held'),
    @('shift-range-10',       'batch --batch 10 --select shift-range --settle-ms 0 --cycles 1',   'Shift-click range 10, released, drag at once'),
    @('shift-range-100',      'batch --batch 100 --select shift-range --settle-ms 0 --cycles 1',  'keyboard range of 100 (Home, Shift+End)'),
    @('alt-refused',          'alt --cycles 2',                                                   'Alt held: refused with a toast, nothing runs'),
    @('escape',               'escape --cycles 2',                                                'Esc cancels'),
    @('outside',              'outside --cycles 2',                                               'release outside the ring runs nothing'),
    @('fast',                 'fast --cycles 3',                                                  'fast releases follow the arm rule'),
    @('category',             'category --cycles 2',                                              'a drop on a category runs nothing'),
    @('edge',                 'edge --cycles 1',                                                  'trigger in a screen corner'),
    @('unicode-png',          'png --path-style unicode --cycles 2',                              'Arabic, emoji and spaces in folder and file names'),
    @('unicode-batch-10',     'batch --batch 10 --path-style unicode --settle-ms 0 --cycles 1',   'Unicode names, 10 files'),
    @('long-png',             'png --path-style long --cycles 2',                                 'source path longer than 260 characters'),
    @('long-batch-10',        'batch --batch 10 --path-style long --cycles 1',                    'long paths, 10 files')
)
if ($HostPath) {
    $hostArgs = '--host "' + $HostPath + '"'
    $rows += @(
        @('installed-first-use',  "png --cycles 2 $hostArgs",                                        'installed artifact: first use'),
        @('installed-photo',      "photo --cycles 1 $hostArgs",                                      'installed artifact: compress'),
        @('installed-ctrl-held',  "batch --batch 3 --select ctrl-click --hold ctrl --cycles 1 $hostArgs", 'installed artifact: Ctrl held'),
        @('installed-alt',        "alt --cycles 1 $hostArgs",                                        'installed artifact: Alt refused')
    )
}
if ($Only.Count -gt 0) {
    # powershell.exe -File passes "-Only a,b" as one string: accept commas, and refuse unknown names, so a typo can never
    # turn into an empty run that reports success.
    $Only = @($Only | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim() } | Where-Object { $_ })
    $unknown = @($Only | Where-Object { $n = $_; -not ($rows | Where-Object { $_[0] -eq $n }) })
    if ($unknown.Count -gt 0) { throw "Unknown row(s): $($unknown -join ', ')" }
    $rows = @($rows | Where-Object { $Only -contains $_[0] })
}
if ($rows.Count -eq 0) { throw 'No rows to run.' }

$report = New-Object System.Collections.Generic.List[string]
$report.Add('# Release regression matrix')
$report.Add('')
$report.Add(('Run {0} on {1}, harness `{2}`{3}.' -f (Get-Date -Format 's'), $env:COMPUTERNAME, $Harness, $(if ($HostPath) { ", installed host ``$HostPath``" } else { '' })))
$report.Add('')
$report.Add('| Row | Proves | Cycles passed | Exit | Keys left down |')
$report.Add('|---|---|---|---|---|')
$failed = 0
$first = $true
foreach ($row in $rows) {
    $name, $arguments, $proves = $row
    # The harness's own injected input counts as input too: before the first row the desktop must have been idle for
    # $IdleSeconds; between rows, nobody may touch it during a 3 s pause.
    if (-not $first) { Start-Sleep -Seconds 3 }
    $idle = [MatrixIdle]::Seconds()
    $needed = if ($first) { $IdleSeconds } else { 2.5 }
    $first = $false
    if ($idle -lt $needed) {
        $report.Add("| $name | $proves | not run: the desktop was in use ($([Math]::Round($idle, 1)) s since the last input) | - | - |")
        Write-Warning "Stopping: the mouse or keyboard was used $([Math]::Round($idle, 1)) s ago."
        $failed++
        break
    }
    $out = Join-Path $Results "$name.txt"
    $jsonl = Join-Path $Results "$name.jsonl"
    $p = Start-Process -FilePath $Harness -ArgumentList "$arguments --results `"$jsonl`"" -WorkingDirectory (Split-Path $Harness) -NoNewWindow -Wait -PassThru -RedirectStandardOutput $out
    $summary = Get-Content $out -Encoding UTF8 | Where-Object { $_ -match '"summary":true' } | Select-Object -Last 1
    $s = if ($summary) { $summary | ConvertFrom-Json } else { $null }
    $keys = if ($s -and -not $s.leftDownAtEnd -and -not $s.spaceDownAtEnd -and -not $s.modifiersDownAtEnd) { 'none' } else { 'CHECK' }
    $cyclesText = if ($s) { "$($s.passed)/$($s.cycles)" } else { 'no summary' }
    if ($p.ExitCode -ne 0 -or -not $s -or $s.passed -ne $s.cycles -or $keys -ne 'none') { $failed++ }
    $report.Add("| $name | $proves | $cyclesText | $($p.ExitCode) | $keys |")
    Write-Host ("{0,-22} {1,-8} exit {2}" -f $name, $cyclesText, $p.ExitCode)
    if ($keys -ne 'none') { Write-Warning "A key or button may still be down after $name; stopping."; break }
}
$report.Add('')
$report.Add($(if ($failed -eq 0) { '**All rows passed.**' } else { "**$failed row(s) failed or did not run.** Per-row output and JSONL results: ``$Results``." }))
$reportPath = Join-Path $Results 'release-matrix.md'
$report | Set-Content -Path $reportPath -Encoding UTF8
Write-Host "Report: $reportPath"
exit $failed
