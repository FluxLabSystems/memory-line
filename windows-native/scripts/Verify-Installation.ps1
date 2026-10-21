<#
.SYNOPSIS
    Verifies the Memory Timeline installation and dependencies.

.DESCRIPTION
    This script performs comprehensive verification of the Memory Timeline installation,
    including all dependencies, runtime components, and optional tools.

.PARAMETER Detailed
    Show detailed information about each component.

.PARAMETER ExportReport
    Export verification results to a text file.

.EXAMPLE
    .\Verify-Installation.ps1
    Performs basic verification.

.EXAMPLE
    .\Verify-Installation.ps1 -Detailed
    Shows detailed information for each component.

.EXAMPLE
    .\Verify-Installation.ps1 -ExportReport
    Exports results to verification-report.txt
#>

param(
    [switch]$Detailed,
    [switch]$ExportReport
)

$ErrorActionPreference = 'Continue'

# Color helpers
function Write-Pass { param([string]$Message) Write-Host "✓ PASS: $Message" -ForegroundColor Green }
function Write-Fail { param([string]$Message) Write-Host "✗ FAIL: $Message" -ForegroundColor Red }
function Write-Warn { param([string]$Message) Write-Host "⚠ WARN: $Message" -ForegroundColor Yellow }
function Write-Info { param([string]$Message) Write-Host "ℹ INFO: $Message" -ForegroundColor Cyan }

$report = @()
$passCount = 0
$failCount = 0
$warnCount = 0

function Add-ReportLine {
    param([string]$Line)
    $script:report += $Line
}

Write-Host ""
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " Memory Timeline - Installation Verification" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host ""

Add-ReportLine "Memory Timeline - Installation Verification Report"
Add-ReportLine "Generated: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Add-ReportLine ""
Add-ReportLine "═══════════════════════════════════════════════════════"
Add-ReportLine ""

# 1. Windows Version
Write-Host "Checking Windows Version..." -ForegroundColor Yellow
$osVersion = [System.Environment]::OSVersion.Version
$osName = (Get-CimInstance -ClassName Win32_OperatingSystem).Caption

if ($osVersion.Build -ge 22621) {
    Write-Pass "Windows 11 detected: $osName (Build $($osVersion.Build))"
    Add-ReportLine "[PASS] Windows: $osName (Build $($osVersion.Build))"
    $passCount++

    if ($Detailed) {
        Write-Info "  Version: $osVersion"
        Write-Info "  Build: $($osVersion.Build)"
    }
} else {
    Write-Fail "Windows 11 required. Current: $osName (Build $($osVersion.Build))"
    Add-ReportLine "[FAIL] Windows: $osName (Build $($osVersion.Build)) - Windows 11 required"
    $failCount++
}

# 2. .NET SDK
Write-Host "`nChecking .NET SDK..." -ForegroundColor Yellow
try {
    $dotnetVersion = & dotnet --version 2>$null
    $dotnetInfo = & dotnet --info 2>$null

    if ($dotnetVersion) {
        # src/global.json pins 10.0.300 with rollForward latestFeature: any
        # 10.0.3xx-or-newer SDK satisfies it; 10.0.1xx/2xx (e.g. VS 2022's) do not.
        $version = [Version]$dotnetVersion
        if ($version -ge [Version]'10.0.300') {
            Write-Pass ".NET SDK $dotnetVersion"
            Add-ReportLine "[PASS] .NET SDK: $dotnetVersion"
            $passCount++

            if ($Detailed) {
                $runtimes = & dotnet --list-runtimes
                Write-Info "  Installed runtimes:"
                $runtimes | ForEach-Object { Write-Info "    $_" }
            }
        } else {
            Write-Warn ".NET SDK $dotnetVersion found, but 10.0.300 or newer is required (global.json pin)"
            Add-ReportLine "[WARN] .NET SDK: $dotnetVersion (10.0.3xx+ required)"
            $warnCount++
        }
    }
} catch {
    Write-Fail ".NET SDK not found or not in PATH"
    Add-ReportLine "[FAIL] .NET SDK: Not found"
    $failCount++
}

# 3. Visual Studio
Write-Host "`nChecking Visual Studio 2026 (18.x)..." -ForegroundColor Yellow
$vsWherePath = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"

if (Test-Path $vsWherePath) {
    try {
        # 18.x only: the 10.0.3xx SDK pin needs MSBuild 18, so VS 2022 does not qualify.
        $vsInfo = & $vsWherePath -version "[18.0,19.0)" -latest -format json | ConvertFrom-Json

        if ($vsInfo) {
            Write-Pass "Visual Studio $($vsInfo.displayName) - Version $($vsInfo.catalog.productDisplayVersion)"
            Add-ReportLine "[PASS] Visual Studio: $($vsInfo.displayName) v$($vsInfo.catalog.productDisplayVersion)"
            $passCount++

            if ($Detailed) {
                Write-Info "  Installation Path: $($vsInfo.installationPath)"
                Write-Info "  Product: $($vsInfo.productId)"
            }
        }
    } catch {
        Write-Warn "Visual Studio installed but version info unavailable"
        Add-ReportLine "[WARN] Visual Studio: Installed but version unknown"
        $warnCount++
    }
} else {
    Write-Warn "Visual Studio 2026 not found (required to build; optional for running a built app)"
    Add-ReportLine "[WARN] Visual Studio 2026: Not found (required to build)"
    $warnCount++
}

# 4. Windows SDK
Write-Host "`nChecking Windows SDK..." -ForegroundColor Yellow
$sdkPath = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"

if (Test-Path $sdkPath) {
    $versions = Get-ChildItem $sdkPath -Directory | Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' } | Sort-Object Name -Descending

    if ($versions) {
        $latest = $versions | Select-Object -First 1
        Write-Pass "Windows SDK $($latest.Name)"
        Add-ReportLine "[PASS] Windows SDK: $($latest.Name)"
        $passCount++

        if ($Detailed) {
            Write-Info "  SDK Location: $sdkPath"
            Write-Info "  Installed versions:"
            $versions | Select-Object -First 3 | ForEach-Object { Write-Info "    $($_.Name)" }
        }
    }
} else {
    Write-Warn "Windows SDK not found (required for development)"
    Add-ReportLine "[WARN] Windows SDK: Not found"
    $warnCount++
}

# 5. Windows App SDK (2.3.1 via NuGet; the app is self-contained, so no machine-wide runtime is required)
Write-Host "`nChecking Windows App SDK..." -ForegroundColor Yellow
$nugetPackagesPath = "$env:USERPROFILE\.nuget\packages\microsoft.windowsappsdk"

if (Test-Path $nugetPackagesPath) {
    $versions = Get-ChildItem $nugetPackagesPath -Directory | Where-Object { $_.Name -match '^\d+\.\d+' } | Sort-Object Name -Descending

    if ($versions) {
        $latest = $versions | Select-Object -First 1
        Write-Pass "Windows App SDK $($latest.Name)"
        Add-ReportLine "[PASS] Windows App SDK: $($latest.Name)"
        $passCount++

        if ($Detailed) {
            Write-Info "  Package Location: $nugetPackagesPath"
        }
    }
} else {
    # Check for runtime
    $runtimePath = "${env:ProgramFiles}\WindowsApps"
    $appSDKPackages = Get-ChildItem $runtimePath -Filter "Microsoft.WindowsAppRuntime*" -Directory -ErrorAction SilentlyContinue

    if ($appSDKPackages) {
        Write-Pass "Windows App SDK runtime installed"
        Add-ReportLine "[PASS] Windows App SDK: Runtime installed"
        $passCount++
    } else {
        Write-Warn "Windows App SDK not found in the NuGet cache (restored by msbuild /t:Restore; no runtime install needed - the app is self-contained)"
        Add-ReportLine "[WARN] Windows App SDK: Not found in NuGet cache (restored on first build)"
        $warnCount++
    }
}

# 6. Git
Write-Host "`nChecking Git..." -ForegroundColor Yellow
try {
    $gitVersion = & git --version 2>$null

    if ($gitVersion) {
        Write-Pass "$gitVersion"
        Add-ReportLine "[PASS] Git: $gitVersion"
        $passCount++

        if ($Detailed) {
            $gitPath = (Get-Command git).Source
            Write-Info "  Location: $gitPath"
        }
    }
} catch {
    Write-Warn "Git not found (optional for production)"
    Add-ReportLine "[WARN] Git: Not found (optional)"
    $warnCount++
}

# 7. PowerShell
Write-Host "`nChecking PowerShell..." -ForegroundColor Yellow
$psVersion = $PSVersionTable.PSVersion

if ($psVersion.Major -ge 5) {
    Write-Pass "PowerShell $psVersion"
    Add-ReportLine "[PASS] PowerShell: $psVersion"
    $passCount++

    if ($Detailed) {
        Write-Info "  Edition: $($PSVersionTable.PSEdition)"
        Write-Info "  OS: $($PSVersionTable.OS)"
    }
} else {
    Write-Warn "PowerShell 5.0+ recommended. Current: $psVersion"
    Add-ReportLine "[WARN] PowerShell: $psVersion (5.0+ recommended)"
    $warnCount++
}

# 8. Memory Timeline Installation
Write-Host "`nChecking Memory Timeline Installation..." -ForegroundColor Yellow
$appPackage = Get-AppxPackage -Name "*MemoryTimeline*" -ErrorAction SilentlyContinue

if ($appPackage) {
    Write-Pass "Memory Timeline installed - Version $($appPackage.Version)"
    Add-ReportLine "[PASS] Memory Timeline: v$($appPackage.Version)"
    $passCount++

    if ($Detailed) {
        Write-Info "  Package Name: $($appPackage.Name)"
        Write-Info "  Publisher: $($appPackage.Publisher)"
        Write-Info "  Install Location: $($appPackage.InstallLocation)"
        Write-Info "  Architecture: $($appPackage.Architecture)"
    }
} else {
    Write-Info "Memory Timeline app package not installed"
    Add-ReportLine "[INFO] Memory Timeline: Not installed as MSIX package"
}

# 9. Optional Tools
Write-Host "`nChecking Optional Tools..." -ForegroundColor Yellow

# winget
try {
    $wingetVersion = & winget --version 2>$null
    if ($wingetVersion) {
        Write-Pass "winget $wingetVersion"
        Add-ReportLine "[PASS] winget: $wingetVersion"
        $passCount++
    }
} catch {
    Write-Warn "winget not found"
    Add-ReportLine "[WARN] winget: Not found"
    $warnCount++
}

# VS Code
$vscodePath1 = "$env:LOCALAPPDATA\Programs\Microsoft VS Code\Code.exe"
$vscodePath2 = "$env:ProgramFiles\Microsoft VS Code\Code.exe"

if ((Test-Path $vscodePath1) -or (Test-Path $vscodePath2)) {
    try {
        $vscodeVersion = & code --version 2>$null | Select-Object -First 1
        Write-Pass "Visual Studio Code $vscodeVersion (optional)"
        Add-ReportLine "[PASS] VS Code: $vscodeVersion"
        $passCount++
    } catch {
        Write-Pass "Visual Studio Code installed (optional)"
        Add-ReportLine "[PASS] VS Code: Installed"
        $passCount++
    }
}

# MSIX Packaging Tool
$msixTool = Get-AppxPackage -Name "Microsoft.MsixPackagingTool" -ErrorAction SilentlyContinue
if ($msixTool) {
    Write-Pass "MSIX Packaging Tool v$($msixTool.Version) (optional)"
    Add-ReportLine "[PASS] MSIX Packaging Tool: v$($msixTool.Version)"
    $passCount++
}

# 10. System Resources
Write-Host "`nChecking System Resources..." -ForegroundColor Yellow

$computerInfo = Get-ComputerInfo

# RAM
$ramGB = [Math]::Round($computerInfo.CsTotalPhysicalMemory / 1GB, 2)
if ($ramGB -ge 8) {
    Write-Pass "RAM: $ramGB GB"
    Add-ReportLine "[PASS] RAM: $ramGB GB"
    $passCount++
} else {
    Write-Warn "RAM: $ramGB GB (8GB+ recommended)"
    Add-ReportLine "[WARN] RAM: $ramGB GB (8GB+ recommended)"
    $warnCount++
}

# Disk Space
$sysDrive = Get-PSDrive C
$freeSpaceGB = [Math]::Round($sysDrive.Free / 1GB, 2)

if ($freeSpaceGB -ge 10) {
    Write-Pass "Free Disk Space: $freeSpaceGB GB"
    Add-ReportLine "[PASS] Free Space: $freeSpaceGB GB"
    $passCount++
} else {
    Write-Warn "Free Disk Space: $freeSpaceGB GB (10GB+ recommended)"
    Add-ReportLine "[WARN] Free Space: $freeSpaceGB GB (10GB+ recommended)"
    $warnCount++
}

# CPU
$processor = Get-CimInstance -ClassName Win32_Processor | Select-Object -First 1
Write-Info "CPU: $($processor.Name)"
Add-ReportLine "[INFO] CPU: $($processor.Name)"

# Summary
Write-Host ""
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan
Write-Host " Verification Summary" -ForegroundColor Cyan
Write-Host "═══════════════════════════════════════════════════════" -ForegroundColor Cyan

$total = $passCount + $failCount + $warnCount

Write-Host ""
Write-Host "Passed: $passCount" -ForegroundColor Green
Write-Host "Failed: $failCount" -ForegroundColor Red
Write-Host "Warnings: $warnCount" -ForegroundColor Yellow
Write-Host "Total Checks: $total"
Write-Host ""

Add-ReportLine ""
Add-ReportLine "═══════════════════════════════════════════════════════"
Add-ReportLine "Summary:"
Add-ReportLine "  Passed: $passCount"
Add-ReportLine "  Failed: $failCount"
Add-ReportLine "  Warnings: $warnCount"
Add-ReportLine "  Total: $total"
Add-ReportLine ""

if ($failCount -eq 0 -and $warnCount -eq 0) {
    Write-Host "✓ All checks passed! System is ready." -ForegroundColor Green
    Add-ReportLine "[SUCCESS] All checks passed! System is ready."
} elseif ($failCount -eq 0) {
    Write-Host "✓ All critical checks passed. Some optional components missing." -ForegroundColor Yellow
    Add-ReportLine "[SUCCESS] All critical checks passed with $warnCount warning(s)."
} else {
    Write-Host "✗ $failCount critical check(s) failed. Please install missing components." -ForegroundColor Red
    Add-ReportLine "[FAILURE] $failCount critical check(s) failed."
    Write-Host ""
    Write-Host "Run Setup-Dependencies.ps1 to install missing components:" -ForegroundColor Cyan
    Write-Host "  .\scripts\Setup-Dependencies.ps1 -Mode Development" -ForegroundColor Cyan
}

# Export report
if ($ExportReport) {
    $reportPath = Join-Path $PSScriptRoot "verification-report.txt"
    $report | Out-File -FilePath $reportPath -Encoding UTF8
    Write-Host ""
    Write-Host "Report exported to: $reportPath" -ForegroundColor Cyan
}

Write-Host ""

# Exit code
exit $failCount
