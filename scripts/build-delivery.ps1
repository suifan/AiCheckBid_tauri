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

function Invoke-Ext([string]$exe, [string[]]$Arguments) {
  & $exe @Arguments
  if ($LASTEXITCODE -ne 0) {
    throw "Command failed (exit $LASTEXITCODE): $exe $($Arguments -join ' ')"
  }
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
    Toolchain = $Toolchain
    DebugBuild = $DebugBuild
  }
}

# Add common tool paths for restricted environments.
Add-PathIfExists "C:\Users\Administrator\AppData\Roaming\TRAE SOLO\ModularData\ai-agent\vm\tools\node"
Add-PathIfExists "C:\Users\Administrator\AppData\Roaming\TRAE SOLO\ModularData\ai-agent\vm\tools\bin"
Add-PathIfExists "C:\Users\Administrator\.cargo\bin"
Add-PathIfExists "C:\Program Files\nodejs"

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

Write-Host "==> 3) Build Tauri installer"
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
  "1.88.0-x86_64-pc-windows-msvc",
  "stable-x86_64-pc-windows-msvc",
  "1.89.0-x86_64-pc-windows-msvc"
)

$installedToolchains = Get-InstalledToolchains
Write-Host "==> Installed toolchains:"
$installedToolchains | ForEach-Object { Write-Host "   - $_" }

$buildResult = $null
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
    if ($result.Success) {
      $buildResult = $result
      break
    }
    Write-Host "==> Build failed on $tc (exit $($result.ExitCode))"
    Write-Host "==> Key errors from log:"
    Select-String -Path $result.LogPath -Pattern "error\[", "error:", "failed to build app" | Select-Object -Last 30
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
    if ($result.Success) {
      $buildResult = $result
      break
    }
    Write-Host "==> Default toolchain build failed (exit $($result.ExitCode))"
    Select-String -Path $result.LogPath -Pattern "error\[", "error:", "failed to build app" | Select-Object -Last 30
  }
}

if (-not $buildResult) {
  throw "All build attempts failed. Check logs in: $(Join-Path $ProjectRoot 'build-logs')"
}

Write-Host "==> Build succeeded with toolchain: $($buildResult.Toolchain), debug=$($buildResult.DebugBuild)"

$deliveryDir = Join-Path $ProjectRoot "delivery"
if (!(Test-Path $deliveryDir)) {
  New-Item -ItemType Directory -Path $deliveryDir | Out-Null
}

$setupExe = Join-Path $ProjectRoot "src-tauri\target\release\bundle\nsis\AiCheckBidNext_0.1.0_x64-setup.exe"
$mainExe = Join-Path $ProjectRoot "src-tauri\target\release\AiCheckBidNext.exe"
if ($buildResult.DebugBuild) {
  $mainExe = Join-Path $ProjectRoot "src-tauri\target\debug\AiCheckBidNext.exe"
}

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

$buildInfo = @(
  "BuildTime: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
  "Mode: $(if ($buildResult.DebugBuild) { 'debug' } else { 'release' })"
  "NoBundle: $NoBundle"
  "Toolchain: $($buildResult.Toolchain)"
  "ExeSource: $mainExe"
  "DeliveryExe: $deliveryExe"
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
