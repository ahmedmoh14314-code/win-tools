<#
.SYNOPSIS
  Builds the WinTools per-user installer: a self-contained Release win-x64 payload, then Setup.exe with Inno Setup.
.DESCRIPTION
  Publishes the host, the worker and the settings window self-contained into artifacts\installer\payload, without
  debug symbols (no PDB, no local paths in the binaries), checks the payload, and compiles installer\WinTools.iss.
  The source is not changed; the version is set for this build only.
.PARAMETER Iscc
  Path to ISCC.exe (Inno Setup 6.7 or later, command-line compiler).
#>
param(
    [Parameter(Mandatory = $true)] [string] $Iscc,
    [string] $Version = '0.1.0',
    [string] $Label = '0.1.0-beta'
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $root 'artifacts\installer\payload'
if (Test-Path $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }

$props = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    "-p:Version=$Version", "-p:FileVersion=$Version.0", "-p:AssemblyVersion=$Version.0", "-p:InformationalVersion=$Label",
    '-p:IncludeSourceRevisionInInformationalVersion=false', '-p:Product=WinTools',
    '-p:DebugType=none', '-p:DebugSymbols=false', '-p:GenerateDocumentationFile=false')
# Host first; then the worker and the settings window, each with its own runtimeconfig for the bundled runtime.
foreach ($project in 'WinTools.Host', 'WinTools.Worker', 'WinTools.Settings', 'WinTools.Dialogs') {
    & dotnet publish (Join-Path $root "src\$project") @props -o $payload
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}

# The payload must run without an installed .NET and must not carry debug or developer files.
foreach ($exe in 'WinTools.Host', 'WinTools.Worker', 'WinTools.Settings', 'WinTools.Dialogs') {
    $config = Get-Content -Raw (Join-Path $payload "$exe.runtimeconfig.json") | ConvertFrom-Json
    if (-not $config.runtimeOptions.includedFrameworks) { throw "$exe is not self-contained" }
}
$worker = Get-Content -Raw (Join-Path $payload 'WinTools.Worker.runtimeconfig.json') | ConvertFrom-Json
if ($worker.runtimeOptions.configProperties.'System.GC.HeapHardLimit' -ne 1610612736) { throw 'The worker memory limit is missing (ADR-0015)' }
$unwanted = Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Extension -in '.pdb', '.log', '.cs', '.json' -and $_.Name -notlike '*.runtimeconfig.json' -and $_.Name -notlike '*.deps.json' }
if ($unwanted) { throw "Unexpected files in the payload: $($unwanted.Name -join ', ')" }

& $Iscc "/DPayloadDir=$payload" "/DOutputDir=$(Join-Path $root 'artifacts\installer')" (Join-Path $PSScriptRoot 'WinTools.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
$setup = Join-Path $root "artifacts\installer\WinTools-$Version-Beta-Setup.exe"
Get-FileHash -Algorithm SHA256 -LiteralPath $setup | Format-List
