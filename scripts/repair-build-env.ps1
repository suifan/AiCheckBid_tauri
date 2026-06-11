param(
  [string]$ProjectRoot = "d:\work\ollama\AiCheckBid_tauri",
  [string]$Toolchain = "1.88.0-x86_64-pc-windows-msvc",
  [switch]$FullCargoReset,
  [switch]$ReinstallToolchain,
  [switch]$ReinstallStable,
  [switch]$CheckVsBuildTools
)

$ErrorActionPreference = "Stop"

function Step([string]$Message) {
  Write-Host ""
  Write-Host "==> $Message" -ForegroundColor Cyan
}

function Remove-PathIfExists([string]$PathValue) {
  if (Test-Path $PathValue) {
    Write-Host "Remove: $PathValue"
    Remove-Item $PathValue -Recurse -Force -ErrorAction SilentlyContinue
  } else {
    Write-Host "Skip missing: $PathValue"
  }
}

function Require-Command([string]$Name) {
  if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
    throw "Missing command: $Name"
  }
}

function Show-CommandLocation([string]$Name) {
  Write-Host ""
  Write-Host "${Name}:" -ForegroundColor Yellow
  $commands = @(Get-Command $Name -All -ErrorAction SilentlyContinue)
  if ($commands.Count -eq 0) {
    Write-Host "Not found in current PATH." -ForegroundColor Yellow
    return
  }

  $commands |
    Select-Object -ExpandProperty Source -Unique |
    ForEach-Object { Write-Host $_ }
}

function Import-VsDevEnvironment() {
  $cmdExe = Join-Path $env:WINDIR "System32\cmd.exe"
  $vsDevCmd = @(
    "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat",
    "C:\Program Files\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat"
  ) | Where-Object { Test-Path $_ } | Select-Object -First 1

  if (!(Test-Path $cmdExe) -or -not $vsDevCmd) {
    Write-Host "Skip VS env import." -ForegroundColor Yellow
    return $false
  }

  $dumpFile = Join-Path $env:TEMP "aicheckbid_vsdevcmd_env.txt"
  if (Test-Path $dumpFile) {
    Remove-Item $dumpFile -Force -ErrorAction SilentlyContinue
  }

  $cmdLine = "`"$vsDevCmd`" -arch=x64 -host_arch=x64 >nul && set > `"$dumpFile`""
  & $cmdExe /d /s /c $cmdLine | Out-Null
  if (!(Test-Path $dumpFile)) {
    Write-Host "Failed to import VS env." -ForegroundColor Yellow
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

Set-Location $ProjectRoot

Require-Command "rustup"
[void](Import-VsDevEnvironment)

Step "Stop leftover build processes"
Get-Process cargo, rustc, link, node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

Step "Clear volatile environment variables"
foreach ($name in @(
  "RUSTC",
  "CARGO",
  "RUSTC_WRAPPER",
  "RUSTFLAGS",
  "RUSTUP_TOOLCHAIN",
  "RUSTC_STAGE",
  "CARGO_BUILD_JOBS",
  "CARGO_TARGET_DIR"
)) {
  if (Test-Path "Env:$name") {
    Remove-Item "Env:$name" -ErrorAction SilentlyContinue
  }
}

Step "Clean project build output"
Remove-PathIfExists (Join-Path $ProjectRoot "src-tauri\target")

if ($FullCargoReset) {
  $cargoHome = Join-Path $env:USERPROFILE ".cargo"
  Step "Reset cargo registry and git cache"
  Remove-PathIfExists (Join-Path $cargoHome "registry\src")
  Remove-PathIfExists (Join-Path $cargoHome "registry\cache")
  Remove-PathIfExists (Join-Path $cargoHome "registry\index")
  Remove-PathIfExists (Join-Path $cargoHome "git\checkouts")
  Remove-PathIfExists (Join-Path $cargoHome "git\db")
}

if ($ReinstallToolchain) {
  Step "Reinstall toolchain $Toolchain"
  rustup toolchain uninstall $Toolchain
  rustup toolchain install $Toolchain
}

if ($ReinstallStable) {
  Step "Reinstall stable toolchain"
  rustup toolchain uninstall stable-x86_64-pc-windows-msvc
  rustup toolchain install stable-x86_64-pc-windows-msvc
}

if ($CheckVsBuildTools) {
  Step "Check Visual Studio build tools"
  $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
  if (Test-Path $vswhere) {
    & $vswhere -latest -products * -format json
  } else {
    Write-Host "vswhere.exe not found: $vswhere" -ForegroundColor Yellow
  }
  Show-CommandLocation "link.exe"
  Show-CommandLocation "cl.exe"
  Write-Host ""
  Write-Host "LIB:" -ForegroundColor Yellow
  Write-Host $env:LIB
  Write-Host ""
  Write-Host "INCLUDE:" -ForegroundColor Yellow
  Write-Host $env:INCLUDE
}

Step "Installed Rust toolchains"
rustup toolchain list

Write-Host ""
Write-Host "Environment reset complete." -ForegroundColor Green
Write-Host "Suggested next command:" -ForegroundColor Green
Write-Host "& '.\scripts\build-delivery.ps1' -ProjectRoot '$ProjectRoot' -Installer"
