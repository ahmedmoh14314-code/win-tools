# Makes the RAR samples (ADR-0030) with WinRAR's console Rar.exe: no free RAR encoder exists, and WinRAR is RARLAB's own,
# so it is also independent of the reader under test (SharpCompress). Recorded with Rar.exe 6.24 x64 (signed by
# win.rar GmbH, the evaluation copy on the developer machine). Nothing in the build or CI runs it.
# Run from any folder: .\New-RarSamples.ps1 -OutDir <folder>
param(
    [Parameter(Mandatory)][string]$OutDir,
    [string]$Rar = 'C:\Program Files\WinRAR\Rar.exe'
)
$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ("wintools-rar-" + [Guid]::NewGuid().ToString('N'))
$fixed = [DateTime]::new(2026, 1, 1, 0, 0, 0, [DateTimeKind]::Utc)
$arabic = 'سرّé1'

function Write-Sample([string]$Path, [byte[]]$Bytes) {
    New-Item -ItemType Directory -Force -Path (Split-Path $Path) | Out-Null
    [IO.File]::WriteAllBytes($Path, $Bytes)
    [IO.File]::SetLastWriteTimeUtc($Path, $fixed)
}

function Invoke-Rar([string]$Name, [string]$From, [string[]]$Switches, [string[]]$Items) {
    $out = Join-Path $OutDir $Name
    Get-ChildItem -LiteralPath $OutDir -Filter (($Name -replace '\.rar$', '') + '*.rar') | Remove-Item
    Push-Location $From
    try {
        & $Rar a -idq -y -r @Switches $out @Items
        if ($LASTEXITCODE -ne 0) { throw "Rar.exe exited $LASTEXITCODE for $Name" }
    }
    finally { Pop-Location }
    Get-ChildItem -LiteralPath $OutDir -Filter (($Name -replace '\.rar$', '') + '*.rar') | ForEach-Object { '{0}  {1}' -f (Get-FileHash $_.FullName -Algorithm SHA256).Hash, $_.Name }
}

try {
    # The tree every ordinary sample holds.
    $src = Join-Path $work 'src'
    Write-Sample (Join-Path $src 'a.txt') ([Text.Encoding]::ASCII.GetBytes("hello rar world`n"))
    Write-Sample (Join-Path $src 'sub\big.bin') ([Text.Encoding]::ASCII.GetBytes('WinTools sample line ' * 5000))
    Write-Sample (Join-Path $src 'tiny.txt') ([Text.Encoding]::ASCII.GetBytes('x'))
    Write-Sample (Join-Path $src 'ملاحظات.txt') ([Text.Encoding]::UTF8.GetBytes('ملاحظة'))
    $items = @('a.txt', 'sub', 'tiny.txt', 'ملاحظات.txt')
    Invoke-Rar 'rar5.rar' $src @('-ma5') $items
    Invoke-Rar 'rar4.rar' $src @('-ma4') $items
    Invoke-Rar 'rar5-solid.rar' $src @('-ma5', '-s') $items
    Invoke-Rar 'rar4-solid.rar' $src @('-ma4', '-s') $items
    Invoke-Rar 'rar5-locked.rar' $src @('-ma5', '-pright') $items
    Invoke-Rar 'rar5-locked-names.rar' $src @('-ma5', '-hpright') $items
    Invoke-Rar 'rar4-locked.rar' $src @('-ma4', '-pright') $items
    Invoke-Rar 'rar4-locked-names.rar' $src @('-ma4', '-hpright') $items
    Invoke-Rar 'rar5-locked-arabic.rar' $src @('-ma5', "-p$arabic") $items

    # Links: a hard link (RAR 5 stores the second name as a redirection) and a junction to C:\Windows. The junction is
    # removed on its own before the folder, so nothing ever follows it.
    $links = Join-Path $work 'links'
    Write-Sample (Join-Path $links 'ok.txt') ([Text.Encoding]::ASCII.GetBytes('fine'))
    New-Item -ItemType HardLink -Path (Join-Path $links 'hard.txt') -Target (Join-Path $links 'ok.txt') | Out-Null
    $junction = Join-Path $links 'windows'
    New-Item -ItemType Junction -Path $junction -Target 'C:\Windows' | Out-Null
    try { Invoke-Rar 'rar5-links.rar' $links @('-ma5', '-ol', '-oh', '-x*\windows\*') @('ok.txt', 'hard.txt', 'windows') }
    finally { [IO.Directory]::Delete($junction, $false) }

    # 4 MB of zeros in a non-solid RAR: a few hundred bytes that expand far beyond 200:1 (the per-file bomb rule).
    $zeros = Join-Path $work 'zeros'
    Write-Sample (Join-Path $zeros 'zeros.bin') (New-Object byte[] (4MB))
    Invoke-Rar 'rar5-zeros.rar' $zeros @('-ma5', '-m5') @('zeros.bin')

    # An archive in parts of 2 KB (rar5-split.part1.rar, .part2.rar): stored, so the parts stay small.
    $split = Join-Path $work 'split'
    Write-Sample (Join-Path $split 'random.bin') ([byte[]](0..2999 | ForEach-Object { ($_ * 7919 + 13) % 251 }))
    Invoke-Rar 'rar5-split.rar' $split @('-ma5', '-m0', '-v2k') @('random.bin')
    # Stored and encrypted: a changed byte decrypts to wrong bytes without any decoder noticing, so only the CRC-32 can.
    Invoke-Rar 'rar4-locked-stored.rar' $split @('-ma4', '-m0', '-pright') @('random.bin')
}
finally {
    if (Test-Path $work) { Remove-Item -Recurse -Force $work }
}
