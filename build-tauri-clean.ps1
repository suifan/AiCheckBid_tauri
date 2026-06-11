param(
    [string]$Toolchain = "1.85.1-x86_64-pc-windows-msvc",
    [switch]$ReinstallToolchain,
    [switch]$DebugBuild
)

$ErrorActionPreference = "Stop"

function Step([string]$Message) {
    Write-Host ""
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Warn([string]$Message) {
    Write-Host "WARN: $Message" -ForegroundColor Yellow
}

function Require-Command([string]$Name) {
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Missing command: $Name"
    }
}

$ProjectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $ProjectRoot

Require-Command "npm"
Require-Command "rustup"
Require-Command "cargo"

Step "Stop leftover build processes"
Get-Process cargo, rustc, node -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 800

Step "Clean old Tauri target"
Remove-Item -Recurse -Force "$ProjectRoot\src-tauri\target" -ErrorAction SilentlyContinue

if ($ReinstallToolchain) {
    Step "Reinstall Rust toolchain $Toolchain"
    rustup toolchain uninstall $Toolchain
    rustup toolchain install $Toolchain
}

Step "Set build environment"
$env:RUSTUP_TOOLCHAIN = $Toolchain
$env:CARGO_BUILD_JOBS = "1"
$env:RUSTFLAGS = "-C codegen-units=1"

if ($DebugBuild) {
    $env:RUSTFLAGS = "-C codegen-units=1 -C opt-level=0"
}

Write-Host "RUSTUP_TOOLCHAIN=$env:RUSTUP_TOOLCHAIN"
Write-Host "CARGO_BUILD_JOBS=$env:CARGO_BUILD_JOBS"
Write-Host "RUSTFLAGS=$env:RUSTFLAGS"

Step "Install frontend deps"
npm install

Step "Build frontend"
npm run build

Step "Build Tauri app"
if ($DebugBuild) {
    npm run tauri build -- --debug
} else {
    npm run tauri build
}

Step "Check bundle output"
$bundlePaths = @(
    "$ProjectRoot\src-tauri\target\release\bundle",
    "$ProjectRoot\src-tauri\target\debug\bundle"
)

$found = $false
foreach ($bundlePath in $bundlePaths) {
    if (Test-Path $bundlePath) {
        $found = $true
        Write-Host "Bundle path: $bundlePath" -ForegroundColor Green
        Get-ChildItem -Recurse -File $bundlePath | Select-Object FullName, Length, LastWriteTime | Format-Table -AutoSize
    }
}

if (-not $found) {
    Warn "No bundle directory found. Check the real error output above."
}

Write-Host ""
Write-Host "Done." -ForegroundColor Green
