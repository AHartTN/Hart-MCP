# Build.ps1 - Idempotent build script for Hart-MCP
# Builds native library, .NET projects, and runs all tests
# Usage: .\Build.ps1 [-Configuration Release|Debug] [-SkipTests] [-Clean]

param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$SkipTests,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"
$RepoRoot = $PSScriptRoot
$NativeDir = Join-Path $RepoRoot "src\Hart.MCP.Native"
$NativeBuildDir = Join-Path $NativeDir "build"
$NativeLibDir = Join-Path $NativeDir "lib"

Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  HART-MCP Build Script" -ForegroundColor Cyan
Write-Host "  Configuration: $Configuration" -ForegroundColor Cyan
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""

# Helper function
function Test-CommandExists($command) {
    $null = Get-Command $command -ErrorAction SilentlyContinue
    return $?
}

# Find Visual Studio
function Find-VisualStudio {
    $vsSearchPaths = @(
        "C:\Program Files\Microsoft Visual Studio\18\Insiders",  # VS 2026 Insider
        "C:\Program Files\Microsoft Visual Studio\2022\Enterprise",
        "C:\Program Files\Microsoft Visual Studio\2022\Professional",
        "C:\Program Files\Microsoft Visual Studio\2022\Community",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Enterprise",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Professional",
        "C:\Program Files (x86)\Microsoft Visual Studio\2019\Community"
    )
    
    foreach ($path in $vsSearchPaths) {
        $vcvarsall = Join-Path $path "VC\Auxiliary\Build\vcvarsall.bat"
        if (Test-Path $vcvarsall) {
            Write-Host "Found Visual Studio at: $path" -ForegroundColor Green
            return $vcvarsall
        }
    }
    
    Write-Host "ERROR: Visual Studio not found" -ForegroundColor Red
    exit 1
}

# Step 1: Clean if requested
if ($Clean) {
    Write-Host "==> Cleaning build artifacts..." -ForegroundColor Yellow
    
    if (Test-Path $NativeBuildDir) {
        Remove-Item $NativeBuildDir -Recurse -Force
    }
    
    Get-ChildItem -Path (Join-Path $RepoRoot "src") -Include "bin","obj" -Recurse -Directory | ForEach-Object {
        Write-Host "  Removing: $($_.FullName)"
        Remove-Item $_.FullName -Recurse -Force
    }
    
    Write-Host "  Clean complete" -ForegroundColor Green
    Write-Host ""
}

# Step 2: Build Native Library
Write-Host "==> Building Native Library (hartonomous_native)" -ForegroundColor Yellow

# Ensure build directory exists
if (!(Test-Path $NativeBuildDir)) {
    New-Item -ItemType Directory -Path $NativeBuildDir | Out-Null
}

# Ensure lib directory exists
if (!(Test-Path $NativeLibDir)) {
    New-Item -ItemType Directory -Path $NativeLibDir | Out-Null
}

# Check for CMake
if (!(Test-CommandExists "cmake")) {
    Write-Host "ERROR: CMake not found in PATH" -ForegroundColor Red
    exit 1
}

# Find Visual Studio and set up environment
$vcvarsall = Find-VisualStudio
Write-Host "  Using vcvarsall: $vcvarsall"

Push-Location $NativeBuildDir
try {
    # Configure CMake
    Write-Host "  Configuring CMake..."
    $cmakeOutput = & cmake -G "Visual Studio 18 2026" -A x64 .. 2>&1
    if ($LASTEXITCODE -ne 0) {
        # Try VS 2022 if 2026 not available
        $cmakeOutput = & cmake -G "Visual Studio 17 2022" -A x64 .. 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: CMake configuration failed" -ForegroundColor Red
            Write-Host $cmakeOutput
            exit 1
        }
    }
    
    # Build
    Write-Host "  Building $Configuration..."
    $buildOutput = & cmake --build . --config $Configuration 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: Native build failed" -ForegroundColor Red
        Write-Host $buildOutput
        exit 1
    }
    
    # Check for warnings
    $realWarnings = $buildOutput -split "`n" | Where-Object { $_ -match "warning" -and $_ -notmatch "MASM" }
    if ($realWarnings.Count -gt 0) {
        Write-Host "  Warnings detected in build:" -ForegroundColor Yellow
        $realWarnings | ForEach-Object { Write-Host "    $_" }
        if ($realWarnings -match "C4244|truncation|conversion") {
            Write-Host "ERROR: Truncation warnings detected - these cause data loss!" -ForegroundColor Red
            exit 1
        }
    } else {
        Write-Host "  Zero C/C++ warnings - clean build!" -ForegroundColor Green
    }
    
    # Copy DLL to lib folder
    $dllPath = Join-Path $NativeBuildDir "$Configuration\hartonomous_native.dll"
    if (Test-Path $dllPath) {
        Copy-Item $dllPath -Destination $NativeLibDir -Force
        $dllInfo = Get-Item (Join-Path $NativeLibDir "hartonomous_native.dll")
        Write-Host "  DLL copied: $($dllInfo.Length) bytes" -ForegroundColor Green
    } else {
        Write-Host "ERROR: DLL not found at $dllPath" -ForegroundColor Red
        exit 1
    }
    
} finally {
    Pop-Location
}

# Step 3: Run Native Tests
if (!$SkipTests) {
    Write-Host ""
    Write-Host "==> Running Native Tests" -ForegroundColor Yellow
    
    $testHilbert = Join-Path $NativeBuildDir "$Configuration\test_hilbert.exe"
    $testAtomSeed = Join-Path $NativeBuildDir "$Configuration\test_atom_seed.exe"
    
    if (Test-Path $testHilbert) {
        Write-Host "  Running test_hilbert..."
        $result = & $testHilbert 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: test_hilbert failed" -ForegroundColor Red
            Write-Host $result
            exit 1
        }
        $passed = ($result | Select-String "RESULTS:.+passed").Matches.Value
        Write-Host "  $passed" -ForegroundColor Green
    }
    
    if (Test-Path $testAtomSeed) {
        Write-Host "  Running test_atom_seed..."
        $result = & $testAtomSeed 2>&1
        if ($LASTEXITCODE -ne 0) {
            Write-Host "ERROR: test_atom_seed failed" -ForegroundColor Red
            Write-Host $result
            exit 1
        }
        $passed = ($result | Select-String "RESULTS:.+passed").Matches.Value
        Write-Host "  $passed" -ForegroundColor Green
    }
}

# Step 4: Restore .NET packages
Write-Host ""
Write-Host "==> Restoring .NET packages" -ForegroundColor Yellow

$dotnetOutput = & dotnet restore (Join-Path $RepoRoot "Hart.MCP.sln") --verbosity quiet 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet restore failed" -ForegroundColor Red
    Write-Host $dotnetOutput
    exit 1
}
Write-Host "  Restore complete" -ForegroundColor Green

# Step 5: Build .NET Solution
Write-Host ""
Write-Host "==> Building .NET Solution" -ForegroundColor Yellow

$buildOutput = & dotnet build (Join-Path $RepoRoot "Hart.MCP.sln") --configuration $Configuration --no-restore --verbosity minimal 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) {
    Write-Host "ERROR: dotnet build failed" -ForegroundColor Red
    Write-Host $buildOutput
    exit 1
}

# Check for warnings
$warnCount = ([regex]::Matches($buildOutput, "\d+ Warning")).Value | Select-Object -First 1
if ($warnCount -and $warnCount -ne "0 Warning") {
    Write-Host "  Build completed with warnings: $warnCount" -ForegroundColor Yellow
} else {
    Write-Host "  Build completed with zero warnings" -ForegroundColor Green
}

# Step 6: Run .NET Tests
if (!$SkipTests) {
    Write-Host ""
    Write-Host "==> Running .NET Tests" -ForegroundColor Yellow
    
    $testOutput = & dotnet test (Join-Path $RepoRoot "src\Hart.MCP.Tests\Hart.MCP.Tests.csproj") --configuration $Configuration --no-build --verbosity normal 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) {
        Write-Host "ERROR: dotnet test failed" -ForegroundColor Red
        Write-Host $testOutput
        exit 1
    }
    
    # Extract test results
    $testSummary = $testOutput -split "`n" | Where-Object { $_ -match "Total tests:|Passed:|Failed:" }
    $testSummary | ForEach-Object { Write-Host "  $_" }
    
    if ($testOutput -match "Failed:\s*[1-9]") {
        Write-Host "ERROR: Some tests failed!" -ForegroundColor Red
        exit 1
    }
    Write-Host "  All tests passed!" -ForegroundColor Green
}

# Summary
Write-Host ""
Write-Host "============================================" -ForegroundColor Cyan
Write-Host "  BUILD SUCCESSFUL" -ForegroundColor Green
Write-Host "============================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Artifacts:"
Write-Host "  Native DLL: $NativeLibDir\hartonomous_native.dll"
Write-Host "  API:        src\Hart.MCP.Api\bin\$Configuration\net10.0\Hart.MCP.Api.dll"
Write-Host "  Core:       src\Hart.MCP.Core\bin\$Configuration\net10.0\Hart.MCP.Core.dll"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. Set up PostgreSQL 18 with PostGIS"
Write-Host "  2. Run: dotnet ef database update -p src\Hart.MCP.Api"
Write-Host "  3. Run: dotnet run --project src\Hart.MCP.Api"
Write-Host ""
