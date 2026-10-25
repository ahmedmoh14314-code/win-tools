<#
.SYNOPSIS
  Fetches the PDF engine (qpdf) the build bundles, pinned and hash-checked (ADR-0022, dependency matrix).
.DESCRIPTION
  Downloads the official qpdf Windows x64 release from the qpdf project's GitHub releases, checks the archive's
  SHA-256 against the value pinned here (the same value is in qpdf's PGP-signed hash list for the release), extracts
  only the files WinTools runs, checks each of their hashes, and puts them in artifacts\engines\qpdf-<version>.
  Nothing is run. A file that is already there with the right hash is kept; any mismatch stops the build.
  The download is cached in artifacts\engines\downloads; pass -Archive to use a copy you fetched yourself.
#>
param(
    [string] $Archive = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Windows PowerShell 5.1 crawls through a download that draws a progress bar
$root = Split-Path -Parent $PSScriptRoot

$version = '12.4.2'
$url = "https://github.com/qpdf/qpdf/releases/download/v$version/qpdf-$version-msvc64.zip"
$archiveSha256 = 'db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d'
# The files WinTools runs: qpdf, its library, and the Visual C++ runtime they import (the qpdf build ships them).
$files = [ordered]@{
    'qpdf.exe'         = '1bdaf878f37eaffea1ea3c30591214c77636a0f2fc7432427516b6e22e36c0ff'
    'qpdf30.dll'       = '8812952678de93e0750308a113aa650e9161af466f9770679ea9673adeb61041'
    'msvcp140.dll'     = '7c26614e1d733892c2deac7e245ce115504b1d80592dd0a01b08e3e5a55f89ca'
    'vcruntime140.dll' = 'd1f4225df2cd877dbf130d5668a021dce3f94118455ff5ec952061c30afc9ce7'
    'vcruntime140_1.dll' = 'a7146c08f89fe5b04541ab507cdb59ff7b44534d4ba3c668a426c6450a03434e'
}

$target = Join-Path $root "artifacts\engines\qpdf-$version"
function Test-Target {
    foreach ($name in $files.Keys) {
        $path = Join-Path $target $name
        if (-not (Test-Path -LiteralPath $path)) { return $false }
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() -ne $files[$name]) { throw "$path does not match its pinned hash; delete the folder and run this again." }
    }
    return $true
}
if (Test-Target) { Write-Host "qpdf $version is in place: $target"; return }

$downloads = Join-Path $root 'artifacts\engines\downloads'
New-Item -ItemType Directory -Force -Path $downloads | Out-Null
if (-not $Archive) {
    $Archive = Join-Path $downloads "qpdf-$version-msvc64.zip"
    if (-not (Test-Path -LiteralPath $Archive)) {
        Write-Host "Downloading $url"
        # Into a partial file first, so an interrupted download never looks like a cached archive.
        $partial = "$Archive.partial"
        if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
        Invoke-WebRequest -Uri $url -OutFile $partial -UseBasicParsing
        Move-Item -LiteralPath $partial -Destination $Archive
    }
}
$actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Archive).Hash.ToLowerInvariant()
if ($actual -ne $archiveSha256) { throw "The qpdf archive's SHA-256 is $actual, not the pinned $archiveSha256. Not used; delete $Archive and run this again." }

Add-Type -AssemblyName System.IO.Compression.FileSystem
$staging = Join-Path $downloads "qpdf-$version-staging"
if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
New-Item -ItemType Directory -Force -Path $staging | Out-Null
$zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
try {
    foreach ($name in $files.Keys) {
        # Exactly one entry by its known path inside the archive; nothing else is extracted.
        $entry = $zip.GetEntry("qpdf-$version-msvc64/bin/$name")
        if (-not $entry) { throw "qpdf-$version-msvc64/bin/$name is not in the archive." }
        [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $staging $name), $false)
    }
} finally { $zip.Dispose() }
foreach ($name in $files.Keys) {
    $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $staging $name)).Hash.ToLowerInvariant()
    if ($hash -ne $files[$name]) { throw "$name has SHA-256 $hash, not the pinned $($files[$name]). Not used." }
}
if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
Move-Item -LiteralPath $staging -Destination $target
Write-Host "qpdf $version extracted and checked: $target"
