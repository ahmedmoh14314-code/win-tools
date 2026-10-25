# Makes the encrypted ZIP samples (V2 PR 5.3) with WinRAR, an encoder independent of the decryption under test.
# Recorded with WinRAR 6.24 (x64, signed by win.rar GmbH). Run from any folder: .\New-EncryptedZipSamples.ps1 -OutDir <folder>
param(
    [Parameter(Mandatory)][string]$OutDir,
    [string]$WinRar = 'C:\Program Files\WinRAR\WinRAR.exe'
)
$ErrorActionPreference = 'Stop'
$work = Join-Path ([IO.Path]::GetTempPath()) ("wintools-zipenc-" + [Guid]::NewGuid().ToString('N'))
$src = Join-Path $work 'src'
New-Item -ItemType Directory -Path (Join-Path $src 'sub') | Out-Null
[IO.File]::WriteAllText((Join-Path $src 'a.txt'), "hello encrypted world`n")
[IO.File]::WriteAllBytes((Join-Path $src 'sub\big.bin'), [Text.Encoding]::ASCII.GetBytes('WinTools sample line ' * 5000))
[IO.File]::WriteAllText((Join-Path $src 'tiny.txt'), 'x')
$arabic = 'سرّé1'
$samples = @(
    @{ Name = 'locked-aes256.zip'; Password = 'right'; Legacy = $false },
    @{ Name = 'locked-zipcrypto.zip'; Password = 'right'; Legacy = $true },
    @{ Name = 'locked-aes256-arabic.zip'; Password = $arabic; Legacy = $false },
    @{ Name = 'locked-zipcrypto-arabic.zip'; Password = $arabic; Legacy = $true }
)
try {
    foreach ($s in $samples) {
        $out = Join-Path $OutDir $s.Name
        if (Test-Path $out) { Remove-Item $out }
        $args = @('a', '-afzip', '-ibck', '-y', '-ep1', '-r', "-p$($s.Password)")
        if ($s.Legacy) { $args += '-mezl' } # ZIP 2.0 ("ZipCrypto") instead of AES-256
        $args += @($out, (Join-Path $src '*'))
        $p = Start-Process -FilePath $WinRar -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
        if ($p.ExitCode -ne 0) { throw "WinRAR exited $($p.ExitCode) for $($s.Name)" }
        '{0}  {1}' -f (Get-FileHash $out -Algorithm SHA256).Hash, $s.Name
    }
}
finally { Remove-Item -Recurse -Force $work }
