# Windows launch smoke test: starts the published exe with an isolated temp data dir, waits for the
# in-app marker (window created, preferences loaded, UI bound) and exit code 0. Does not touch real user data.
param([string]$Exe = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/Hikyaku/Hikyaku.exe'), [int]$TimeoutSec = 30)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { Write-Host "Missing $Exe (run scripts/build.ps1)"; exit 2 }
$dir = Join-Path ([IO.Path]::GetTempPath()) ("hikyaku-smoke-" + [guid]::NewGuid().ToString('N'))
$env:HIKYAKU_DATA_DIR = $dir
try {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process -FilePath $Exe -ArgumentList '--smoke' -PassThru
  if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill(); Write-Host 'SMOKE FAIL: timeout'; exit 1 }
  $marker = Join-Path $dir 'smoke.txt'
  if ($p.ExitCode -ne 0 -or -not (Test-Path $marker)) { Write-Host "SMOKE FAIL: exit=$($p.ExitCode) marker=$(Test-Path $marker)"; exit 1 }
  Write-Host ("SMOKE OK in {0} ms: {1}" -f $sw.ElapsedMilliseconds, (Get-Content $marker -Raw))
} finally {
  Remove-Item Env:HIKYAKU_DATA_DIR -ErrorAction SilentlyContinue
  if (Test-Path $dir) { Remove-Item $dir -Recurse -Force }
}
