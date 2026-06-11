﻿$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$sidecarExe = Join-Path $projectRoot "sidecar\DocParserSidecarNet48\bin\Debug\DocParserSidecarNet48.exe"
$rulesPath = Join-Path $projectRoot "rules\set.ini"
$summaryMd = Join-Path $projectRoot "docs\docx-regression-summary.md"
$summaryJson = Join-Path $projectRoot "docs\docx-regression-summary.json"

if (-not (Test-Path $sidecarExe)) { throw "Sidecar EXE not found: $sidecarExe" }
if (-not (Test-Path $rulesPath)) { throw "Rules file not found: $rulesPath" }

function Get-TextByAutoEncoding {
  param([Parameter(Mandatory = $true)][string]$Path)

  $bytes = [System.IO.File]::ReadAllBytes($Path)
  if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
    return [System.Text.Encoding]::UTF8.GetString($bytes, 3, $bytes.Length - 3)
  }

  $utf8 = [System.Text.Encoding]::UTF8.GetString($bytes)
  if ($utf8.Contains([char]0xFFFD)) {
    return [System.Text.Encoding]::GetEncoding("GB18030").GetString($bytes)
  }

  return $utf8
}

function Write-Utf8BomText {
  param(
    [Parameter(Mandatory = $true)][string]$Path,
    [Parameter(Mandatory = $true)][string]$Content
  )

  [System.IO.File]::WriteAllText($Path, $Content, (New-Object System.Text.UTF8Encoding($true)))
}

function New-RulesFile {
  param(
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][scriptblock]$Mutator
  )

  $content = Get-TextByAutoEncoding -Path $rulesPath
  $mutated = & $Mutator $content
  Write-Utf8BomText -Path $OutputPath -Content $mutated
}

function New-DocxTextVariant {
  param(
    [Parameter(Mandatory = $true)][string]$TemplatePath,
    [Parameter(Mandatory = $true)][string]$OutputPath,
    [Parameter(Mandatory = $true)][scriptblock]$Mutator
  )

  Copy-Item -Force $TemplatePath $OutputPath
  Add-Type -AssemblyName System.IO.Compression
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zip = [System.IO.Compression.ZipFile]::Open($OutputPath, [System.IO.Compression.ZipArchiveMode]::Update)
  try {
    $entry = $zip.GetEntry("word/document.xml")
    if ($null -eq $entry) {
      throw "word/document.xml not found: $OutputPath"
    }

    $reader = New-Object System.IO.StreamReader($entry.Open())
    try {
      $xmlContent = $reader.ReadToEnd()
    }
    finally {
      $reader.Dispose()
    }

    $updatedContent = & $Mutator $xmlContent
    $entry.Delete()
    $newEntry = $zip.CreateEntry("word/document.xml")
    $writer = New-Object System.IO.StreamWriter($newEntry.Open(), (New-Object System.Text.UTF8Encoding($false)))
    try {
      $writer.Write($updatedContent)
    }
    finally {
      $writer.Dispose()
    }
  }
  finally {
    $zip.Dispose()
  }
}

function Get-FileSha256 {
  param([string]$Path)

  if (-not (Test-Path $Path)) {
    return $null
  }

  return (Get-FileHash $Path -Algorithm SHA256).Hash
}

function Invoke-DocxRegression {
  param(
    [Parameter(Mandatory = $true)][string]$Name,
    [Parameter(Mandatory = $true)][string]$InputPath,
    [Parameter(Mandatory = $true)][string]$OutputDir,
    [string]$RulesFile = $rulesPath,
    [hashtable]$Environment = @{},
    [bool]$CleanOutput = $true
  )

  if (-not (Test-Path $InputPath)) { throw "Sample file not found: $InputPath" }
  if (-not (Test-Path $RulesFile)) { throw "Rules file not found: $RulesFile" }

  if ($CleanOutput -and (Test-Path $OutputDir)) {
    Remove-Item -Recurse -Force $OutputDir
  }
  New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null

  $oldValues = @{}
  $env:AICHECKBID_RESULT_DIR = $OutputDir
  foreach ($key in $Environment.Keys) {
    $oldValues[$key] = [Environment]::GetEnvironmentVariable($key)
    [Environment]::SetEnvironmentVariable($key, [string]$Environment[$key])
  }

  try {
    $json = & $sidecarExe $InputPath $RulesFile | ConvertFrom-Json
  }
  finally {
    Remove-Item Env:\AICHECKBID_RESULT_DIR -ErrorAction SilentlyContinue
    foreach ($key in $Environment.Keys) {
      if ($null -eq $oldValues[$key]) {
        Remove-Item ("Env:\" + $key) -ErrorAction SilentlyContinue
      }
      else {
        [Environment]::SetEnvironmentVariable($key, $oldValues[$key])
      }
    }
  }

  $topRules = ($json.issues |
    Group-Object rule |
    Sort-Object Count -Descending |
    Select-Object -First 8 |
    ForEach-Object { "{0}: {1}" -f $_.Name, $_.Count }) -join ", "

  [PSCustomObject]@{
    name = $Name
    input = $InputPath
    issueCount = [int]$json.issues.Count
    parser = [string]$json.parser
    reportPath = [string]$json.reportPath
    reportDocxPath = [string]$json.reportDocxPath
    topRules = $topRules
    outputDir = $OutputDir
  }
}

$sampleOutDir = Join-Path $projectRoot "tmp_regression_sample"
$techOutDir = Join-Path $projectRoot "tmp_regression_tech"
$smartFixOutDir = Join-Path $projectRoot "tmp_regression_smartfix"
$smartFixRecheckOutDir = Join-Path $projectRoot "tmp_regression_smartfix_recheck"
$textCheckOutDir = Join-Path $projectRoot "tmp_regression_textcheck"
$batchOutDir = Join-Path $projectRoot "tmp_regression_batch"
$tempRulesDir = Join-Path $projectRoot "tmp_regression_rules"
New-Item -ItemType Directory -Force -Path $tempRulesDir | Out-Null

$smartFixRules = Join-Path $tempRulesDir "set-smartfix.ini"
New-RulesFile -OutputPath $smartFixRules -Mutator {
  param($content)
  [System.Text.RegularExpressions.Regex]::Replace(
    $content,
    '(?m)^智能修正\s*=\s*False\s*$',
    '智能修正 = True'
  )
}

$textCheckRules = Join-Path $tempRulesDir "set-textcheck.ini"
$textCheckSample = Join-Path $tempRulesDir "sample-textcheck.docx"

New-DocxTextVariant -TemplatePath (Join-Path $projectRoot "sample.docx") -OutputPath $textCheckSample -Mutator {
  param($xmlContent)
  $xmlContent.Replace("Hello AiCheckBid", "Hello! AiCheckBid")
}

New-RulesFile -OutputPath $textCheckRules -Mutator {
  param($content)
  $content = [System.Text.RegularExpressions.Regex]::Replace($content, '(?m)^输出页码\s*=\s*False\s*$', '输出页码 = True')
  $content = [System.Text.RegularExpressions.Regex]::Replace($content, '(?m)^敏感词词典\s*=\s*$', '敏感词词典 = Hello')
  $content = [System.Text.RegularExpressions.Regex]::Replace($content, '(?m)^地名词典\s*=\s*$', '地名词典 = 测试文档')
  $content = [System.Text.RegularExpressions.Regex]::Replace($content, '(?m)^公司名词典\s*=\s*$', '公司名词典 = AiCheckBid')
  $content = [System.Text.RegularExpressions.Regex]::Replace($content, '(?m)^标点符号\s*=\s*$', '标点符号 = !')
  return $content
}

$sample = Invoke-DocxRegression `
  -Name "sample.docx" `
  -InputPath (Join-Path $projectRoot "sample.docx") `
  -OutputDir $sampleOutDir

$tech = Invoke-DocxRegression `
  -Name "技术暗标表格调整版m.docx" `
  -InputPath (Join-Path $projectRoot "result_net48_verify\技术暗标表格调整版m.docx") `
  -OutputDir $techOutDir

$smartFix = Invoke-DocxRegression `
  -Name "技术暗标表格调整版m.docx-smartfix" `
  -InputPath (Join-Path $projectRoot "tmp_regression_tech\技术暗标表格调整版mm.docx") `
  -OutputDir $smartFixOutDir `
  -RulesFile $smartFixRules

$smartFixSourceCopy = Join-Path $smartFixOutDir "技术暗标表格调整版mmm.docx"
$smartFixRecheck = $null
if (Test-Path $smartFixSourceCopy) {
  $smartFixRecheck = Invoke-DocxRegression `
    -Name "技术暗标表格调整版m.docx-smartfix-recheck" `
    -InputPath $smartFixSourceCopy `
    -OutputDir $smartFixRecheckOutDir
}

$textCheck = Invoke-DocxRegression `
  -Name "sample.docx-textcheck" `
  -InputPath $textCheckSample `
  -OutputDir $textCheckOutDir `
  -RulesFile $textCheckRules

if (Test-Path $batchOutDir) {
  Remove-Item -Recurse -Force $batchOutDir
}
New-Item -ItemType Directory -Force -Path $batchOutDir | Out-Null

$null = Invoke-DocxRegression `
  -Name "batch-1" `
  -InputPath (Join-Path $projectRoot "sample.docx") `
  -OutputDir $batchOutDir `
  -Environment @{ AICHECKBID_BATCH_SERIAL = "1"; AICHECKBID_OVERVIEW_MODE = "replace" } `
  -CleanOutput $false

$null = Invoke-DocxRegression `
  -Name "batch-2" `
  -InputPath (Join-Path $projectRoot "tmp_regression_tech\技术暗标表格调整版mm.docx") `
  -OutputDir $batchOutDir `
  -Environment @{ AICHECKBID_BATCH_SERIAL = "2"; AICHECKBID_OVERVIEW_MODE = "append" } `
  -CleanOutput $false

$techChecks = @(
  [PSCustomObject]@{
    file = "1的格式检查结果.txt"
    baseline = Join-Path $projectRoot "result_net48_verify\1的格式检查结果.txt"
    actual = Join-Path $techOutDir "1的格式检查结果.txt"
  },
  [PSCustomObject]@{
    file = "1的其他检查结果.txt"
    baseline = Join-Path $projectRoot "result_net48_verify\1的其他检查结果.txt"
    actual = Join-Path $techOutDir "1的其他检查结果.txt"
  }
) | ForEach-Object {
  $baselineHash = Get-FileSha256 $_.baseline
  $actualHash = Get-FileSha256 $_.actual
  [PSCustomObject]@{
    file = $_.file
    baselineHash = $baselineHash
    actualHash = $actualHash
    matches = ($baselineHash -and $actualHash -and $baselineHash -eq $actualHash)
  }
}

$textHashChecks = @(
  "1的公司名检查结果.txt",
  "1的地名检查结果.txt",
  "1的敏感词检查结果.txt",
  "1的标点符号检查结果.txt",
  "1的其他检查结果.txt"
) | ForEach-Object {
  [PSCustomObject]@{
    file = $_
    actualHash = Get-FileSha256 (Join-Path $textCheckOutDir $_)
  }
}

$overviewPath = Join-Path $batchOutDir "检查结果概要.txt"
$overviewText = if (Test-Path $overviewPath) { Get-TextByAutoEncoding -Path $overviewPath } else { "" }
$overviewLines = @($overviewText -split "`r?`n" | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })

$batchArtifacts = @(
  "检查结果-samplem.docx",
  "检查结果-技术暗标表格调整版mmm.docx",
  "samplem.docx",
  "技术暗标表格调整版mmm.docx"
) | ForEach-Object {
  [PSCustomObject]@{
    file = $_
    exists = Test-Path (Join-Path $batchOutDir $_)
  }
}

$batchContentChecks = @(
  [PSCustomObject]@{
    file = "1的格式检查结果.txt"
    contains = "测试文档 Hello AiCheckBid"
  },
  [PSCustomObject]@{
    file = "2的格式检查结果.txt"
    contains = "现场观摩区是公众直观了解处置过程的核心区域"
  },
  [PSCustomObject]@{
    file = "1的其他检查结果.txt"
    contains = "测试文档 Hello AiCheckBid"
  },
  [PSCustomObject]@{
    file = "2的其他检查结果.txt"
    contains = "一级保障（必保）"
  },
  [PSCustomObject]@{
    file = "1的标点符号检查结果.txt"
    contains = "未发现问题。"
  },
  [PSCustomObject]@{
    file = "2的标点符号检查结果.txt"
    contains = "未发现问题。"
  }
) | ForEach-Object {
  $path = Join-Path $batchOutDir $_.file
  $text = if (Test-Path $path) { Get-TextByAutoEncoding -Path $path } else { "" }
  [PSCustomObject]@{
    file = $_.file
    contains = $_.contains
    matches = ($text -like ("*" + $_.contains + "*"))
  }
}

$smartFixScenario = [PSCustomObject]@{
  name = $smartFix.name
  issueCount = $smartFix.issueCount
  parser = $smartFix.parser
  reportDocxExists = (Test-Path $smartFix.reportDocxPath)
  sourceCopyExists = (Test-Path $smartFixSourceCopy)
  sourceCopyHash = Get-FileSha256 $smartFixSourceCopy
  sourceCopyRecheckIssueCount = if ($null -ne $smartFixRecheck) { $smartFixRecheck.issueCount } else { $null }
}

$textCheckScenario = [PSCustomObject]@{
  name = $textCheck.name
  issueCount = $textCheck.issueCount
  parser = $textCheck.parser
  hashChecks = $textHashChecks
}

$batchScenario = [PSCustomObject]@{
  overviewLineCount = $overviewLines.Count
  overviewContains = @(
    "samplem",
    "技术暗标表格调整版mmm"
  ) | ForEach-Object {
    [PSCustomObject]@{
      value = $_
      exists = ($overviewText -like ("*" + $_ + "*"))
    }
  }
  artifacts = $batchArtifacts
  contentChecks = $batchContentChecks
}

$lines = @(
  "# DOCX Regression Summary",
  "",
  ("GeneratedAt: {0}" -f (Get-Date).ToString('s')),
  "",
  "## sample.docx",
  ("- IssueCount: {0}" -f $sample.issueCount),
  ("- Parser: {0}" -f $sample.parser),
  ("- Report: {0}" -f $sample.reportPath),
  ("- ReportDocx: {0}" -f $sample.reportDocxPath),
  ("- TopRules: {0}" -f $sample.topRules),
  "",
  "## 技术暗标表格调整版m.docx",
  ("- IssueCount: {0}" -f $tech.issueCount),
  ("- Parser: {0}" -f $tech.parser),
  ("- Report: {0}" -f $tech.reportPath),
  ("- ReportDocx: {0}" -f $tech.reportDocxPath),
  ("- TopRules: {0}" -f $tech.topRules),
  "",
  "## 技术暗标关键文件哈希",
  ""
)

foreach ($item in $techChecks) {
  $lines += ("- {0}: Match={1} Baseline={2} Actual={3}" -f $item.file, $item.matches, $item.baselineHash, $item.actualHash)
}

$lines += @(
  "",
  "## 智能修正场景",
  ("- IssueCount: {0}" -f $smartFixScenario.issueCount),
  ("- Parser: {0}" -f $smartFixScenario.parser),
  ("- ReportDocxExists: {0}" -f $smartFixScenario.reportDocxExists),
  ("- SourceCopyExists: {0}" -f $smartFixScenario.sourceCopyExists),
  ("- SourceCopyRecheckIssueCount: {0}" -f $smartFixScenario.sourceCopyRecheckIssueCount),
  ("- SourceCopyHash: {0}" -f $smartFixScenario.sourceCopyHash),
  "",
  "## 文本类检查场景",
  ("- IssueCount: {0}" -f $textCheckScenario.issueCount),
  ("- Parser: {0}" -f $textCheckScenario.parser),
  ""
)

foreach ($item in $textHashChecks) {
  $lines += ("- {0}: Hash={1}" -f $item.file, $item.actualHash)
}

$lines += @(
  "",
  "## 批量结果场景",
  ("- OverviewLineCount: {0}" -f $batchScenario.overviewLineCount),
  ""
)

foreach ($item in $batchScenario.overviewContains) {
  $lines += ("- OverviewContains {0}: {1}" -f $item.value, $item.exists)
}
foreach ($item in $batchScenario.artifacts) {
  $lines += ("- Artifact {0}: {1}" -f $item.file, $item.exists)
}
foreach ($item in $batchScenario.contentChecks) {
  $lines += ("- BatchContent {0}: {1} => {2}" -f $item.file, $item.matches, $item.contains)
}

Set-Content -Path $summaryMd -Value $lines -Encoding UTF8

$jsonData = [PSCustomObject]@{
  generatedAt = (Get-Date).ToString('s')
  samples = @($sample, $tech)
  techHashChecks = $techChecks
  smartFixScenario = $smartFixScenario
  textCheckScenario = $textCheckScenario
  batchScenario = $batchScenario
}
$jsonData | ConvertTo-Json -Depth 8 | Set-Content -Path $summaryJson -Encoding UTF8

Write-Output ("Generated: {0}" -f $summaryMd)
Write-Output ("Generated: {0}" -f $summaryJson)
