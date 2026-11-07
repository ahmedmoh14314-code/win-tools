<#
.SYNOPSIS
  Fetches the engines the build bundles, pinned and hash-checked: qpdf (ADR-0022), FFmpeg (ADR-0028) and PDFium (ADR-0029).
.DESCRIPTION
  Downloads each engine's official Windows x64 release, checks the archive's SHA-256 against the value pinned here,
  extracts only the files WinTools runs, checks each of their hashes, and puts them in artifacts\engines\<name>.
  Nothing is run. A file that is already there with the right hash is kept; any mismatch stops the build.
  Downloads are cached in artifacts\engines\downloads.
  - qpdf 12.4.2: the qpdf project's GitHub release; the archive hash is also in qpdf's PGP-signed hash list.
  - FFmpeg 8.1.3 (LGPL shared build): BtbN/FFmpeg-Builds, release autobuild-2026-10-24-13-02; the archive hash is the
    one in that release's checksums.sha256. Configured with --enable-version3 and without --enable-gpl or nonfree:
    LGPL 3.0, run as a separate program.
  - PDFium chromium/8111 (bblanchon/pdfium-binaries): the library alone, built without V8 and XFA (no JavaScript, no
    forms engine). BSD-3-Clause with permissive third-party licences (third-party\pdfium).
#>
param(
    [string] $QpdfArchive = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue' # Windows PowerShell 5.1 crawls through a download that draws a progress bar
$root = Split-Path -Parent $PSScriptRoot
$downloads = Join-Path $root 'artifacts\engines\downloads'
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Engine {
    param(
        [string] $Name,        # the folder under artifacts\engines
        [string] $Url,
        [string] $ArchiveName,
        [string] $ArchiveSha256,
        [string] $Prefix,      # the path of the files inside the archive
        [System.Collections.Specialized.OrderedDictionary] $Files, # name inside the prefix -> SHA-256
        [string] $Archive = ''
    )
    $target = Join-Path $root "artifacts\engines\$Name"
    $complete = $true
    foreach ($file in $Files.Keys) {
        $path = Join-Path $target (Split-Path -Leaf $file)
        if (-not (Test-Path -LiteralPath $path)) { $complete = $false; break }
        if ((Get-FileHash -Algorithm SHA256 -LiteralPath $path).Hash.ToLowerInvariant() -ne $Files[$file]) { throw "$path does not match its pinned hash; delete the folder and run this again." }
    }
    if ($complete) { Write-Host "$Name is in place: $target"; return }

    New-Item -ItemType Directory -Force -Path $downloads | Out-Null
    if (-not $Archive) {
        $Archive = Join-Path $downloads $ArchiveName
        if (-not (Test-Path -LiteralPath $Archive)) {
            Write-Host "Downloading $Url"
            # Into a partial file first, so an interrupted download never looks like a cached archive.
            $partial = "$Archive.partial"
            if (Test-Path -LiteralPath $partial) { Remove-Item -LiteralPath $partial -Force }
            Invoke-WebRequest -Uri $Url -OutFile $partial -UseBasicParsing
            Move-Item -LiteralPath $partial -Destination $Archive
        }
    }
    $actual = (Get-FileHash -Algorithm SHA256 -LiteralPath $Archive).Hash.ToLowerInvariant()
    if ($actual -ne $ArchiveSha256) { throw "The $Name archive's SHA-256 is $actual, not the pinned $ArchiveSha256. Not used; delete $Archive and run this again." }

    $staging = Join-Path $downloads "$Name-staging"
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
    New-Item -ItemType Directory -Force -Path $staging | Out-Null
    if ($ArchiveName.EndsWith('.tgz')) {
        # A gzipped tar: Windows' own tar extracts exactly the named entries, then they are flattened like a ZIP's.
        # (Not $name: PowerShell names ignore case, and it would overwrite $Name.)
        $tar = Join-Path $env:SystemRoot 'System32\tar.exe'
        $names = @($Files.Keys | ForEach-Object { if ($Prefix) { "$Prefix/$_" } else { $_ } })
        & $tar -xzf $Archive -C $staging @names
        if ($LASTEXITCODE -ne 0) { throw "tar could not extract $($names -join ', ') from $Archive." }
        foreach ($entryName in $names) {
            $extracted = Join-Path $staging ($entryName -replace '/', '\')
            if (-not (Test-Path -LiteralPath $extracted)) { throw "$entryName is not in the archive." }
            if ((Split-Path -Parent $extracted) -ne $staging) { Move-Item -LiteralPath $extracted -Destination (Join-Path $staging (Split-Path -Leaf $entryName)) }
        }
        Get-ChildItem -LiteralPath $staging -Directory | Remove-Item -Recurse -Force
    } else {
        $zip = [System.IO.Compression.ZipFile]::OpenRead($Archive)
        try {
            foreach ($file in $Files.Keys) {
                # Exactly one entry by its known path inside the archive; nothing else is extracted.
                $entry = $zip.GetEntry("$Prefix/$file")
                if (-not $entry) { throw "$Prefix/$file is not in the archive." }
                [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, (Join-Path $staging (Split-Path -Leaf $file)), $false)
            }
        } finally { $zip.Dispose() }
    }
    foreach ($file in $Files.Keys) {
        $hash = (Get-FileHash -Algorithm SHA256 -LiteralPath (Join-Path $staging (Split-Path -Leaf $file))).Hash.ToLowerInvariant()
        if ($hash -ne $Files[$file]) { throw "$file has SHA-256 $hash, not the pinned $($Files[$file]). Not used." }
    }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
    Move-Item -LiteralPath $staging -Destination $target
    Write-Host "$Name extracted and checked: $target"
}

# qpdf: itself, its library, and the Visual C++ runtime they import (the qpdf build ships them).
$qpdfVersion = '12.4.2'
Get-Engine -Name "qpdf-$qpdfVersion" -Archive $QpdfArchive `
    -Url "https://github.com/qpdf/qpdf/releases/download/v$qpdfVersion/qpdf-$qpdfVersion-msvc64.zip" `
    -ArchiveName "qpdf-$qpdfVersion-msvc64.zip" -ArchiveSha256 'db87077e683630c1217e0e8f9a20a9749d952ab676e881c3689187763a5de25d' `
    -Prefix "qpdf-$qpdfVersion-msvc64/bin" -Files ([ordered]@{
        'qpdf.exe'           = '1bdaf878f37eaffea1ea3c30591214c77636a0f2fc7432427516b6e22e36c0ff'
        'qpdf30.dll'         = '8812952678de93e0750308a113aa650e9161af466f9770679ea9673adeb61041'
        'msvcp140.dll'       = '7c26614e1d733892c2deac7e245ce115504b1d80592dd0a01b08e3e5a55f89ca'
        'vcruntime140.dll'   = 'd1f4225df2cd877dbf130d5668a021dce3f94118455ff5ec952061c30afc9ce7'
        'vcruntime140_1.dll' = 'a7146c08f89fe5b04541ab507cdb59ff7b44534d4ba3c668a426c6450a03434e'
    })

# FFmpeg: the two programs, their shared libraries and the licence (ffplay is not shipped).
$ffmpegBuild = 'ffmpeg-n8.1.3-24-g92611900bb-win64-lgpl-shared-8.1'
Get-Engine -Name 'ffmpeg-8.1.3' `
    -Url "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-10-24-13-02/$ffmpegBuild.zip" `
    -ArchiveName "$ffmpegBuild.zip" -ArchiveSha256 '0c2e70f180ae48a748acf2308229ae19eafc734515e9876c75cfe36998981c53' `
    -Prefix $ffmpegBuild -Files ([ordered]@{
        'bin/ffmpeg.exe'       = 'ad567b69484cde6ebaacf6ed3e5ae591e3624a5d853dd9e9969e104d3cdaa4b5'
        'bin/ffprobe.exe'      = 'a53f767ba68306f5f9205b40caabe34cc9028a0ef1504fbbe1657d9f09c9be4b'
        'bin/avcodec-62.dll'   = '264be085d9ef6d09087981beba3be6d23153c4753a5415eccfabc11a7e1090a0'
        'bin/avdevice-62.dll'  = '15b5fc0fbd3137eec120338bf0af717c48aea83ffd31f5d809b2f6c811d830f2'
        'bin/avfilter-11.dll'  = 'ae186471622640695d4042e46c5110a16ba2b025ce2fe740f2a4c9af09101f38'
        'bin/avformat-62.dll'  = 'c6fb1606ce04ae3a04b69f4cbff1c79cd20dbdae1f7b0d5f8a0239f5593b1ebf'
        'bin/avutil-60.dll'    = '3ba430598f7020539c9fa4711bce6785eb760262e590083b3e77434b982f5bbe'
        'bin/swresample-6.dll' = 'feaef02141f2fa5ef04904cf2c89f6accd9b2236eeb7805ca57a5df2480850a9'
        'bin/swscale-9.dll'    = 'a2b0785591a69e10d23c025a2e18afe6ee42c778b90ee2bee07d2ac353b7bc8c'
        'LICENSE.txt'          = 'da7eabb7bafdf7d3ae5e9f223aa5bdc1eece45ac569dc21b3b037520b4464768'
    })

# PDFium: the library only (rendering and text for PDF to PNG, JPG, TXT and Markdown, and the check of Images to PDF).
Get-Engine -Name 'pdfium-8111' `
    -Url 'https://github.com/bblanchon/pdfium-binaries/releases/download/chromium/8111/pdfium-win-x64.tgz' `
    -ArchiveName 'pdfium-win-x64-8111.tgz' -ArchiveSha256 '3452d11031050ec65e57f180215b57b9e6d465e3fbedafa8a77ad90885c7d23e' `
    -Prefix '' -Files ([ordered]@{
        'bin/pdfium.dll' = 'a318c39c137482dc4687c2973b7ec1c27fc1c20253cf2dc31928f922eab89e8f'
    })
