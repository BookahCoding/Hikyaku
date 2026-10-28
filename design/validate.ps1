# Synthetic prototype validation — no network fetch of app resources.
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$report = [System.Collections.Generic.List[string]]::new()
$fail = 0

function Ok($m) { $report.Add("PASS: $m") }
function Bad($m) { $script:fail++; $report.Add("FAIL: $m") }

# 1. Required files
foreach ($f in @("index.html", "assets\styles.css", "assets\app.js", "README.md")) {
  if (Test-Path (Join-Path $root $f)) { Ok "exists $f" } else { Bad "missing $f" }
}

$html = Get-Content (Join-Path $root "index.html") -Raw
$css = Get-Content (Join-Path $root "assets\styles.css") -Raw
$js = Get-Content (Join-Path $root "assets\app.js") -Raw

# 2. No CDN / external script/link/img
$ext = [regex]::Matches($html, '(?i)(src|href)\s*=\s*"(https?:)?//[^"]+"')
if ($ext.Count -eq 0) { Ok "no external src/href protocol-relative or http(s) assets in index.html" }
else { Bad "external assets: $($ext.Value -join ', ')" }

$cdnHints = [regex]::Matches("$html$css$js", "(?i)(cdn\.|unpkg|jsdelivr|googleapis|cloudflare)")
if ($cdnHints.Count -eq 0) { Ok "no CDN host hints in html/css/js" }
else { Bad "CDN hints found" }

# 3. Local asset hrefs resolve
foreach ($m in [regex]::Matches($html, '(?i)(?:src|href)="([^"]+)"')) {
  $u = $m.Groups[1].Value
  if ($u.StartsWith("#") -or $u.StartsWith("mailto:")) { continue }
  if ($u -match '^(https?:)?//') { continue } # counted above
  $path = Join-Path $root ($u -replace "/", [IO.Path]::DirectorySeparatorChar)
  if ($u -eq "README.md" -or $u.StartsWith("../")) {
    if (Test-Path $path) { Ok "link resolves: $u" } else { Bad "broken link: $u" }
  } elseif (Test-Path $path) { Ok "asset resolves: $u" }
  else { Bad "broken asset: $u" }
}

# 4. Required views + scenario hooks present
foreach ($v in @("welcome","chat","bots","inbox","project","settings")) {
  if ($html -match "data-view=`"$v`"") { Ok "view $v" } else { Bad "missing view $v" }
}
foreach ($s in @("grant","diff","checkpoint","push","worktree","secret","needsyou","disconnected","loading","empty","error","profilehide")) {
  if ($html -match "data-scenario=`"$s`"") { Ok "scenario chip $s" } else { Bad "missing scenario $s" }
}

# 5. Synthetic labelling
if ($html -match "SYNTHETIC MOCKUP" -and $js -match "Simulated:") { Ok "synthetic labelling present" }
else { Bad "missing synthetic labelling" }

# 6. Simple/Advanced + reduced motion
if ($js -match "advanced" -and $css -match "prefers-reduced-motion") { Ok "Advanced toggle + reduced-motion CSS" }
else { Bad "missing Advanced or reduced-motion" }

# 7. Checkpoint vs Push separate controls
if ($html -match "btnCheckpoint" -and $html -match "btnPush") { Ok "checkpoint and push are separate controls" }
else { Bad "missing separate checkpoint/push" }

# 8. No fetch/XHR/WebSocket in prototype JS
if ($js -notmatch "(?i)\bfetch\s*\(|XMLHttpRequest|WebSocket|navigator\.sendBeacon") { Ok "no network APIs in app.js" }
else { Bad "network API usage in app.js" }

# 9. Interaction smoke via JSDOM-less HTML id inventory
foreach ($id in @("advancedToggle","btnGrantFolder","btnCheckpoint","btnPush","btnWorktree","confirmTerminal","needsBadge","terminalDrawer","panelHost")) {
  if ($html -match "id=`"$id`"") { Ok "control id $id" } else { Bad "missing id $id" }
}

$out = Join-Path $root "..\.superpowers\sdd\UX-RESEARCH-CHECKPOINT\prototype-validate.log"
# Do not write into .superpowers if we must preserve — checkpoint said preserve .superpowers untouched.
# Write beside design instead.
$out = Join-Path $root "validate-results.txt"
$report.Add("---")
$report.Add("RESULT: $(if ($fail -eq 0) { 'ALL PASS' } else { "$fail FAILURES" })")
$report | Set-Content -Encoding utf8 $out
$report | ForEach-Object { $_ }
exit $fail
