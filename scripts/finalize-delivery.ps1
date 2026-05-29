$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$sidecarProj = Join-Path $projectRoot "sidecar\DocParserSidecar\DocParserSidecar.csproj"
$baselineScript = Join-Path $PSScriptRoot "check-regression-baseline.ps1"
$summaryMd = Join-Path $projectRoot "docs\final-regression-summary.md"
$summaryJson = Join-Path $projectRoot "docs\final-regression-summary.json"

if (-not (Test-Path $sidecarProj)) { throw "Missing project: $sidecarProj" }
if (-not (Test-Path $baselineScript)) { throw "Missing script: $baselineScript" }

Write-Output "[1/3] Build sidecar..."
dotnet build $sidecarProj -v minimal | Out-Host

Write-Output "[2/3] Run regression + baseline check..."
& $baselineScript | Out-Host

Write-Output "[3/3] Delivery artifacts..."
if (-not (Test-Path $summaryMd)) { throw "Missing summary: $summaryMd" }
if (-not (Test-Path $summaryJson)) { throw "Missing summary: $summaryJson" }

Write-Output "Done."
Write-Output "Summary (md): $summaryMd"
Write-Output "Summary (json): $summaryJson"
