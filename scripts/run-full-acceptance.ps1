$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$parityScript = Join-Path $PSScriptRoot "run-clean-rule-key-parity.ps1"
$ruleRegressionScript = Join-Path $PSScriptRoot "run-rule-level-regression.ps1"
$baselinePath = Join-Path $projectRoot "docs\regression-baseline.json"
$summaryPath = Join-Path $projectRoot "docs\rule-level-regression.json"
$parityPath = Join-Path $projectRoot "docs\rule-key-parity-clean.json"

if (-not (Test-Path $parityScript)) { throw "Missing script: $parityScript" }
if (-not (Test-Path $ruleRegressionScript)) { throw "Missing script: $ruleRegressionScript" }
if (-not (Test-Path $baselinePath)) { throw "Missing baseline: $baselinePath" }

Write-Output "[1/3] 规则键对账..."
& $parityScript | Out-Host
if (-not (Test-Path $parityPath)) { throw "Missing parity output: $parityPath" }

Write-Output "[2/3] 规则级回归..."
& $ruleRegressionScript | Out-Host
if (-not (Test-Path $summaryPath)) { throw "Missing regression output: $summaryPath" }

Write-Output "[3/3] 基线校验..."
$baseline = Get-Content -LiteralPath $baselinePath -Raw -Encoding UTF8 | ConvertFrom-Json
$summary = Get-Content -LiteralPath $summaryPath -Raw -Encoding UTF8 | ConvertFrom-Json
$parity = Get-Content -LiteralPath $parityPath -Raw -Encoding UTF8 | ConvertFrom-Json

$tolerance = [int]$baseline.tolerance
$baselineErrors = @()
foreach ($b in $baseline.samples) {
  $file = [string]$b.file
  $expected = [int]$b.issueCount
  $actual = $summary.samples | Where-Object { $_.file -eq $file } | Select-Object -First 1
  if ($null -eq $actual) {
    $baselineErrors += "缺少样本：$file"
    continue
  }
  $actualCount = [int]$actual.issueCount
  if ([Math]::Abs($actualCount - $expected) -gt $tolerance) {
    $baselineErrors += "$file 偏移：expected=$expected actual=$actualCount tolerance=$tolerance"
  }
}

$missingInNew = @($parity | Where-Object { $_.parity -eq "MISSING_IN_NEW" }).Count
$matched = @($parity | Where-Object { $_.parity -eq "MATCHED" }).Count
$onlyInNew = @($parity | Where-Object { $_.parity -eq "ONLY_IN_NEW" }).Count
$totalKeys = @($parity).Count

$acceptance = [PSCustomObject]@{
  generatedAt = (Get-Date).ToString("s")
  ruleKeyParity = [PSCustomObject]@{
    totalKeys = $totalKeys
    matched = $matched
    missingInNew = $missingInNew
    onlyInNew = $onlyInNew
    pass = ($missingInNew -eq 0)
  }
  baselineRegression = [PSCustomObject]@{
    tolerance = $tolerance
    errors = $baselineErrors
    pass = ($baselineErrors.Count -eq 0)
  }
  overallPass = (($missingInNew -eq 0) -and ($baselineErrors.Count -eq 0))
}

$jsonOut = Join-Path $projectRoot "docs\acceptance-summary.json"
$mdOut = Join-Path $projectRoot "docs\acceptance-summary.md"
$acceptance | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $jsonOut -Encoding UTF8

$lines = @()
$lines += "# Full Acceptance Summary"
$lines += ""
$lines += "- GeneratedAt: $($acceptance.generatedAt)"
$lines += "- OverallPass: $($acceptance.overallPass)"
$lines += ""
$lines += "## Rule Key Parity"
$lines += "- TotalKeys: $($acceptance.ruleKeyParity.totalKeys)"
$lines += "- Matched: $($acceptance.ruleKeyParity.matched)"
$lines += "- MissingInNew: $($acceptance.ruleKeyParity.missingInNew)"
$lines += "- OnlyInNew: $($acceptance.ruleKeyParity.onlyInNew)"
$lines += "- Pass: $($acceptance.ruleKeyParity.pass)"
$lines += ""
$lines += "## Baseline Regression"
$lines += "- Tolerance: $($acceptance.baselineRegression.tolerance)"
$lines += "- Pass: $($acceptance.baselineRegression.pass)"
if ($baselineErrors.Count -gt 0) {
  foreach ($e in $baselineErrors) {
    $lines += "- Error: $e"
  }
}
else {
  foreach ($s in $summary.samples) {
    $lines += "- $($s.file): issueCount=$($s.issueCount)"
  }
}

Set-Content -LiteralPath $mdOut -Value $lines -Encoding UTF8

Write-Output "Generated: $mdOut"
Write-Output "Generated: $jsonOut"
Write-Output "OverallPass: $($acceptance.overallPass)"
