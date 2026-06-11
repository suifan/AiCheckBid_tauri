﻿$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$runScript = Join-Path $PSScriptRoot "run-docx-regression.ps1"
$baselinePath = Join-Path $projectRoot "docs\docx-regression-baseline.json"
$summaryPath = Join-Path $projectRoot "docs\docx-regression-summary.json"

if (-not (Test-Path $runScript)) { throw "Missing script: $runScript" }
if (-not (Test-Path $baselinePath)) { throw "Missing baseline: $baselinePath" }

& $runScript | Out-Null

if (-not (Test-Path $summaryPath)) { throw "Missing summary: $summaryPath" }

$baseline = Get-Content $baselinePath -Raw | ConvertFrom-Json
$summary = Get-Content $summaryPath -Raw | ConvertFrom-Json
$errors = @()

foreach ($item in $baseline.samples) {
  $actual = $summary.samples | Where-Object { $_.name -eq $item.name } | Select-Object -First 1
  if ($null -eq $actual) {
    $errors += ("Missing sample in summary: {0}" -f $item.name)
    continue
  }

  if ([int]$actual.issueCount -ne [int]$item.issueCount) {
    $errors += ("{0} issueCount drifted: expected={1} actual={2}" -f $item.name, $item.issueCount, $actual.issueCount)
  }

  if ([string]$actual.parser -ne [string]$item.parser) {
    $errors += ("{0} parser drifted: expected={1} actual={2}" -f $item.name, $item.parser, $actual.parser)
  }
}

foreach ($item in $baseline.techHashChecks) {
  $actual = $summary.techHashChecks | Where-Object { $_.file -eq $item.file } | Select-Object -First 1
  if ($null -eq $actual) {
    $errors += ("Missing tech hash check: {0}" -f $item.file)
    continue
  }

  if (-not [bool]$actual.matches) {
    $errors += ("{0} hash mismatch: actual={1}" -f $item.file, $actual.actualHash)
    continue
  }

  if ([string]$actual.actualHash -ne [string]$item.hash) {
    $errors += ("{0} hash drifted: expected={1} actual={2}" -f $item.file, $item.hash, $actual.actualHash)
  }
}

if ($baseline.smartFixScenario) {
  $actual = $summary.smartFixScenario
  if ($null -eq $actual) {
    $errors += "Missing smartFixScenario in summary"
  }
  else {
    if ([int]$actual.issueCount -ne [int]$baseline.smartFixScenario.issueCount) {
      $errors += ("smartFixScenario issueCount drifted: expected={0} actual={1}" -f $baseline.smartFixScenario.issueCount, $actual.issueCount)
    }
    if ([string]$actual.parser -ne [string]$baseline.smartFixScenario.parser) {
      $errors += ("smartFixScenario parser drifted: expected={0} actual={1}" -f $baseline.smartFixScenario.parser, $actual.parser)
    }
    if ([bool]$actual.reportDocxExists -ne [bool]$baseline.smartFixScenario.reportDocxExists) {
      $errors += ("smartFixScenario reportDocxExists drifted: expected={0} actual={1}" -f $baseline.smartFixScenario.reportDocxExists, $actual.reportDocxExists)
    }
    if ([bool]$actual.sourceCopyExists -ne [bool]$baseline.smartFixScenario.sourceCopyExists) {
      $errors += ("smartFixScenario sourceCopyExists drifted: expected={0} actual={1}" -f $baseline.smartFixScenario.sourceCopyExists, $actual.sourceCopyExists)
    }
    if ($baseline.smartFixScenario.PSObject.Properties.Name -contains "sourceCopyRecheckIssueCount") {
      if ([int]$actual.sourceCopyRecheckIssueCount -ne [int]$baseline.smartFixScenario.sourceCopyRecheckIssueCount) {
        $errors += ("smartFixScenario sourceCopyRecheckIssueCount drifted: expected={0} actual={1}" -f $baseline.smartFixScenario.sourceCopyRecheckIssueCount, $actual.sourceCopyRecheckIssueCount)
      }
    }
    elseif ([string]$actual.sourceCopyHash -ne [string]$baseline.smartFixScenario.sourceCopyHash) {
      $errors += ("smartFixScenario sourceCopyHash drifted: expected={0} actual={1}" -f $baseline.smartFixScenario.sourceCopyHash, $actual.sourceCopyHash)
    }
  }
}

if ($baseline.textCheckScenario) {
  $actual = $summary.textCheckScenario
  if ($null -eq $actual) {
    $errors += "Missing textCheckScenario in summary"
  }
  else {
    if ([int]$actual.issueCount -ne [int]$baseline.textCheckScenario.issueCount) {
      $errors += ("textCheckScenario issueCount drifted: expected={0} actual={1}" -f $baseline.textCheckScenario.issueCount, $actual.issueCount)
    }
    if ([string]$actual.parser -ne [string]$baseline.textCheckScenario.parser) {
      $errors += ("textCheckScenario parser drifted: expected={0} actual={1}" -f $baseline.textCheckScenario.parser, $actual.parser)
    }
    foreach ($item in $baseline.textCheckScenario.hashChecks) {
      $match = $actual.hashChecks | Where-Object { $_.file -eq $item.file } | Select-Object -First 1
      if ($null -eq $match) {
        $errors += ("Missing text hash check: {0}" -f $item.file)
        continue
      }
      if ([string]$match.actualHash -ne [string]$item.hash) {
        $errors += ("textCheckScenario {0} hash drifted: expected={1} actual={2}" -f $item.file, $item.hash, $match.actualHash)
      }
    }
  }
}

if ($baseline.batchScenario) {
  $actual = $summary.batchScenario
  if ($null -eq $actual) {
    $errors += "Missing batchScenario in summary"
  }
  else {
    if ([int]$actual.overviewLineCount -ne [int]$baseline.batchScenario.overviewLineCount) {
      $errors += ("batchScenario overviewLineCount drifted: expected={0} actual={1}" -f $baseline.batchScenario.overviewLineCount, $actual.overviewLineCount)
    }
    foreach ($item in $baseline.batchScenario.overviewContains) {
      $match = $actual.overviewContains | Where-Object { $_.value -eq $item } | Select-Object -First 1
      if ($null -eq $match -or -not [bool]$match.exists) {
        $errors += ("batchScenario overview missing token: {0}" -f $item)
      }
    }
    foreach ($item in $baseline.batchScenario.artifacts) {
      $match = $actual.artifacts | Where-Object { $_.file -eq $item } | Select-Object -First 1
      if ($null -eq $match -or -not [bool]$match.exists) {
        $errors += ("batchScenario artifact missing: {0}" -f $item)
      }
    }
    foreach ($item in $baseline.batchScenario.contentChecks) {
      $match = $actual.contentChecks | Where-Object { $_.file -eq $item.file } | Select-Object -First 1
      if ($null -eq $match) {
        $errors += ("batchScenario content check missing: {0}" -f $item.file)
        continue
      }
      if (-not [bool]$match.matches) {
        $errors += ("batchScenario content mismatch: {0} missing token={1}" -f $item.file, $item.contains)
      }
    }
  }
}

if ($errors.Count -gt 0) {
  $errors | ForEach-Object { Write-Error $_ }
  exit 1
}

Write-Output "DOCX baseline check passed."
