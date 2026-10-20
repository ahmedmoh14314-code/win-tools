# Summarises an overlay-host JSONL log (Win32 or WPF host) into the numbers the spike report needs.
# Usage: pwsh ./scripts/Summarize-SpikeLog.ps1 -Path results/host-win32-space.jsonl [-Expected 20]
param(
    [Parameter(Mandatory)] [string] $Path,
    [int] $Expected = 0
)

$events = Get-Content -LiteralPath $Path -Encoding UTF8 | ForEach-Object { $_ | ConvertFrom-Json }

function Stat([double[]] $v) {
    if (-not $v -or $v.Count -eq 0) { return "n/a" }
    $s = $v | Sort-Object
    $med = $s[[math]::Floor(($s.Count - 1) / 2)]
    "n={0} min={1:0.0} med={2:0.0} max={3:0.0}" -f $s.Count, $s[0], $med, $s[-1]
}

function Of([string] $name) { ,@($events | Where-Object { $_.ev -eq $name }) }
$shows   = Of 'overlay.show'
$paints  = Of 'overlay.painted'
$enters  = Of 'drag.enter'
$drops   = Of 'drop'
$hides   = Of 'overlay.hide'
$stats   = Of 'stats'

"file: $Path"
"events: " + (($events | Group-Object ev | Sort-Object Name | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' ')
"hook->overlay shown ms : " + (Stat ($shows  | ForEach-Object { [double]$_.data.hookToShowMs }))
"hook->first paint ms   : " + (Stat ($paints | ForEach-Object { [double]$_.data.hookToPaintMs }))
"hook->DragEnter ms     : " + (Stat ($enters | ForEach-Object { [double]$_.data.hookToEnterMs }))
"hook->Drop ms          : " + (Stat ($drops  | ForEach-Object { [double]$_.data.hookToDropMs }))
"focusStolen any        : " + (@($shows | Where-Object { $_.data.focusStolen }).Count -gt 0)
"clamped shows          : " + @($shows | Where-Object { $_.data.clamped }).Count
"hide reasons           : " + (($hides | Group-Object { $_.data.reason } | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' ')
"drops by sector        : " + (($drops | Group-Object { $_.data.sector } | ForEach-Object { "$($_.Name)=$($_.Count)" }) -join ' ')
"drops by path count    : " + (($drops | Group-Object { $_.data.count } | ForEach-Object { "$($_.Name)files=$($_.Count)" }) -join ' ')
if ($drops.Count -gt 0) { "last drop paths        : " + (($drops[-1].data.paths) -join ' | ') }
if ($Expected -gt 0) { "expected cycles        : $Expected  shows=$($shows.Count) enters=$($enters.Count) drops=$($drops.Count)" }
if ($stats.Count -gt 1) {
    $f = $stats[0].data; $l = $stats[-1].data
    "stats first            : ws={0}MB priv={1}MB gdi={2} user={3} handles={4} threads={5}" -f $f.proc.workingSetMB, $f.proc.privateMB, $f.proc.gdi, $f.proc.user, $f.proc.handles, $f.proc.threads
    "stats last             : ws={0}MB priv={1}MB gdi={2} user={3} handles={4} threads={5}" -f $l.proc.workingSetMB, $l.proc.privateMB, $l.proc.gdi, $l.proc.user, $l.proc.handles, $l.proc.threads
    "cpuPct over run        : " + (Stat ($stats | ForEach-Object { [double]$_.data.cpuPct }))
    "hook max ms (kb/mouse) : {0} / {1}" -f ($stats | ForEach-Object { [double]$_.data.hookKbMaxMs } | Measure-Object -Maximum).Maximum, ($stats | ForEach-Object { [double]$_.data.hookMouseMaxMs } | Measure-Object -Maximum).Maximum
}
