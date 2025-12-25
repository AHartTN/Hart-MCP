# Build script for Hart.MCP.Native on Windows
# Detects Visual Studio 2026 Insider or other VS versions

Write-Host "Building Hart.MCP.Native Library..." -ForegroundColor Cyan

# Detect Visual Studio installation
$vsWhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
$vsGenerator = $null

# Check for VS 2026 Insider first (version 18.x)
$vs2026Path = "C:\Program Files\Microsoft Visual Studio\18\Insiders"
if (Test-Path "$vs2026Path\VC\Auxiliary\Build\vcvarsall.bat") {
    Write-Host "Found: Visual Studio 2026 Insider" -ForegroundColor Green
    $vsGenerator = "Visual Studio 18 2026"
}
# Check for VS 2022 (version 17.x)
elseif (Test-Path "C:\Program Files\Microsoft Visual Studio\2022\Enterprise\VC\Auxiliary\Build\vcvarsall.bat") {
    Write-Host "Found: Visual Studio 2022 Enterprise" -ForegroundColor Green
    $vsGenerator = "Visual Studio 17 2022"
}
elseif (Test-Path "C:\Program Files\Microsoft Visual Studio\2022\Professional\VC\Auxiliary\Build\vcvarsall.bat") {
    Write-Host "Found: Visual Studio 2022 Professional" -ForegroundColor Green
    $vsGenerator = "Visual Studio 17 2022"
}
elseif (Test-Path "C:\Program Files\Microsoft Visual Studio\2022\Community\VC\Auxiliary\Build\vcvarsall.bat") {
    Write-Host "Found: Visual Studio 2022 Community" -ForegroundColor Green
    $vsGenerator = "Visual Studio 17 2022"
}
else {
    Write-Host "ERROR: No compatible Visual Studio installation found." -ForegroundColor Red
    Write-Host "       Please install Visual Studio 2022 or 2026 with C++ Desktop development workload" -ForegroundColor Yellow
    exit 1
}

# Check for required tools
$cmake = Get-Command cmake -ErrorAction SilentlyContinue
if (-not $cmake) {
    Write-Host "ERROR: CMake not found. Please install CMake 3.20+" -ForegroundColor Red
    exit 1
}
Write-Host "Found: CMake at $($cmake.Source)" -ForegroundColor Green

# Create build directory
$buildDir = "build"
if (Test-Path $buildDir) {
    Write-Host "Cleaning existing build directory..." -ForegroundColor Yellow
    Remove-Item -Recurse -Force $buildDir
}

New-Item -ItemType Directory -Path $buildDir | Out-Null
Set-Location $buildDir

# Configure
Write-Host "`nConfiguring with CMake using $vsGenerator..." -ForegroundColor Cyan
cmake .. -G $vsGenerator -A x64

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: CMake configuration failed" -ForegroundColor Red
    Set-Location ..
    exit 1
}

# Build
Write-Host "`nBuilding..." -ForegroundColor Cyan
cmake --build . --config Release

if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: Build failed" -ForegroundColor Red
    Set-Location ..
    exit 1
}

Set-Location ..

# Copy to lib folder
Write-Host "`nCopying output to lib folder..." -ForegroundColor Cyan
if (-not (Test-Path "lib")) { New-Item -ItemType Directory -Path "lib" | Out-Null }
Copy-Item "build\Release\hartonomous_native.dll" "lib\" -Force
Copy-Item "build\Release\hartonomous_native.lib" "lib\" -Force

# Run tests
Write-Host "`nRunning tests..." -ForegroundColor Cyan
& "build\Release\test_atom_seed.exe"
& "build\Release\test_hilbert.exe"

Write-Host "`n✓ Build complete!" -ForegroundColor Green
Write-Host "Library: lib\hartonomous_native.dll" -ForegroundColor Green
Write-Host "`nTo use in .NET, copy to your output directory or add to project:" -ForegroundColor Cyan
Write-Host '  <Content Include="..\..\Hart.MCP.Native\lib\hartonomous_native.dll">' -ForegroundColor Gray
Write-Host '    <CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>' -ForegroundColor Gray
Write-Host '  </Content>' -ForegroundColor Gray
