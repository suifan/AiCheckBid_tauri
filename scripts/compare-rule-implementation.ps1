$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$oldQ = "d:\work\ollama\AiCheckBid\q.cs"
$newNet48 = Join-Path $projectRoot "sidecar\DocParserSidecarNet48\Program.cs"
$newNet8 = Join-Path $projectRoot "sidecar\DocParserSidecar\Program.cs"

if (-not (Test-Path $oldQ)) { throw "Missing old q.cs: $oldQ" }
if (-not (Test-Path $newNet48)) { throw "Missing net48 Program.cs: $newNet48" }
if (-not (Test-Path $newNet8)) { throw "Missing net8 Program.cs: $newNet8" }

$oldText = Get-Content -LiteralPath $oldQ -Raw -Encoding UTF8
$net48Text = Get-Content -LiteralPath $newNet48 -Raw -Encoding UTF8
$net8Text = Get-Content -LiteralPath $newNet8 -Raw -Encoding UTF8

$rows = @()
$oldRules = New-Object System.Collections.Generic.HashSet[string]
foreach ($m in [regex]::Matches($oldText, '\["([^"]+)"\]\["([^"]+)"\]')) {
  $sec = $m.Groups[1].Value
  $key = $m.Groups[2].Value
  if ([string]::IsNullOrWhiteSpace($sec) -or [string]::IsNullOrWhiteSpace($key)) { continue }
  if ($sec -notmatch '\p{IsCJKUnifiedIdeographs}') { continue }
  if ($key -notmatch '\p{IsCJKUnifiedIdeographs}') { continue }
  [void]$oldRules.Add("$sec::$key")
}

$newRules = New-Object System.Collections.Generic.HashSet[string]
foreach ($m in [regex]::Matches($net48Text, 'rules\.Get(?:Bool)?\("([^"]+)",\s*"([^"]+)"')) {
  $sec = $m.Groups[1].Value
  $key = $m.Groups[2].Value
  if ($sec -notmatch '\p{IsCJKUnifiedIdeographs}' -or $key -notmatch '\p{IsCJKUnifiedIdeographs}') { continue }
  [void]$newRules.Add(($sec + "::" + $key))
}
foreach ($m in [regex]::Matches($net8Text, 'rules\.Get(?:Bool)?\("([^"]+)",\s*"([^"]+)"')) {
  $sec = $m.Groups[1].Value
  $key = $m.Groups[2].Value
  if ($sec -notmatch '\p{IsCJKUnifiedIdeographs}' -or $key -notmatch '\p{IsCJKUnifiedIdeographs}') { continue }
  [void]$newRules.Add(($sec + "::" + $key))
}

$all = New-Object System.Collections.Generic.HashSet[string]
foreach ($r in $oldRules) { [void]$all.Add($r) }
foreach ($r in $newRules) { [void]$all.Add($r) }

foreach ($ruleId in $all) {
  $parts = $ruleId.Split("::")
  $sec = $parts[0]
  $key = $parts[1]
  $oldHit = $oldRules.Contains($ruleId)
  $newHit = $newRules.Contains($ruleId)
  $n48Hit = $net48Text -match [regex]::Escape($key)
  $n8Hit = $net8Text -match [regex]::Escape($key)
  $rows += [PSCustomObject]@{
    section = $sec
    rule = $key
    oldImplemented = [bool]$oldHit
    newImplemented = [bool]$newHit
    net48 = [bool]$n48Hit
    net8 = [bool]$n8Hit
    parity = if ($oldHit -and $newHit) { "MATCHED" } elseif ($oldHit -and -not $newHit) { "MISSING_IN_NEW" } elseif (-not $oldHit -and $newHit) { "ONLY_IN_NEW" } else { "NO_SIGNAL" }
  }
}

$jsonPath = Join-Path $projectRoot "docs\rule-implementation-matrix.json"
$rows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $jsonPath -Encoding UTF8

$summary = [PSCustomObject]@{
  total = $rows.Count
  matched = ($rows | Where-Object { $_.parity -eq "MATCHED" }).Count
  missingInNew = ($rows | Where-Object { $_.parity -eq "MISSING_IN_NEW" }).Count
  onlyInNew = ($rows | Where-Object { $_.parity -eq "ONLY_IN_NEW" }).Count
  noSignal = ($rows | Where-Object { $_.parity -eq "NO_SIGNAL" }).Count
}

$md = @()
$md += "# Rule Implementation Matrix"
$md += ""
$md += "- TotalRules: $($summary.total)"
$md += "- Matched: $($summary.matched)"
$md += "- MissingInNew: $($summary.missingInNew)"
$md += "- OnlyInNew: $($summary.onlyInNew)"
$md += "- NoSignal: $($summary.noSignal)"
$md += ""

$missing = $rows | Where-Object { $_.parity -eq "MISSING_IN_NEW" } | Sort-Object section, rule
$onlyNew = $rows | Where-Object { $_.parity -eq "ONLY_IN_NEW" } | Sort-Object section, rule

$md += "## Missing In New"
if ($missing.Count -eq 0) {
  $md += "- None"
} else {
  foreach ($r in $missing) { $md += "- [$($r.section)] $($r.rule)" }
}
$md += ""

$md += "## Only In New"
if ($onlyNew.Count -eq 0) {
  $md += "- None"
} else {
  foreach ($r in $onlyNew) { $md += "- [$($r.section)] $($r.rule)" }
}
$md += ""

$md += "## Detailed"
foreach ($r in ($rows | Sort-Object section, rule)) {
  $md += "- [$($r.section)] $($r.rule): old=$($r.oldImplemented), new=$($r.newImplemented), net48=$($r.net48), net8=$($r.net8), parity=$($r.parity)"
}

$mdPath = Join-Path $projectRoot "docs\rule-implementation-matrix.md"
Set-Content -LiteralPath $mdPath -Value $md -Encoding UTF8

Write-Output "Generated: $mdPath"
Write-Output "Generated: $jsonPath"
