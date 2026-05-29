$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $PSScriptRoot
$ini = Join-Path $root "rules\set.ini"
$old = "d:\work\ollama\AiCheckBid\q.cs"
$new48 = Join-Path $root "sidecar\DocParserSidecarNet48\Program.cs"
$new8 = Join-Path $root "sidecar\DocParserSidecar\Program.cs"

$iniLines = Get-Content -LiteralPath $ini -Encoding UTF8
$oldText = Get-Content -LiteralPath $old -Raw -Encoding UTF8
$n48 = Get-Content -LiteralPath $new48 -Raw -Encoding UTF8
$n8 = Get-Content -LiteralPath $new8 -Raw -Encoding UTF8

$rows = @()
$sec = ""

foreach ($line in $iniLines) {
  $t = $line.Trim()
  if ($t -match '^\[(.+)\]$') {
    $sec = $matches[1]
    continue
  }
  if ([string]::IsNullOrWhiteSpace($t) -or $t.StartsWith(";")) { continue }
  if ($t -notmatch '^([^=]+)=') { continue }

  $key = $matches[1].Trim()
  $oldHit = $oldText -match [regex]::Escape('this.s["' + $sec + '"]["' + $key + '"]')

  $n48Direct = $n48 -match [regex]::Escape('rules.Get("' + $sec + '", "' + $key + '"') `
    -or $n48 -match [regex]::Escape('rules.GetBool("' + $sec + '", "' + $key + '"') `
    -or $n48 -match [regex]::Escape('rules.GetFloat("' + $sec + '", "' + $key + '"')

  $n8Direct = $n8 -match [regex]::Escape('rules.Get("' + $sec + '", "' + $key + '"') `
    -or $n8 -match [regex]::Escape('rules.GetBool("' + $sec + '", "' + $key + '"') `
    -or $n8 -match [regex]::Escape('rules.GetFloat("' + $sec + '", "' + $key + '"')

  $titleGeneric = $n48 -match [regex]::Escape('rules.Get(titleSection, "' + $key + '"') `
    -or $n48 -match [regex]::Escape('rules.GetBool(titleSection, "' + $key + '"') `
    -or $n48 -match [regex]::Escape('rules.GetFloat(titleSection, "' + $key + '"') `
    -or (($n8 -match [regex]::Escape('rules.Get(section, "' + $key + '"') `
      -or $n8 -match [regex]::Escape('rules.GetBool(section, "' + $key + '"') `
      -or $n8 -match [regex]::Escape('rules.GetFloat(section, "' + $key + '"')) -and $n8 -match "GetHeadingSectionByLevel")

  $pageMarginGeneric = $n48 -match [regex]::Escape('CheckMarginIssue(rules, issues, "' + $key + '"') `
    -or $n8 -match [regex]::Escape('CheckMargin("' + $key + '"')

  $newHit = $n48Direct -or $n8Direct -or $titleGeneric -or $pageMarginGeneric
  $parity = "UNUSED_BOTH"
  if ($oldHit -and $newHit) { $parity = "MATCHED" }
  elseif ($oldHit -and -not $newHit) { $parity = "MISSING_IN_NEW" }
  elseif (-not $oldHit -and $newHit) { $parity = "ONLY_IN_NEW" }

  $rows += [PSCustomObject]@{
    section = $sec
    key = $key
    oldImplemented = [bool]$oldHit
    newImplemented = [bool]$newHit
    newNet48 = [bool]$n48Direct
    newNet8 = [bool]$n8Direct
    titleGeneric = [bool]$titleGeneric
    parity = $parity
  }
}

$json = Join-Path $root "docs\rule-key-parity-clean.json"
$md = Join-Path $root "docs\rule-key-parity-clean.md"
$rows | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $json -Encoding UTF8

$total = $rows.Count
$matched = ($rows | Where-Object { $_.parity -eq "MATCHED" }).Count
$missing = $rows | Where-Object { $_.parity -eq "MISSING_IN_NEW" }
$onlyNew = $rows | Where-Object { $_.parity -eq "ONLY_IN_NEW" }
$unused = ($rows | Where-Object { $_.parity -eq "UNUSED_BOTH" }).Count

$lines = @()
$lines += "# Clean Rule Key Parity"
$lines += ""
$lines += "- TotalKeys: $total"
$lines += "- Matched: $matched"
$lines += "- MissingInNew: $($missing.Count)"
$lines += "- OnlyInNew: $($onlyNew.Count)"
$lines += "- UnusedBoth: $unused"
$lines += ""
$lines += "## Missing In New"
if ($missing.Count -eq 0) {
  $lines += "- None"
}
else {
  foreach ($r in ($missing | Sort-Object section, key)) {
    $lines += "- [$($r.section)] $($r.key)"
  }
}
$lines += ""
$lines += "## Only In New"
if ($onlyNew.Count -eq 0) {
  $lines += "- None"
}
else {
  foreach ($r in ($onlyNew | Sort-Object section, key)) {
    $lines += "- [$($r.section)] $($r.key)"
  }
}
$lines += ""
$lines += "## Detailed"
foreach ($r in ($rows | Sort-Object section, key)) {
  $lines += "- [$($r.section)] $($r.key): old=$($r.oldImplemented), new=$($r.newImplemented), n48=$($r.newNet48), n8=$($r.newNet8), titleGeneric=$($r.titleGeneric), parity=$($r.parity)"
}

Set-Content -LiteralPath $md -Value $lines -Encoding UTF8
Write-Output "Generated: $md"
Write-Output "Generated: $json"
