$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$sidecarDll = Join-Path $projectRoot "sidecar\DocParserSidecar\bin\Debug\net8.0\DocParserSidecar.dll"
$xinhan = "d:\work\ollama\xinhan.pdf"
$jialin = "d:\work\ollama\jialin.pdf"

if (-not (Test-Path $sidecarDll)) { throw "Sidecar DLL not found: $sidecarDll" }
if (-not (Test-Path $xinhan)) { throw "Sample file not found: $xinhan" }
if (-not (Test-Path $jialin)) { throw "Sample file not found: $jialin" }

$a = dotnet $sidecarDll $xinhan | ConvertFrom-Json
$b = dotnet $sidecarDll $jialin | ConvertFrom-Json

$aTop = ($a.issues | Group-Object rule | Sort-Object Count -Descending | Select-Object -First 8 | ForEach-Object { "$($_.Name): $($_.Count)" }) -join ", "
$bTop = ($b.issues | Group-Object rule | Sort-Object Count -Descending | Select-Object -First 8 | ForEach-Object { "$($_.Name): $($_.Count)" }) -join ", "

$lines = @(
  "# Final Regression Summary",
  "",
  "## xinhan.pdf",
  "- IssueCount: $($a.issues.Count)",
  "- Report: $($a.reportPath)",
  "- TopRules: $aTop",
  "",
  "## jialin.pdf",
  "- IssueCount: $($b.issues.Count)",
  "- Report: $($b.reportPath)",
  "- TopRules: $bTop",
  ""
)

$out = Join-Path $projectRoot "docs\final-regression-summary.md"
Set-Content -Path $out -Value $lines -Encoding UTF8

$jsonOut = Join-Path $projectRoot "docs\final-regression-summary.json"
$jsonData = [PSCustomObject]@{
  generatedAt = (Get-Date).ToString("s")
  samples = @(
    [PSCustomObject]@{
      file = "xinhan.pdf"
      issueCount = [int]$a.issues.Count
      report = $a.reportPath
      topRules = $aTop
    },
    [PSCustomObject]@{
      file = "jialin.pdf"
      issueCount = [int]$b.issues.Count
      report = $b.reportPath
      topRules = $bTop
    }
  )
}
$jsonData | ConvertTo-Json -Depth 6 | Set-Content -Path $jsonOut -Encoding UTF8

Write-Output "Generated: $out"
Write-Output "Generated: $jsonOut"
