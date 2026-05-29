$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$runScript = Join-Path $PSScriptRoot "run-final-regression.ps1"
$baselinePath = Join-Path $projectRoot "docs\regression-baseline.json"
$summaryPath = Join-Path $projectRoot "docs\final-regression-summary.json"

if (-not (Test-Path $runScript)) { throw "Missing script: $runScript" }
if (-not (Test-Path $baselinePath)) { throw "Missing baseline: $baselinePath" }

& $runScript | Out-Null

if (-not (Test-Path $summaryPath)) { throw "Missing summary: $summaryPath" }

$baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json
$summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
$tol = [int]$baseline.tolerance

$errors = @()
foreach ($b in $baseline.samples) {
  $file = [string]$b.file
  $expected = [int]$b.issueCount
  $actualItem = $summary.samples | Where-Object { $_.file -eq $file } | Select-Object -First 1
  if ($null -eq $actualItem) {
    $errors += "Missing sample in summary: $file"
    continue
  }

  $actual = [int]$actualItem.issueCount
  if ([Math]::Abs($actual - $expected) -gt $tol) {
    $errors += "$file issueCount drifted: expected=$expected actual=$actual tolerance=$tol"
  }
}

if ($errors.Count -gt 0) {
  $errors | ForEach-Object { Write-Error $_ }
  exit 1
}

Write-Output "Baseline check passed."
