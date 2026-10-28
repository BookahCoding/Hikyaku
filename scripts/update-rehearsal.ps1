# Rehearses ONLY the real exe's --apply-update helper mode against throwaway folders: an "old" install and a
# staged "new" package built from the current publish. It does NOT exercise download, signature verification or
# GitHub (those are covered by the Core tests); the staging marker is written by this script, so this is not proof
# that a signed release installs. Uses an isolated HIKYAKU_DATA_DIR; touches nothing outside the temp folder.
param([string]$Published = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts/Hikyaku'))
$ErrorActionPreference = 'Stop'
if (-not (Test-Path (Join-Path $Published 'Hikyaku.exe'))) { Write-Host "Missing $Published (run scripts/build.ps1)"; exit 2 }
$root = Join-Path ([IO.Path]::GetTempPath()) ("hikyaku-rehearsal-" + [guid]::NewGuid().ToString('N'))
$data = Join-Path $root 'data'
$install = Join-Path $root 'install'
$staging = Join-Path $data 'updates/x/staged'
$launched = $null
try {
  New-Item -ItemType Directory -Force $data, $install, $staging | Out-Null
  Copy-Item (Join-Path $Published '*') $install -Recurse -Force
  Copy-Item (Join-Path $Published '*') $staging -Recurse -Force
  Set-Content (Join-Path $install 'VERSION.txt') 'old' -NoNewline
  Set-Content (Join-Path $install 'user-note.txt') 'keep' -NoNewline
  Set-Content (Join-Path $staging 'VERSION.txt') 'new' -NoNewline
  Set-Content (Join-Path $staging '.hikyaku-verified.json') '{"format":1,"version":"9.9.9"}' -NoNewline

  $env:HIKYAKU_DATA_DIR = $data
  $sw = [Diagnostics.Stopwatch]::StartNew()
  $helper = Start-Process (Join-Path $staging 'Hikyaku.exe') -ArgumentList '--apply-update','--pid','0','--install',"`"$install`"",'--staging',"`"$staging`"" -PassThru
  if (-not $helper.WaitForExit(60000)) { $helper.Kill(); Write-Host 'REHEARSAL FAIL: helper timeout'; exit 1 }
  Start-Sleep -Milliseconds 1500
  $ver = Get-Content (Join-Path $install 'VERSION.txt') -Raw
  $note = Get-Content (Join-Path $install 'user-note.txt') -Raw
  $leftover = Test-Path (Join-Path $install '.update-old')
  $launched = Get-Process Hikyaku -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq (Join-Path $install 'Hikyaku.exe') }
  $ok = ($helper.ExitCode -eq 0) -and ($ver -eq 'new') -and ($note -eq 'keep') -and (-not $leftover) -and $null -ne $launched
  Write-Host ("REHEARSAL {0} in {1} ms: exit={2} VERSION={3} userFile={4} rollbackLeft={5} relaunched={6}" -f ($(if ($ok) {'OK'} else {'FAIL'}), $sw.ElapsedMilliseconds, $helper.ExitCode, $ver, $note, $leftover, ($null -ne $launched)))
  if (-not $ok) { exit 1 }
} finally {
  Remove-Item Env:HIKYAKU_DATA_DIR -ErrorAction SilentlyContinue
  Get-Process Hikyaku -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$root*" } | Stop-Process -Force
  Start-Sleep -Milliseconds 500
  if (Test-Path $root) { Remove-Item $root -Recurse -Force -ErrorAction SilentlyContinue }
}
