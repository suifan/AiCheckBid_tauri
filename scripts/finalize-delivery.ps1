$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$sidecarProj = Join-Path $projectRoot "sidecar\DocParserSidecar\DocParserSidecar.csproj"
$net48Proj = Join-Path $projectRoot "sidecar\DocParserSidecarNet48\DocParserSidecarNet48.csproj"
$baselineScript = Join-Path $PSScriptRoot "check-regression-baseline.ps1"
$summaryMd = Join-Path $projectRoot "docs\final-regression-summary.md"
$summaryJson = Join-Path $projectRoot "docs\final-regression-summary.json"

if (-not (Test-Path $sidecarProj)) { throw "Missing project: $sidecarProj" }
if (-not (Test-Path $net48Proj)) { throw "Missing project: $net48Proj" }
if (-not (Test-Path $baselineScript)) { throw "Missing script: $baselineScript" }

Write-Output "[1/3] Build sidecar..."
dotnet build $sidecarProj -v minimal | Out-Host
$msbuildCandidates = @(
  "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe",
  "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
  "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
) | Where-Object { Test-Path $_ }
if ($msbuildCandidates.Count -eq 0) { throw "Missing MSBuild.exe for net48 sidecar build" }
$net48Built = $false
foreach ($msbuild in $msbuildCandidates) {
  & $msbuild $net48Proj /t:Build /p:Configuration=Debug /p:Platform=AnyCPU /v:minimal | Out-Host
  if ($LASTEXITCODE -eq 0) {
    $net48Built = $true
    break
  }
}
if (-not $net48Built) { throw "net48 sidecar build failed" }

Write-Output "[2/3] Run regression + baseline check..."
& $baselineScript | Out-Host

Write-Output "[3/3] Delivery artifacts..."
if (-not (Test-Path $summaryMd)) { throw "Missing summary: $summaryMd" }
if (-not (Test-Path $summaryJson)) { throw "Missing summary: $summaryJson" }

Write-Output "Done."
Write-Output "Summary (md): $summaryMd"
Write-Output "Summary (json): $summaryJson"
