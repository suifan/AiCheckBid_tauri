param(
  [string]$ProjectRoot = "d:\work\ollama\AiCheckBid_tauri",
  [ValidateSet("auto","release","debug")]
  [string]$Mode = "auto",
  [switch]$SkipClean,
  [switch]$NoBundle = $true,
  [switch]$Installer
)

$ErrorActionPreference = "Stop"

Set-Location $ProjectRoot

if ($Installer) {
  $NoBundle = $false
  if ($Mode -eq "auto" -or $Mode -eq "debug") {
    $Mode = "release"
  }
}

function Add-PathIfExists([string]$p) {
  if ($p -and (Test-Path $p)) {
    $env:PATH = "$p;$env:PATH"
  }
}

function Resolve-FirstExisting([string[]]$candidates) {
  foreach ($c in $candidates) {
    if ($c -and (Test-Path $c)) {
      return $c
    }
  }
  return $null
}

function Import-VsDevEnvironment() {
  $cmdExe = Join-Path $env:WINDIR "System32\cmd.exe"
  if (!(Test-Path $cmdExe)) {
    Write-Host "==> Skip VS env import: cmd.exe not found"
    return $false
  }

  $vsDevCmd = Resolve-FirstExisting @(
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat",
    "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat"
  )
  if (-not $vsDevCmd) {
    Write-Host "==> Skip VS env import: VsDevCmd.bat not found"
    return $false
  }

  Write-Host "==> Import VS build environment"
  $dumpFile = Join-Path $env:TEMP "aicheckbid_vsdevcmd_env.txt"
  if (Test-Path $dumpFile) {
    Remove-Item $dumpFile -Force -ErrorAction SilentlyContinue
  }

  $cmdLine = "`"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && set > `"$dumpFile`""
  & $cmdExe /d /s /c $cmdLine | Out-Null
  if (!(Test-Path $dumpFile)) {
    Write-Host "==> Skip VS env import: failed to dump environment"
    return $false
  }

  foreach ($line in Get-Content $dumpFile) {
    $idx = $line.IndexOf("=")
    if ($idx -le 0) {
      continue
    }
    $name = $line.Substring(0, $idx)
    $value = $line.Substring($idx + 1)
    Set-Item -Path "Env:$name" -Value $value
  }
  Remove-Item $dumpFile -Force -ErrorAction SilentlyContinue
  return $true
}

function Copy-TreeIfExists([string]$Source, [string]$Destination) {
  if (!(Test-Path $Source)) {
    Write-Host "==> Skip missing resource tree: $Source"
    return $false
  }
  $parent = Split-Path $Destination -Parent
  if ($parent -and !(Test-Path $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
  }
  if (Test-Path $Destination) {
    Remove-Item $Destination -Recurse -Force
  }
  Copy-Item $Source $Destination -Recurse -Force
  return $true
}

function Copy-FileIfExists([string]$Source, [string]$Destination) {
  if (!(Test-Path $Source)) {
    Write-Host "==> Skip missing resource file: $Source"
    return $false
  }
  $parent = Split-Path $Destination -Parent
  if ($parent -and !(Test-Path $parent)) {
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
  }
  Copy-Item $Source $Destination -Force
  return $true
}

function Invoke-Ext([string]$exe, [string[]]$Arguments) {
  & $exe @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "Command failed (exit $LASTEXITCODE): $exe $($Arguments -join ' ')"
  }
}

function Get-Net48MsBuildCandidates() {
  return @(
    "C:\Windows\Microsoft.NET\Framework64\v4.0.30319\MSBuild.exe",
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe",
    "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe"
  ) | Where-Object { Test-Path $_ }
}

function Assert-PathExists([string]$PathValue, [string]$Label) {
  if (!(Test-Path $PathValue)) {
    throw "$Label not found: $PathValue"
  }
}

function Build-DotNetSidecars([string]$ProjectRoot) {
  $net8Proj = Join-Path $ProjectRoot "sidecar\DocParserSidecar\DocParserSidecar.csproj"
  $net48Proj = Join-Path $ProjectRoot "sidecar\DocParserSidecarNet48\DocParserSidecarNet48.csproj"
  $spireDoc = Join-Path $ProjectRoot "sidecar\lib\spire.doc.dll"
  $spireLicense = Join-Path $ProjectRoot "sidecar\lib\spire.license.dll"

  Assert-PathExists $net8Proj "net8 sidecar project"
  Assert-PathExists $net48Proj "net48 sidecar project"
  Assert-PathExists $spireDoc "Spire.Doc dependency"
  Assert-PathExists $spireLicense "Spire.License dependency"

  Write-Host "==> Build net8 sidecar"
  Invoke-Ext "dotnet" @("build", $net8Proj, "-c", "Debug", "-v", "minimal")

  $msbuildCandidates = @(Get-Net48MsBuildCandidates)
  if ($msbuildCandidates.Count -eq 0) {
    throw "MSBuild.exe for .NET Framework was not found. Please install Visual Studio Build Tools or .NET Framework 4.8 Developer Pack."
  }

  $net48Built = $false
  $net48Errors = @()
  foreach ($msbuild in $msbuildCandidates) {
    Write-Host "==> Build net48 sidecar with: $msbuild"
    try {
      Invoke-Ext $msbuild @($net48Proj, "/t:Build", "/p:Configuration=Debug", "/p:Platform=AnyCPU", "/v:minimal")
      $net48Built = $true
      break
    } catch {
      $net48Errors += $_.Exception.Message
      Write-Host "==> net48 build failed with current MSBuild candidate, try next if available"
    }
  }
  if (-not $net48Built) {
    throw ("All net48 build attempts failed.`r`n" + ($net48Errors -join "`r`n"))
  }

  Assert-PathExists (Join-Path $ProjectRoot "sidecar\DocParserSidecar\bin\Debug\net8.0\DocParserSidecar.exe") "net8 sidecar output"
  Assert-PathExists (Join-Path $ProjectRoot "sidecar\DocParserSidecarNet48\bin\Debug\DocParserSidecarNet48.exe") "net48 sidecar output"
}

function Get-InstalledToolchains() {
  $raw = & rustup toolchain list
  $items = @()
  foreach ($line in $raw) {
    $name = ($line -split "\s+")[0].Trim()
    if ($name) {
      $items += $name
    }
  }
  return $items
}

function Get-DisplayToolchainName([string]$Toolchain) {
  if ($Toolchain -and $Toolchain.Trim().Length -gt 0) {
    return $Toolchain
  }
  return "(default)"
}

function Get-BuildFailureSummary([string]$LogPath, [int]$MaxLines = 12) {
  if ([string]::IsNullOrWhiteSpace($LogPath)) {
    return @("log path is empty")
  }
  if (!(Test-Path $LogPath)) {
    return @("log file missing: $LogPath")
  }

  $patterns = @(
    "error\["
    "error:"
    "Caused by:"
    "could not compile"
    "failed to build app"
    "failed to run custom build command for `proc-macro2"
    "STATUS_ACCESS_VIOLATION"
    "scalar size mismatch"
    "unstable library feature"
    "link.exe"
    "LNK"
    "panicked at"
    "unexpected error"
    "Visual Studio build tools"
  )

  $summary = New-Object System.Collections.Generic.List[string]
  $seen = @{}
  foreach ($match in Select-String -Path $LogPath -Pattern $patterns -CaseSensitive:$false) {
    $line = $match.Line.Trim()
    if (!$line) {
      continue
    }
    if ($seen.ContainsKey($line)) {
      continue
    }
    $seen[$line] = $true
    $summary.Add($line)
  }

  if ($summary.Count -eq 0) {
    return @("no matching error lines found in $LogPath")
  }

  return @($summary | Select-Object -Last $MaxLines)
}

function Test-ProcMacro2WindowsHostPanic([string]$LogPath) {
  if ([string]::IsNullOrWhiteSpace($LogPath) -or !(Test-Path $LogPath)) {
    return $false
  }

  $content = Get-Content $LogPath -Raw -ErrorAction SilentlyContinue
  if ([string]::IsNullOrWhiteSpace($content)) {
    return $false
  }

  return (
    $content -match 'failed to run custom build command for `' -and
    $content -match 'called `Result::unwrap\(\)` on an `Err` value: Os \{ code: 0' -and
    ($content -match '操作成功完成' -or $content -match 'sys_common\\process\.rs' -or $content -match 'sys\\pal\\windows\\process\.rs' -or $content -match 'sys\\process\\mod\.rs')
  )
}

function Throw-ProcMacro2WindowsHostPanic($Result, [string]$ProjectRoot) {
  $toolchainName = Get-DisplayToolchainName $Result.Toolchain
  $modeText = if ($Result.DebugBuild) { "debug" } else { "release" }
  throw @"
Detected repeated Windows host panic while running a Rust/Tauri build script.
This is a build-host environment failure, not an app source compile error.

toolchain: $toolchainName
mode: $modeText
log: $($Result.LogPath)

Recommended actions:
1) Run: .\scripts\repair-build-env.ps1 -ProjectRoot '$ProjectRoot'
2) Retry after closing antivirus / EDR / file hook tools
3) If it still fails, reboot the build host and retry once
4) Do not keep rotating Rust toolchains for this error pattern
"@
}

function Write-BuildFailureSummary($Result, [string]$Label) {
  if ($null -eq $Result) {
    Write-Host "==> $Label"
    Write-Host "   result is null"
    return
  }
  $toolchainName = Get-DisplayToolchainName $Result.Toolchain
  Write-Host "==> $Label"
  Write-Host "   toolchain: $toolchainName"
  Write-Host "   mode: $(if ($Result.DebugBuild) { 'debug' } else { 'release' })"
  Write-Host "   exit: $($Result.ExitCode)"
  Write-Host "   log: $($Result.LogPath)"
  $summary = Get-BuildFailureSummary -LogPath $Result.LogPath
  foreach ($line in $summary) {
    Write-Host "   $line"
  }
}

function Invoke-TauriBuildWithToolchain(
  [string]$Toolchain,
  [string]$ProjectRoot,
  [string]$NodeExe,
  [string]$TauriJs,
  [bool]$DebugBuild = $false,
  [bool]$DoClean = $true,
  [bool]$NoBundleBuild = $true
) {
  if ($Toolchain -and $Toolchain.Trim().Length -gt 0) {
    $env:RUSTUP_TOOLCHAIN = $Toolchain
  }

  # Conservative compiler settings to reduce host-specific rustc instability.
  $env:CARGO_BUILD_JOBS = "1"
  $env:CARGO_INCREMENTAL = "0"
  $env:RUST_BACKTRACE = "1"
  $env:RUSTFLAGS = "-C codegen-units=1 -C debuginfo=0"

  Write-Host "==> Rust toolchain: $Toolchain"
  Invoke-Ext "cargo" @("-V")

  if ($DoClean) {
    Write-Host "==> Clean Rust cache"
    Invoke-Ext "cargo" @("clean", "--manifest-path", (Join-Path $ProjectRoot "src-tauri\Cargo.toml"))
  } else {
    Write-Host "==> Skip cargo clean"
  }

  $logDir = Join-Path $ProjectRoot "build-logs"
  if (!(Test-Path $logDir)) {
    New-Item -ItemType Directory -Path $logDir | Out-Null
  }
  $ts = Get-Date -Format "yyyyMMdd_HHmmss"
  $safeTc = ($Toolchain -replace "[^A-Za-z0-9\-_.]", "_")
  $tauriLog = Join-Path $logDir "tauri-build-$ts-$safeTc.log"
  $tauriErr = Join-Path $logDir "tauri-build-$ts-$safeTc.err.log"
  Write-Host "==> Build log: $tauriLog"

  $exePath = $null
  $argList = @()
  if ($NoBundleBuild) {
    # No-bundle mode: build app binary directly with cargo to avoid WiX/network dependency.
    $exePath = "cargo"
    $argList = @("build", "--manifest-path", (Join-Path $ProjectRoot "src-tauri\Cargo.toml"))
    if (-not $DebugBuild) {
      $argList += "--release"
      $env:CARGO_PROFILE_RELEASE_OPT_LEVEL = "1"
      $env:CARGO_PROFILE_RELEASE_CODEGEN_UNITS = "1"
      $env:CARGO_PROFILE_RELEASE_LTO = "false"
    }
  } else {
    $exePath = $NodeExe
    $argList = @($TauriJs, "build")
    if ($DebugBuild) {
      $argList += "--debug"
    } else {
      # Conservative release profile to reduce compiler instability on some hosts.
      $env:CARGO_PROFILE_RELEASE_OPT_LEVEL = "1"
      $env:CARGO_PROFILE_RELEASE_CODEGEN_UNITS = "1"
      $env:CARGO_PROFILE_RELEASE_LTO = "false"
    }
  }

  $proc = Start-Process -FilePath $exePath `
    -ArgumentList $argList `
    -NoNewWindow `
    -RedirectStandardOutput $tauriLog `
    -RedirectStandardError $tauriErr `
    -PassThru `
    -Wait

  if (Test-Path $tauriErr) {
    Add-Content -Path $tauriLog -Value "`r`n===== STDERR =====`r`n"
    Get-Content -Path $tauriErr | Add-Content -Path $tauriLog
  }

  return [PSCustomObject]@{
    Success = ($proc.ExitCode -eq 0)
    ExitCode = $proc.ExitCode
    LogPath = $tauriLog
    ErrLogPath = $tauriErr
    Toolchain = $Toolchain
    DebugBuild = $DebugBuild
  }
}

# Add common tool paths for restricted environments.
Add-PathIfExists "C:\Users\Administrator\AppData\Roaming\TRAE SOLO\ModularData\ai-agent\vm\tools\node"
Add-PathIfExists "C:\Users\Administrator\AppData\Roaming\TRAE SOLO\ModularData\ai-agent\vm\tools\bin"
Add-PathIfExists "C:\Users\Administrator\.cargo\bin"
Add-PathIfExists "C:\Program Files\nodejs"
Add-PathIfExists (Join-Path $env:WINDIR "System32")
Add-PathIfExists $env:WINDIR

[void](Import-VsDevEnvironment)

if (-not (Get-Command cargo -ErrorAction SilentlyContinue)) {
  throw "cargo was not found. Please install Rust or add cargo to PATH."
}

$rustupExe = Resolve-FirstExisting @(
  (Get-Command rustup -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
  "C:\Users\Administrator\.cargo\bin\rustup.exe"
)
if (-not $rustupExe) {
  throw "rustup.exe was not found."
}

$nodeExe = Resolve-FirstExisting @(
  (Get-Command node -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
  "C:\Program Files\nodejs\node.exe",
  "C:\Users\Administrator\AppData\Roaming\TRAE SOLO\ModularData\ai-agent\vm\tools\node\node.exe"
)
if (-not $nodeExe) {
  throw "node.exe was not found."
}

$npmCmd = Resolve-FirstExisting @(
  (Get-Command npm -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Source -ErrorAction SilentlyContinue),
  "C:\Program Files\nodejs\npm.cmd",
  "C:\Users\Administrator\AppData\Roaming\TRAE SOLO\ModularData\ai-agent\vm\tools\node\npm.cmd"
)
if (-not $npmCmd) {
  throw "npm.cmd was not found."
}

$viteJs = Join-Path $ProjectRoot "node_modules\vite\bin\vite.js"
$tauriJs = Join-Path $ProjectRoot "node_modules\@tauri-apps\cli\tauri.js"

Write-Host "==> 1) Install dependencies"
Invoke-Ext $npmCmd @("install")

Write-Host "==> 2) Build frontend dist"
if (!(Test-Path $viteJs)) {
  throw "vite.js was not found: $viteJs"
}
Invoke-Ext $nodeExe @($viteJs, "build")

Write-Host "==> 3) Build .NET sidecars"
Build-DotNetSidecars -ProjectRoot $ProjectRoot

Write-Host "==> 4) Build Tauri installer"
if (-not $NoBundle -and !(Test-Path $tauriJs)) {
  throw "tauri.js was not found: $tauriJs"
}

if (-not $NoBundle) {
  $deliveryDirForClean = Join-Path $ProjectRoot "delivery"
  $oldSetup = Join-Path $deliveryDirForClean "AiCheckBidNext_0.1.0_x64-setup.exe"
  if (Test-Path $oldSetup) {
    Write-Host "==> Remove stale installer in delivery: $oldSetup"
    Remove-Item $oldSetup -Force
  }
}

$toolchains = @(
  "1.85.1-x86_64-pc-windows-msvc",
  "1.88.0-x86_64-pc-windows-msvc",
  "stable-x86_64-pc-windows-msvc",
  "1.89.0-x86_64-pc-windows-msvc"
)

$installedToolchains = Get-InstalledToolchains
Write-Host "==> Installed toolchains:"
$installedToolchains | ForEach-Object { Write-Host "   - $_" }

$buildResult = $null
$attemptResults = @()
$modes = @()
if ($Mode -eq "release") {
  $modes = @($false)
} elseif ($Mode -eq "debug") {
  $modes = @($true)
} else {
  $modes = @($false, $true) # release first, then debug fallback
}
foreach ($isDebug in $modes) {
  foreach ($tc in $toolchains) {
    if ($installedToolchains -notcontains $tc) {
      Write-Host "==> Skip toolchain (not installed offline): $tc"
      continue
    }
    if ($isDebug) {
      Write-Host "==> Try toolchain: $tc | mode: debug"
    } else {
      Write-Host "==> Try toolchain: $tc | mode: release"
    }
    $result = Invoke-TauriBuildWithToolchain `
      -Toolchain $tc `
      -ProjectRoot $ProjectRoot `
      -NodeExe $nodeExe `
      -TauriJs $tauriJs `
      -DebugBuild:$isDebug `
      -DoClean:(!$SkipClean) `
      -NoBundleBuild:$NoBundle
    $attemptResults += $result
    if ($result.Success) {
      $buildResult = $result
      break
    }
    Write-BuildFailureSummary -Result $result -Label "Build failed"
    if (Test-ProcMacro2WindowsHostPanic -LogPath $result.LogPath) {
      Throw-ProcMacro2WindowsHostPanic -Result $result -ProjectRoot $ProjectRoot
    }
  }
  if ($buildResult) { break }
}

if (-not $buildResult) {
  Write-Host "==> Preferred toolchains all unavailable/failed, fallback to current default toolchain"
  foreach ($isDebug in $modes) {
    $result = Invoke-TauriBuildWithToolchain `
      -Toolchain "" `
      -ProjectRoot $ProjectRoot `
      -NodeExe $nodeExe `
      -TauriJs $tauriJs `
      -DebugBuild:$isDebug `
      -DoClean:(!$SkipClean) `
      -NoBundleBuild:$NoBundle
    $attemptResults += $result
    if ($result.Success) {
      $buildResult = $result
      break
    }
    Write-BuildFailureSummary -Result $result -Label "Default toolchain build failed"
    if (Test-ProcMacro2WindowsHostPanic -LogPath $result.LogPath) {
      Throw-ProcMacro2WindowsHostPanic -Result $result -ProjectRoot $ProjectRoot
    }
  }
}

if (-not $buildResult) {
  Write-Host "==> Final failure summary"
  foreach ($attempt in $attemptResults) {
    Write-BuildFailureSummary -Result $attempt -Label "Attempt summary"
  }
  throw "All build attempts failed. Check logs in: $(Join-Path $ProjectRoot 'build-logs')"
}

Write-Host "==> Build succeeded with toolchain: $($buildResult.Toolchain), debug=$($buildResult.DebugBuild)"

$deliveryDir = Join-Path $ProjectRoot "delivery"
if (!(Test-Path $deliveryDir)) {
  New-Item -ItemType Directory -Path $deliveryDir | Out-Null
}

$setupExe = Join-Path $ProjectRoot "src-tauri\target\release\bundle\nsis\AiCheckBidNext_0.1.0_x64-setup.exe"
$targetDir = if ($buildResult.DebugBuild) {
  Join-Path $ProjectRoot "src-tauri\target\debug"
} else {
  Join-Path $ProjectRoot "src-tauri\target\release"
}
$mainExe = Resolve-FirstExisting @(
  (Join-Path $targetDir "AiCheckBidNext.exe"),
  (Join-Path $targetDir "aicheckbid_tauri.exe")
)

if (!(Test-Path $mainExe)) {
  throw "Main executable not found: $mainExe"
}
$deliveryExe = Join-Path $deliveryDir "AiCheckBidNext.exe"
try {
  Copy-Item $mainExe $deliveryExe -Force
} catch {
  $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
  $deliveryExe = Join-Path $deliveryDir "AiCheckBidNext_$stamp.exe"
  Copy-Item $mainExe $deliveryExe -Force
}

if ($NoBundle) {
  $staleSetup = Join-Path $deliveryDir "AiCheckBidNext_0.1.0_x64-setup.exe"
  if (Test-Path $staleSetup) {
    Remove-Item $staleSetup -Force
  }
}

if (!$buildResult.DebugBuild -and -not $NoBundle) {
  if (!(Test-Path $setupExe)) {
    throw "Installer not found: $setupExe"
  }
  Copy-Item $setupExe (Join-Path $deliveryDir "AiCheckBidNext_0.1.0_x64-setup.exe") -Force
}

$deliveryResultDir = Join-Path $deliveryDir "result"
if (!(Test-Path $deliveryResultDir)) {
  New-Item -ItemType Directory -Path $deliveryResultDir -Force | Out-Null
}

Write-Host "==> Sync portable resources"
Copy-TreeIfExists (Join-Path $ProjectRoot "rules") (Join-Path $deliveryDir "rules") | Out-Null
Copy-TreeIfExists (Join-Path $ProjectRoot "set") (Join-Path $deliveryDir "set") | Out-Null
Copy-TreeIfExists (Join-Path $ProjectRoot "sidecar\DocParserSidecar\bin") (Join-Path $deliveryDir "sidecar\DocParserSidecar\bin") | Out-Null
Copy-TreeIfExists (Join-Path $ProjectRoot "sidecar\DocParserSidecarNet48\bin") (Join-Path $deliveryDir "sidecar\DocParserSidecarNet48\bin") | Out-Null

$buildInfo = @(
  "BuildTime: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
  "Mode: $(if ($buildResult.DebugBuild) { 'debug' } else { 'release' })"
  "NoBundle: $NoBundle"
  "Toolchain: $($buildResult.Toolchain)"
  "ExeSource: $mainExe"
  "DeliveryExe: $deliveryExe"
  "RulesDir: $(Join-Path $deliveryDir 'rules')"
  "LegacySetDir: $(Join-Path $deliveryDir 'set')"
  "SidecarNet8Dir: $(Join-Path $deliveryDir 'sidecar\DocParserSidecar\bin')"
  "SidecarNet48Dir: $(Join-Path $deliveryDir 'sidecar\DocParserSidecarNet48\bin')"
  "ResultDir: $deliveryResultDir"
) -join "`r`n"
Set-Content -Path (Join-Path $deliveryDir "BUILD_INFO.txt") -Value $buildInfo -Encoding UTF8

$zipPath = Join-Path $ProjectRoot "AiCheckBidNext_delivery_latest.zip"
if (Test-Path $zipPath) {
  try {
    Remove-Item $zipPath -Force
  } catch {
    $stamp = Get-Date -Format "yyyyMMdd_HHmmss"
    $zipPath = Join-Path $ProjectRoot "AiCheckBidNext_delivery_$stamp.zip"
  }
}
Compress-Archive -Path (Join-Path $deliveryDir "*") -DestinationPath $zipPath -Force

Write-Host "==> Packaging completed"
if ($buildResult.DebugBuild) {
  Get-Item $deliveryExe, $zipPath | Select-Object FullName, Length, LastWriteTime | Format-Table -AutoSize
} elseif ($NoBundle) {
  Get-Item $deliveryExe, $zipPath | Select-Object FullName, Length, LastWriteTime | Format-Table -AutoSize
} else {
  Get-Item $setupExe, $deliveryExe, $zipPath | Select-Object FullName, Length, LastWriteTime | Format-Table -AutoSize
}
