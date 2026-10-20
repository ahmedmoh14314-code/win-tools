# Runs one automated scenario: start an overlay host, run the drag harness against it, stop the host, summarise.
# Usage: pwsh ./scripts/Run-Scenario.ps1 -Host win32|wpf -Trigger space -Mode self|explorer -Cycles 20 [-Cancel] [-Files 3] [-Name tag] [-Config Debug|Release]
param(
    [ValidateSet('win32','wpf')] [string] $HostKind = 'win32',
    [string] $Trigger = 'space',
    [ValidateSet('self','explorer')] [string] $Mode = 'self',
    [int] $Cycles = 20,
    [switch] $Cancel,
    [int] $Files = 3,
    [string] $Name = '',
    [ValidateSet('Debug','Release')] [string] $Config = 'Debug',
    [int] $StatsSeconds = 2,
    [int] $IdleAfterSeconds = 0,
    [string[]] $ExtraArgs = @(),
    [string[]] $HostArgs = @()
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$rid = "net10.0-windows\win-x64"
$hostExe = if ($HostKind -eq 'win32') { "$root\src\Spike.Interaction.Win32\bin\$Config\$rid\Spike.Interaction.Win32.exe" } else { "$root\src\Spike.Interaction.Wpf\bin\$Config\$rid\Spike.Interaction.Wpf.exe" }
$harness = "$root\src\Spike.Interaction.DragSource\bin\$Config\$rid\Spike.Interaction.DragSource.exe"
if (-not $Name) { $Name = "$HostKind-$Mode-$($Trigger -replace '\+','_')-$Cycles" + $(if ($Cancel) { '-cancel' } else { '' }) + "-$Config" }
$hostLog = "$root\results\host-$Name.jsonl"
$harnessLog = "$root\results\harness-$Name.jsonl"

$hostArgList = @('--trigger', $Trigger, '--log', $hostLog, '--stats-interval', $StatsSeconds, '--quiet') + $HostArgs
$p = Start-Process -FilePath $hostExe -ArgumentList $hostArgList -PassThru -WindowStyle Minimized
Start-Sleep -Seconds 3
if ($p.HasExited) { throw "host exited early with $($p.ExitCode)" }

$args = @('--mode', $Mode, '--cycles', $Cycles, '--trigger', $Trigger, '--files', $Files, '--log', $harnessLog)
if ($Cancel) { $args += '--cancel' }
if ($ExtraArgs) { $args += $ExtraArgs }
$out = & $harness @args 2>&1
$code = $LASTEXITCODE
"harness exit=$code"
$out | Where-Object { $_ -match 'foregroundCheck|foregroundLost|abort|timeout|summary|Refusing|itemNotFound|notFound' } | Select-Object -Last 6

if ($IdleAfterSeconds -gt 0) { Start-Sleep -Seconds $IdleAfterSeconds }
Start-Sleep -Seconds ($StatsSeconds + 1)   # let one more stats sample land
Stop-Process -Id $p.Id -Force
Start-Sleep -Milliseconds 300
& "$PSScriptRoot\Summarize-SpikeLog.ps1" -Path $hostLog -Expected $Cycles
