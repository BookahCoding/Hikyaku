# Synthetic UI acceptance: demo transport, screenshots, toast observation label. Not live backend.
param(
  [string]$Exe = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/Hikyaku/Hikyaku.exe'),
  [int]$TimeoutSec = 60,
  [string]$OutDir = ''
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Exe)) { Write-Host "Missing $Exe (run scripts/build.ps1)"; exit 2 }
$dir = if ($OutDir) { $OutDir } else { Join-Path ([IO.Path]::GetTempPath()) ("hikyaku-ui-" + [guid]::NewGuid().ToString('N')) }
New-Item -ItemType Directory -Force -Path $dir | Out-Null
$env:HIKYAKU_DATA_DIR = $dir
try {
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $p = Start-Process -FilePath $Exe -ArgumentList '--ui-acceptance' -PassThru
  if (-not $p.WaitForExit($TimeoutSec * 1000)) { $p.Kill(); Write-Host 'UI ACCEPTANCE FAIL: timeout'; exit 1 }
  $marker = Join-Path $dir 'ui-acceptance\ui-acceptance.txt'
  if ($p.ExitCode -ne 0 -or -not (Test-Path $marker)) {
    Write-Host "UI ACCEPTANCE FAIL: exit=$($p.ExitCode) marker=$(Test-Path $marker)"
    if (Test-Path (Join-Path $dir 'ui-acceptance')) { Get-ChildItem (Join-Path $dir 'ui-acceptance') | ForEach-Object { $_.FullName } }
    exit 1
  }
  $result = (Get-Content $marker -Raw).Trim()
  # The app writes HIKYAKU_UI_FAIL (and exits 0) when a check inside the run throws; that must not read as a pass.
  if ($result -notmatch '^HIKYAKU_UI_OK\b') { Write-Host "UI ACCEPTANCE FAIL: $result"; exit 1 }
  Write-Host ("UI ACCEPTANCE OK in {0} ms: {1}" -f $sw.ElapsedMilliseconds, $result)
  Write-Host "Report: $(Join-Path $dir 'ui-acceptance\REPORT.md')"
} finally {
  Remove-Item Env:HIKYAKU_DATA_DIR -ErrorAction SilentlyContinue
}
