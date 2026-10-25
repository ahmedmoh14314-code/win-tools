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
#       [-IdleSeconds 20]                    how long nobody may have touched the mouse or keyboard before injecting
#       [-MaxWaitMinutes 30]                 give up when the desktop stays in use this long
#       [-RowMinutes 15]                     a row that runs longer is stopped and every key and button released
#
# Start it and leave the PC: it waits until the mouse and keyboard have been idle for -IdleSeconds before the first
# row, and pauses the same way whenever someone uses the PC between rows. Idle is checked between rows only: someone who
# touches the mouse during a row can still disturb that row (and the harness's injected input), so leave the PC alone.
#
# Not automated here, by design: restarting Explorer (it is the user's shell), sign-in with "Start with Windows", and
# rubber-band selection (UI Automation cannot drive it reliably). Those are manual protocol items in
# docs/testing/release-matrix.md.
param(
    [string]$Harness = (Join-Path $PSScriptRoot 'bin\Release\net10.0-windows\win-x64\WinTools.EndToEnd.exe'),
    [string]$HostPath = '',
    [string]$Results = (Join-Path $env:TEMP ('wintools-e2e\matrix-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))),
    [string[]]$Only = @(),
    [int]$IdleSeconds = 20,
    [int]$MaxWaitMinutes = 30,
    [int]$RowMinutes = 15
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Harness)) { throw "Harness not found: $Harness (build tests\WinTools.EndToEnd -c Release first)" }
# Full paths: the hung-row cleanup finds the host by its path, which a relative -Harness or -HostPath would never match.
$Harness = (Resolve-Path $Harness).Path
if ($HostPath) { $HostPath = (Resolve-Path $HostPath).Path }
New-Item -ItemType Directory -Force -Path $Results | Out-Null

Add-Type @'
using System; using System.Runtime.InteropServices;
public static class MatrixIdle {
  [StructLayout(LayoutKind.Sequential)] struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }
  [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO p);
  public static double Seconds() { var i = new LASTINPUTINFO(); i.cbSize = 8; GetLastInputInfo(ref i); return ((uint)Environment.TickCount - i.dwTime) / 1000.0; }
  [StructLayout(LayoutKind.Sequential)] struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr extra; }
  [StructLayout(LayoutKind.Sequential)] struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr extra; }
  [StructLayout(LayoutKind.Explicit)] struct UNION { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
  [StructLayout(LayoutKind.Sequential)] struct INPUT { public uint type; public UNION u; }
  [DllImport("user32.dll")] static extern uint SendInput(uint n, INPUT[] inputs, int size);
  static INPUT Key(ushort vk, uint flags) { var k = new INPUT(); k.type = 1; k.u.ki.wVk = vk; k.u.ki.dwFlags = flags; return k; }
  /// Releases the keys and the button a killed harness may have left down, in the harness's own order: modifiers up,
  /// Escape tapped BEFORE the button goes up, so a drag still in Explorer's loop is cancelled, never dropped (evaluator,
  /// PR #14). Each input is sent on its own; returns how many SendInput refused (secure desktop, lock screen).
  public static int ReleaseAll() {
    var inputs = new INPUT[] { Key(0x11, 2), Key(0x10, 2), Key(0x12, 2), Key(0x1B, 0), Key(0x1B, 2), Key(0x20, 2), new INPUT() };
    inputs[6].type = 0; inputs[6].u.mi.dwFlags = 4; // left button up
    var refused = 0;
    foreach (var input in inputs) {
      if (SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT))) != 1) refused++;
      System.Threading.Thread.Sleep(40);
    }
    return refused;
  }
}
'@

# name, harness arguments, what the row proves. Order matters: the first rows run on a fresh host and a cold worker.
$rows = @(
    @('first-use-png',        'png --cycles 3',                                                   'first operation after a fresh host (cycle 1), then warm'),
    @('photo-compress',       'photo --cycles 2',                                                 'compress a real-size photo: valid copy, saves >= 8 %'),
    @('photo-compress-high',  'photo --op compress.high --expect already-optimized --cycles 1',    'High on a photo already near its quality: nothing written, says so'),
    @('ctrl-a-2',             'batch --batch 2 --select ctrl-a --settle-ms 0 --cycles 2',         '2 files, Ctrl+A, drag at once'),
    @('ctrl-a-10',            'batch --batch 10 --select ctrl-a --settle-ms 0 --cycles 2',        '10 files, Ctrl+A, drag at once'),
    @('ctrl-a-100',           'batch --batch 100 --select ctrl-a --settle-ms 0 --cycles 1',       '100 files, Ctrl+A, drag at once'),
    @('ctrl-a-held-10',       'batch --batch 10 --select ctrl-a --hold ctrl --cycles 1',          'Ctrl kept down through drag, trigger and drop'),
    @('ctrl-click-held-2',    'batch --batch 2 --select ctrl-click --hold ctrl --cycles 2',       'Ctrl-click 2, Ctrl held (owner report)'),
    @('ctrl-click-held-10',   'batch --batch 10 --select ctrl-click --hold ctrl --cycles 1',      'Ctrl-click 10, Ctrl held'),
    @('ctrl-click-released',  'batch --batch 10 --select ctrl-click --settle-ms 0 --cycles 1',    'Ctrl-click 10, released, drag at once'),
    @('shift-range-held-10',  'batch --batch 10 --select shift-range --hold shift --cycles 2',    'Shift-click range 10, Shift held'),
    @('shift-range-10',       'batch --batch 10 --select shift-range --settle-ms 0 --cycles 1',   'Shift-click range 10, released, drag at once'),
    @('shift-range-100',      'batch --batch 100 --select shift-range --settle-ms 0 --cycles 1',  'range of 100: click, scroll with the wheel, Shift-click'),
    @('alt-refused',          'alt --cycles 2',                                                   'Alt held: refused with a toast, nothing runs'),
    @('escape',               'escape --cycles 2',                                                'Esc cancels'),
    @('outside',              'outside --cycles 2',                                               'release outside the ring runs nothing'),
    @('fast',                 'fast --cycles 3',                                                  'fast releases follow the arm rule'),
    @('category',             'category --cycles 2',                                              'a drop on a category runs nothing'),
    @('edge',                 'edge --cycles 1',                                                  'trigger in a screen corner'),
    @('unicode-png',          'png --path-style unicode --cycles 2',                              'Arabic, emoji and spaces in folder and file names'),
    @('unicode-batch-10',     'batch --batch 10 --path-style unicode --settle-ms 0 --cycles 1',   'Unicode names, 10 files'),
    @('long-png',             'png --path-style long --cycles 2',                                 'source path longer than 260 characters'),
    @('long-batch-10',        'batch --batch 10 --path-style long --cycles 1',                    'long paths, 10 files'),
    @('general-file-info',    'file --cycles 2',                                                  'a file no module reads: File info under Tools, nothing written (V2 PR 1.4)'),
    @('general-rename',       'rename --cycles 2',                                                'Batch rename: "2026 " typed into the dialog; the file renamed, its content kept (V2 PR 7.2)'),
    @('general-sha256',       'file --op files.sha256 --cycles 1',                                'SHA-256 of a file: the info panel, nothing written'),
    @('folder-info',          'folder --cycles 2',                                                'a dropped folder: File info, its contents unchanged (UX-009)'),
    @('pdf-rotate',           'pdf --cycles 2',                                                   'PDF rotate right: a new PDF, the original unchanged (V2 PR 2.1)'),
    @('pdf-info',             'pdf --op pdf.info --cycles 1',                                     'PDF info: the info panel, nothing written'),
    @('pdf-protect',          'pdf --op pdf.protect --cycles 1',                                  'Protect: the password typed twice in the dialog; the copy needs it (V2 PR 2.2)'),
    @('pdf-unprotect',        'pdf --op pdf.unprotect --cycles 1',                                'Remove protection with the right password: the copy is not encrypted'),
    @('zip-extract',          'zip --cycles 2',                                                   'ZIP extract: a new folder with exactly the archive''s tree (V2 PR 5.1)'),
    @('zip-test',             'zip --op archive.test --cycles 1',                                 'ZIP test: the info panel, nothing written'),
    @('tgz-extract',          'tgz --cycles 2',                                                   'TAR.GZ extract: the same tree from a PAX TAR.GZ (V2 PR 5.2)'),
    @('zip-locked-extract',   'lockedzip --cycles 1',                                             'encrypted ZIP: the dialog after the first run, an Arabic password typed, the tree (V2 PR 5.3)'),
    @('zip-locked-wrong',     'lockedzip --password wrong --cycles 1',                            'encrypted ZIP, a wrong password: no folder, the toast'),
    @('srt-to-vtt',           'srt --cycles 2',                                                   'Arabic SRT with an emoji to WebVTT (V2 PR 7.1)'),
    @('srt-shift',            'srt --op subtitles.shift-plus-1000 --cycles 1',                    'subtitle timing 1 s later: a new SRT'),
    @('docx-text',            'docx --cycles 2',                                                  'Word text: a new UTF-8 text file (V2 PR 6.1)'),
    @('docx-info',            'docx --op documents.info --cycles 1',                              'document info with Office-shaped properties: the info panel'),
    @('docx-clean',           'docx --op documents.clean --cycles 1',                             'Remove properties: a copy without author or title (V2 PR 6.2)')
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
# The build under test: the host the rows use (next to the harness unless -HostPath), by version and the content hash of
# each product assembly, so a stale worker or core next to a fresh host shows up too.
$testedHost = if ($HostPath) { $HostPath } else { Join-Path (Split-Path $Harness) 'WinTools.Host.exe' }
$hostDir = Split-Path $testedHost
$hostDll = Join-Path $hostDir 'WinTools.Host.dll'
$identity = if (Test-Path $hostDll) {
    $hashes = foreach ($dll in 'WinTools.Host.dll', 'WinTools.Core.dll', 'WinTools.Worker.dll', 'WinTools.Windows.dll') {
        $path = Join-Path $hostDir $dll
        if (Test-Path $path) { '{0} {1}' -f $dll, (Get-FileHash $path -Algorithm SHA256).Hash.Substring(0, 12) } else { "$dll missing" }
    }
    '{0}, SHA-256 {1}' -f (Get-Item $hostDll).VersionInfo.ProductVersion, ($hashes -join ', ')
} else { 'host not found' }
$report.Add(('Run {0} on {1}, harness `{2}`{3}.' -f (Get-Date -Format 's'), $env:COMPUTERNAME, $Harness, $(if ($HostPath) { ", installed host ``$HostPath``" } else { '' })))
$report.Add("Build under test: $identity.")
$report.Add('')
$report.Add('| Row | Proves | Cycles passed | Exit | Keys left down |')
$report.Add('|---|---|---|---|---|')
# Waits until nobody has touched the mouse or keyboard for $seconds; false when that does not happen in $MaxWaitMinutes.
function Wait-Idle([double]$seconds) {
    $deadline = (Get-Date).AddMinutes($MaxWaitMinutes)
    $told = $false
    while ([MatrixIdle]::Seconds() -lt $seconds) {
        if ((Get-Date) -gt $deadline) { return $false }
        if (-not $told) { Write-Host "Waiting until the mouse and keyboard have been idle for $seconds s..."; $told = $true }
        Start-Sleep -Milliseconds 500
    }
    return $true
}

$failed = 0
$first = $true
$stopped = $null
foreach ($row in $rows) {
    $name, $arguments, $proves = $row
    if ($stopped) { $report.Add("| $name | $proves | not run: $stopped | - | - |"); $failed++; continue }
    # The harness's own injected input counts as input too. Before the first row the desktop must have been idle for
    # $IdleSeconds. Between rows there is a 3 s pause; input in its last 2.5 s is someone using the PC, and the run then
    # waits until they have left it alone for $IdleSeconds again.
    if (-not $first) { Start-Sleep -Seconds 3 }
    $ready = if ($first -or [MatrixIdle]::Seconds() -lt 2.5) { Wait-Idle $IdleSeconds } else { $true }
    $first = $false
    if (-not $ready) {
        $stopped = "the PC stayed in use for $MaxWaitMinutes min"
        $report.Add("| $name | $proves | not run: $stopped | - | - |"); $failed++
        Write-Warning "Stopping: $stopped."
        continue
    }
    $out = Join-Path $Results "$name.txt"
    $jsonl = Join-Path $Results "$name.jsonl"
    $p = Start-Process -FilePath $Harness -ArgumentList "$arguments --results `"$jsonl`"" -WorkingDirectory (Split-Path $Harness) -NoNewWindow -PassThru -RedirectStandardOutput $out
    $null = $p.Handle # without -Wait, PowerShell keeps the exit code only if the handle was taken while the process ran
    if (-not $p.WaitForExit($RowMinutes * 60 * 1000)) {
        # A hung row may hold a synthetic drag: stop it and release every key and button it could have left down.
        Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
        Get-Process WinTools.Host -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $testedHost } | Stop-Process -Force -ErrorAction SilentlyContinue
        $refused = [MatrixIdle]::ReleaseAll()
        $stopped = "row $name hung for $RowMinutes min and was stopped"
        $released = if ($refused -eq 0) { 'released' } else { "CHECK: $refused release input(s) refused" }
        # The harness's own Explorer window stays open (and topmost) because its cleanup never ran: close it by hand.
        $report.Add("| $name | $proves | hung, stopped; close its Explorer window | - | $released |"); $failed++
        Write-Warning "Stopping: $stopped."
        continue
    }
    $summary = Get-Content $out -Encoding UTF8 | Where-Object { $_ -match '"summary":true' } | Select-Object -Last 1
    $s = if ($summary) { $summary | ConvertFrom-Json } else { $null }
    # A harness that refused to start (swapped buttons 6, bad arguments 64, host or Explorer failed to start 3) injected
    # nothing and writes no summary: that row failed, but no key can be down.
    $refusedToRun = -not $s -and @(3, 6, 64) -contains $p.ExitCode
    $keys = if ($refusedToRun) { 'none (refused to run)' }
        elseif ($s -and -not $s.leftDownAtEnd -and -not $s.spaceDownAtEnd -and -not $s.modifiersDownAtEnd) { 'none' } else { 'CHECK' }
    $cyclesText = if ($s) { "$($s.passed)/$($s.cycles)" } else { 'no summary' }
    $keysUp = $keys -like 'none*'
    if ($p.ExitCode -ne 0 -or -not $s -or $s.passed -ne $s.cycles -or -not $keysUp) { $failed++ }
    $report.Add("| $name | $proves | $cyclesText | $($p.ExitCode) | $keys |")
    Write-Host ("{0,-22} {1,-8} exit {2}" -f $name, $cyclesText, $p.ExitCode)
    if (-not $keysUp) {
        $refused = [MatrixIdle]::ReleaseAll()
        $stopped = "a key or button may have been down after $name"
        Write-Warning "Stopping: $stopped (released, $refused input(s) refused)."
    }
}
$report.Add('')
$report.Add($(if ($failed -eq 0) { '**All rows passed.**' } else { "**$failed row(s) failed or did not run.** Per-row output and JSONL results: ``$Results``." }))
$reportPath = Join-Path $Results 'release-matrix.md'
$report | Set-Content -Path $reportPath -Encoding UTF8
Write-Host "Report: $reportPath"
exit $failed
