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
    [string] $Label = '0.1.0-beta',
    # The name shown in Setup and Apps ("0.2.0 Beta", "0.2.0 Preview") and the one in the file name ("0.2.0-Beta").
    [string] $DisplayLabel = '',
    [string] $FileLabel = ''
)
if (-not $DisplayLabel) { $DisplayLabel = "$Version Beta" }
if (-not $FileLabel) { $FileLabel = "$Version-Beta" }
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$payload = Join-Path $root 'artifacts\installer\payload'
if (Test-Path $payload) { Remove-Item -LiteralPath $payload -Recurse -Force }

$props = @('-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    "-p:Version=$Version", "-p:FileVersion=$Version.0", "-p:AssemblyVersion=$Version.0", "-p:InformationalVersion=$Label",
    '-p:IncludeSourceRevisionInInformationalVersion=false', '-p:Product=WinTools',
    '-p:DebugType=none', '-p:DebugSymbols=false', '-p:GenerateDocumentationFile=false')
# The PDF engine the PDF worker bundles: fetched and hash-checked (ADR-0022) before anything is published.
& (Join-Path $root 'scripts\Get-Engines.ps1')
# Host first; then the workers (images, General Files, PDF), the settings window and the dialogs, each with its own runtimeconfig for the bundled runtime.
foreach ($project in 'WinTools.Host', 'WinTools.Worker', 'WinTools.Worker.Text', 'WinTools.Worker.Pdf', 'WinTools.Worker.Archive', 'WinTools.Worker.Media', 'WinTools.Settings', 'WinTools.Dialogs') {
    & dotnet publish (Join-Path $root "src\$project") @props -o $payload
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project failed" }
}

# The payload must run without an installed .NET and must not carry debug or developer files.
foreach ($exe in 'WinTools.Host', 'WinTools.Worker', 'WinTools.Worker.Text', 'WinTools.Worker.Pdf', 'WinTools.Worker.Archive', 'WinTools.Worker.Media', 'WinTools.Settings', 'WinTools.Dialogs') {
    $config = Get-Content -Raw (Join-Path $payload "$exe.runtimeconfig.json") | ConvertFrom-Json
    if (-not $config.runtimeOptions.includedFrameworks) { throw "$exe is not self-contained" }
}
$worker = Get-Content -Raw (Join-Path $payload 'WinTools.Worker.runtimeconfig.json') | ConvertFrom-Json
if ($worker.runtimeOptions.configProperties.'System.GC.HeapHardLimit' -ne 1610612736) { throw 'The worker memory limit is missing (ADR-0015)' }
$unwanted = Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object { $_.Extension -in '.pdb', '.log', '.cs', '.json' -and $_.Name -notlike '*.runtimeconfig.json' -and $_.Name -notlike '*.deps.json' }
if ($unwanted) { throw "Unexpected files in the payload: $($unwanted.Name -join ', ')" }
foreach ($engine in 'qpdf.exe', 'qpdf30.dll', 'msvcp140.dll', 'vcruntime140.dll', 'vcruntime140_1.dll') {
    if (-not (Test-Path -LiteralPath (Join-Path $payload "qpdf\$engine"))) { throw "The PDF engine file qpdf\$engine is missing from the payload" }
}
foreach ($engine in 'ffmpeg.exe', 'ffprobe.exe', 'avcodec-62.dll', 'avdevice-62.dll', 'avfilter-11.dll', 'avformat-62.dll', 'avutil-60.dll', 'swresample-6.dll', 'swscale-9.dll', 'LICENSE.txt') {
    if (-not (Test-Path -LiteralPath (Join-Path $payload "ffmpeg\$engine"))) { throw "The media engine file ffmpeg\$engine is missing from the payload" }
}

# One notices file for everything bundled (V2 plan §8): each component's licence and notices, as its project ships them.
$notices = [System.Text.StringBuilder]::new()
[void]$notices.AppendLine('WinTools includes the following third-party software. Each is used under the licence that follows its name.')
[void]$notices.AppendLine()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'third-party') -Recurse -File | Where-Object Name -ne 'README.md' | Sort-Object FullName) {
    $relative = $file.FullName.Substring((Join-Path $root 'third-party').Length + 1)
    [void]$notices.AppendLine(('=' * 78)).AppendLine($relative).AppendLine(('=' * 78)).AppendLine((Get-Content -Raw -LiteralPath $file.FullName)).AppendLine()
}
[System.IO.File]::WriteAllText((Join-Path $payload 'THIRD-PARTY-NOTICES.txt'), $notices.ToString(), [System.Text.UTF8Encoding]::new($false))

& $Iscc "/DPayloadDir=$payload" "/DOutputDir=$(Join-Path $root 'artifacts\installer')" "/DAppVersion=$Version" "/DAppVersionLabel=$DisplayLabel" "/DAppFileLabel=$FileLabel" (Join-Path $PSScriptRoot 'WinTools.iss')
if ($LASTEXITCODE -ne 0) { throw 'ISCC failed' }
$setup = Join-Path $root "artifacts\installer\WinTools-$FileLabel-Setup.exe"

# The portable package: the same payload as a ZIP (no installer, no Start menu entry, no uninstall entry).
$portable = Join-Path $root "artifacts\installer\WinTools-$FileLabel-portable.zip"
if (Test-Path -LiteralPath $portable) { Remove-Item -LiteralPath $portable -Force }
# Written entry by entry: Windows PowerShell's Compress-Archive names entries with '\' ("qpdf\qpdf.exe"), which the ZIP
# format does not allow and tools on other systems read as one odd file name.
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$zip = [System.IO.Compression.ZipFile]::Open($portable, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    $base = (Resolve-Path -LiteralPath $payload).Path.TrimEnd('\') + '\'
    foreach ($file in Get-ChildItem -LiteralPath $payload -Recurse -File) {
        $name = $file.FullName.Substring($base.Length).Replace('\', '/')
        [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $file.FullName, $name, [System.IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $zip.Dispose() }
Get-FileHash -Algorithm SHA256 -LiteralPath $portable | Format-List
Get-FileHash -Algorithm SHA256 -LiteralPath $setup | Format-List
