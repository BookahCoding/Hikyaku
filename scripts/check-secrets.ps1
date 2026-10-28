# Repeatable secret / private-data check over git-tracked and staged-new files.
# Heuristic only: it cannot prove absence of private data. Exit code 1 on any finding.
# Findings print category + path only; matched secret values are never printed.
# Approved public credit email is allowed only in deliberate LICENSE / README / CREDITS paths.
param([string]$Root = (Split-Path $PSScriptRoot -Parent))
Set-Location $Root
$files = @(git ls-files) + @(git ls-files --others --exclude-standard) | Sort-Object -Unique
$patterns = [ordered]@{
  'private key block'     = '-----BEGIN [A-Z ]*PRIVATE KEY-----'
  'bearer/api token'      = '(?i)(sk-[A-Za-z0-9]{20,}|ghp_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|xox[baprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16})'
  'assigned secret'       = '(?i)(password|passwd|secret|api[_-]?key|token)\s*[:=]\s*["''][^"'']{8,}["'']'
  'email address'         = '[A-Za-z0-9._%+-]+@(?!users\.noreply\.github\.com)[A-Za-z0-9.-]+\.[A-Za-z]{2,}'
  'private IPv4'          = '\b(10\.\d{1,3}\.\d{1,3}\.\d{1,3}|192\.168\.\d{1,3}\.\d{1,3}|172\.(1[6-9]|2\d|3[01])\.\d{1,3}\.\d{1,3})\b'
  'user profile path'     = '(?i)[A-Z]:[\\/]+Users[\\/]+[^\\/\s<>"'']+'
  'personal project path' = '(?i)\b[A-Z]:[\\/]+Projects[\\/]'
  'tailnet/local host'    ='(?i)\b[a-z0-9-]+\.(ts\.net|lan|home\.arpa)\b|\b[a-z0-9-]+\.local\b(?!\.)'
}
$forbiddenNames = '(?i)(^|/)(\.env(\..*)?|.*\.(pfx|snk|key|pem|log|dmp))$'
$creditEmail = 'bookahgamer@gmail.com'
$creditPaths = @('LICENSE', 'README.md', 'docs/CREDITS.md')
$bad = 0
foreach ($f in $files) {
  if (-not (Test-Path $f -PathType Leaf)) { continue }
  if ($f -match $forbiddenNames -and $f -notmatch '\.env\.example$') { Write-Host "FORBIDDEN FILE: $f"; $bad++; continue }
  if ($f -like 'scripts/check-secrets.ps1') { continue } # contains the patterns themselves
  $text = Get-Content $f -Raw -ErrorAction SilentlyContinue
  if (-not $text) { continue }
  $norm = ($f -replace '\\', '/').TrimStart('./')
  $isCreditDoc = $creditPaths -contains $norm
  foreach ($k in $patterns.Keys) {
    if ($text -match $patterns[$k]) {
      if ($k -eq 'email address' -and $isCreditDoc) {
        # Only the approved public credit identity may appear in deliberate credit docs.
        $emails = [regex]::Matches($text, $patterns[$k]) | ForEach-Object { $_.Value }
        $foreign = $emails | Where-Object { $_.ToLowerInvariant() -ne $creditEmail }
        if (-not $foreign) { continue }
      }
      Write-Host "[$k] in $f"
      $bad++
    }
  }
}
# Commit messages are public too: a link to a private session transcript must not ship in them.
$sessionLinks = @(git log --all --format=%B 2>$null | Select-String -Pattern 'claude\.ai/code/session|^Claude-Session:')
if ($sessionLinks.Count -gt 0) { Write-Host "[session-link] in $($sessionLinks.Count) commit message line(s)"; $bad++ }
if ($bad) { Write-Host "check-secrets: $bad finding(s) (values redacted; category+path only)"; exit 1 }
Write-Host "check-secrets: clean ($($files.Count) files scanned; heuristic, not a proof)"
