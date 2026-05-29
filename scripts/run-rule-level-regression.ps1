$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$sidecarExe = Join-Path $projectRoot "sidecar\DocParserSidecar\bin\Debug\net8.0\DocParserSidecar.exe"
$samples = @(
  "d:\work\ollama\xinhan.pdf",
  "d:\work\ollama\jialin.pdf"
)

if (-not (Test-Path $sidecarExe)) { throw "Sidecar EXE not found: $sidecarExe" }
foreach ($s in $samples) {
  if (-not (Test-Path $s)) { throw "Sample file not found: $s" }
}

function Get-RuleStats {
  param(
    [Parameter(Mandatory = $true)][string]$FilePath
  )

  $result = & $sidecarExe $FilePath | ConvertFrom-Json

  $ruleRows = @()
  if ($result.issues) {
    $ruleRows = $result.issues |
      Group-Object rule |
      Sort-Object -Property Count, Name -Descending |
      ForEach-Object {
        [PSCustomObject]@{
          rule  = [string]$_.Name
          count = [int]$_.Count
        }
      }
  }

  $categoryRows = @()
  if ($result.issues) {
    $categoryRows = $result.issues |
      Group-Object category |
      Sort-Object -Property Count, Name -Descending |
      ForEach-Object {
        [PSCustomObject]@{
          category = [string]$_.Name
          count    = [int]$_.Count
        }
      }
  }

  return [PSCustomObject]@{
    file         = [System.IO.Path]::GetFileName($FilePath)
    fullPath     = $FilePath
    issueCount   = [int]$result.issues.Count
    reportPath   = [string]$result.reportPath
    reportDocx   = [string]$result.reportDocxPath
    rules        = $ruleRows
    categories   = $categoryRows
  }
}

$data = @()
foreach ($sample in $samples) {
  $data += Get-RuleStats -FilePath $sample
}

$jsonOut = Join-Path $projectRoot "docs\rule-level-regression.json"
$payload = [PSCustomObject]@{
  generatedAt = (Get-Date).ToString("s")
  samples = $data
}
$payload | ConvertTo-Json -Depth 8 | Set-Content -Path $jsonOut -Encoding UTF8

$lines = @()
$lines += "# Rule Level Regression"
$lines += ""
$lines += "GeneratedAt: $($payload.generatedAt)"
$lines += ""
foreach ($s in $data) {
  $lines += "## $($s.file)"
  $lines += "- IssueCount: $($s.issueCount)"
  $lines += "- Report: $($s.reportPath)"
  if ($s.reportDocx) { $lines += "- ReportDocx: $($s.reportDocx)" }
  $lines += "- CategoryBreakdown:"
  foreach ($c in $s.categories) {
    $lines += "  - $($c.category): $($c.count)"
  }
  $lines += "- RuleBreakdown:"
  foreach ($r in $s.rules) {
    $lines += "  - $($r.rule): $($r.count)"
  }
  $lines += ""
}

$mdOut = Join-Path $projectRoot "docs\rule-level-regression.md"
Set-Content -Path $mdOut -Value $lines -Encoding UTF8

Write-Output "Generated: $mdOut"
Write-Output "Generated: $jsonOut"
