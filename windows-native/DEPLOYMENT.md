# Memory Timeline - Deployment Guide

This guide covers building, testing, and deploying the Memory Timeline Windows native application (WinUI 3 / Windows App SDK, .NET 8).

> **Important build note.** The WinUI 3 app is currently an **unpackaged** desktop app (`WindowsPackageType=None`, self-contained Windows App SDK). It builds for a specific CPU platform (**x64**, x86, or ARM64 — there is **no `AnyCPU`**) and, because of the WinUI PRI/XAML resource-generation tooling, must be built with **Visual Studio or `msbuild.exe`**, not `dotnet build`. See [Building the Application](#building-the-application) for details.
>
> MSIX packaging and Microsoft Store publishing (the later sections of this guide) are **Phase 7 work in progress** and are documented here as the intended path, not as a shipped, verified pipeline.

## Table of Contents
1. [Prerequisites](#prerequisites)
2. [Building the Application](#building-the-application)
3. [Data & Runtime Layout](#data--runtime-layout)
4. [Running Tests](#running-tests)
5. [MSIX Packaging (Phase 7 — in progress)](#msix-packaging-phase-7--in-progress)
6. [Code Signing](#code-signing)
7. [Microsoft Store Deployment (Phase 7 — in progress)](#microsoft-store-deployment-phase-7--in-progress)
8. [Side-loading](#side-loading)
9. [CI/CD Pipeline](#cicd-pipeline)

---

## Prerequisites

### Development Environment
- **Windows 11 22H2** or later (required for Windows App SDK 1.5+). The WinUI target cannot be built on Linux/macOS.
- **Visual Studio 2026** (18.0 or later) with workloads:
  - .NET Desktop Development
  - Universal Windows Platform development
  - Windows App SDK C# Templates
- **.NET SDK** — the repo pins the SDK via `windows-native/src/global.json`:
  ```json
  { "sdk": { "version": "10.0.300", "rollForward": "latestFeature" } }
  ```
  This means: use a **.NET 10 SDK, 10.0.3xx or newer**. An older 10.0.1xx/2xx SDK will not satisfy it, because the newer bands require MSBuild 18 (Visual Studio 2026), which ships a suitable SDK.
- **Windows App SDK** 1.5.x (restored as the `Microsoft.WindowsAppSDK` NuGet package)

### For Packaging / Store Deployment (Phase 7 — in progress)
- **Windows SDK** (10.0.26100.0 or later)
- **MSIX Packaging Tool** (from Microsoft Store)
- **Code Signing Certificate** (EV certificate for Microsoft Store)
- **Microsoft Partner Center account** (for Store deployment)

---

## Building the Application

### Why `dotnet build` does not work for the app

The WinUI 3 app project (`MemoryTimeline`) runs WinUI's PRI/XAML resource generation as part of the build (the `MrtCore.PriGen` / XAML compiler tooling). That tooling is a **.NET Framework** MSBuild task and **does not load under the `dotnet` CLI (.NET Core) MSBuild engine** — a `dotnet build`/`dotnet run` of the app fails during resource/XAML generation (e.g. `MSB4062` / `MSB3073` XAML-compiler errors), even when the C# itself is fine. The build must therefore be driven by **Visual Studio** or the full **Visual Studio `msbuild.exe`**.

Two more consequences:
- The solution defines **`x86`, `x64`, and `ARM64`** platforms — there is **no `AnyCPU`**. Every build must specify a platform (`x64` is the default target).
- The `MemoryTimeline.Tests` project references the WinUI app, so the whole solution is built for a concrete platform (x64) on a Windows machine.

### Build (Release, x64) — recommended

```powershell
cd windows-native/src

# Restore + build the whole solution with Visual Studio MSBuild.
# 'msbuild' here is the VS msbuild.exe (e.g. from a Developer PowerShell for VS 2026),
# NOT 'dotnet build'.
msbuild MemoryTimeline.sln /t:Restore,Build /p:Configuration=Release /p:Platform=x64 /m
```

### Debug build

```powershell
msbuild MemoryTimeline.sln /t:Restore,Build /p:Configuration=Debug /p:Platform=x64 /m
```

You can also simply open `windows-native/src/MemoryTimeline.sln` in Visual Studio 2026, select the **Release / x64** (or **Debug / x64**) configuration, and Build.

### Build Output
The compiled application is placed under the platform-specific output folder, for example:
```
windows-native/src/MemoryTimeline/bin/x64/Release/net8.0-windows10.0.26100.0/
```
Because the app is unpackaged and self-contained, this folder contains `MemoryTimeline.exe` plus the Windows App SDK runtime and can be launched directly.

---

## Data & Runtime Layout

The app is **unpackaged**, so it does not use MSIX app-data virtualization. All runtime data lives under `%LOCALAPPDATA%\MemoryTimeline\`:

| Path | Contents |
|------|----------|
| `%LOCALAPPDATA%\MemoryTimeline\memory-timeline.db` | SQLite database (WAL mode; expect `-wal` / `-shm` sidecar files) |
| `%LOCALAPPDATA%\MemoryTimeline\AudioRecordings\` | Recorded / imported audio files |
| `%LOCALAPPDATA%\MemoryTimeline\Models\ggml-base.bin` | Local Whisper (Whisper.net) speech-to-text model, downloaded on first use |
| `%LOCALAPPDATA%\MemoryTimeline\error.log` | Startup / unhandled-exception log |

**Schema / migrations:** the EF Core migrations were removed in favour of a hand-rolled `SchemaUpgrader` that creates/updates the schema at startup. There is currently **no migrations baseline**, so `dotnet ef migrations add` / `dotnet ef database update` are **not applicable** to this project right now (regenerating a real migration baseline is a follow-up task).

**External services:** speech-to-text is **local** (Whisper.net, offline after the one-time model download). LLM event extraction uses the Anthropic API and embeddings use the OpenAI API, so those features require API keys and network access.

---

## Running Tests

> **Do not use `dotnet test`.** `dotnet test` rebuilds the solution — including the WinUI app — with the `dotnet` MSBuild engine, which hits the same WinUI PRI/XAML failure described above. Instead, build the solution once with `msbuild.exe` (see [Building the Application](#building-the-application)) and run the **already-built** test assembly with `dotnet vstest`. This is exactly what CI does.

### Build once, then run the built test assembly
```powershell
cd windows-native/src

# 1. Build the solution (Release | x64) with VS MSBuild
msbuild MemoryTimeline.sln /t:Restore,Build /p:Configuration=Release /p:Platform=x64 /m

# 2. Run the compiled test DLL with vstest (no rebuild)
dotnet vstest MemoryTimeline.Tests/bin/x64/Release/net8.0-windows10.0.26100.0/MemoryTimeline.Tests.dll `
  --logger:"trx;LogFileName=test.trx" --ResultsDirectory:TestResults
```

### Filtering
`dotnet vstest` supports test filtering via `--TestCaseFilter`, e.g.:
```powershell
dotnet vstest <path-to>\MemoryTimeline.Tests.dll --TestCaseFilter:"FullyQualifiedName~UnitTests"
dotnet vstest <path-to>\MemoryTimeline.Tests.dll --TestCaseFilter:"FullyQualifiedName~Integration"
```

See [`TESTING.md`](./TESTING.md) for the test-suite layout, the EF Core InMemory / SQLite provider caveats, and known follow-up work.

### Coverage goals
- Aspirational target: **> 80%** overall, **> 90%** on critical paths (event CRUD, audio/queue processing, RAG). These are goals, not measured/verified figures.

---

## MSIX Packaging (Phase 7 — in progress)

> The app currently ships **unpackaged** (`WindowsPackageType=None`). The MSIX packaging, code-signing, and Store steps below describe the **intended** path and are not yet a verified, shipped pipeline. Packaging the app will also require reconciling the unpackaged/self-contained settings with an MSIX packaging project.

### Manual Packaging with Visual Studio

1. **Add Packaging Project**
   ```
   File → Add → New Project → Windows Application Packaging Project
   ```

2. **Configure Package Manifest**
   - Use the provided `packaging/Package.appxmanifest`
   - Update Identity Publisher with your certificate CN
   - Set appropriate version number

3. **Add Application Reference**
   - Right-click packaging project → Add → Reference
   - Select MemoryTimeline project

4. **Create Package**
   ```
   Right-click packaging project → Publish → Create App Packages
   ```

### Command-Line Packaging

```powershell
# Set environment variables
$AppxManifestPath = ".\packaging\Package.appxmanifest"
$OutputPath = ".\output\packages"
$MappingFile = ".\packaging\FileMapping.txt"

# Create package using makeappx
makeappx pack /d ".\bin\Release\net8.0-windows\" /p "$OutputPath\MemoryTimeline.msix" /l

# For app bundle (multiple architectures)
makeappx bundle /d "$OutputPath\bundles" /p "$OutputPath\MemoryTimeline.msixbundle"
```

### Package Contents Verification
```powershell
# List package contents
makeappx unpack /p MemoryTimeline.msix /d .\unpacked /l

# Validate package
certutil -hashfile MemoryTimeline.msix SHA256
```

---

## Code Signing

### Requirements
- **EV Code Signing Certificate** (required for Microsoft Store)
- Valid timestamp server URL
- SignTool.exe (included with Windows SDK)

### Signing Command
```powershell
# Set certificate thumbprint
$CertThumbprint = "YOUR_CERT_THUMBPRINT"

# Sign the package
signtool sign /fd SHA256 /sha1 $CertThumbprint /t http://timestamp.digicert.com /v MemoryTimeline.msix

# Verify signature
signtool verify /pa MemoryTimeline.msix
```

### Certificate Requirements
- **Type**: EV (Extended Validation) Code Signing Certificate
- **Algorithm**: SHA256 or higher
- **Validity**: Must be valid at time of signing
- **Trusted Root**: Must chain to a trusted root authority

### Acquiring a Certificate
1. Purchase from authorized CA (DigiCert, Sectigo, GlobalSign)
2. Complete identity verification process
3. Install on signing machine (hardware token or HSM)
4. Export thumbprint for automation

---

## Microsoft Store Deployment (Phase 7 — in progress)

> Store submission has not been performed yet. The steps below are the planned process once MSIX packaging and signing are in place.

### 1. Partner Center Setup

1. **Create Developer Account**
   - Go to [Microsoft Partner Center](https://partner.microsoft.com)
   - Register as individual or company
   - Pay registration fee ($19 one-time for individual, $99 for company)

2. **Reserve App Name**
   - Apps → Create a new app
   - Reserve "Memory Timeline"
   - Note the Identity details for manifest

### 2. App Submission Preparation

#### Required Assets
```
Assets/
├── StoreLogo.png (50x50)
├── Square44x44Logo.png (44x44)
├── Square150x150Logo.png (150x150)
├── Square310x310Logo.png (310x310)
├── Wide310x150Logo.png (310x150)
├── SmallTile.png (71x71)
├── SplashScreen.png (620x300)
└── Screenshots/
    ├── Desktop_1920x1080_1.png
    ├── Desktop_1920x1080_2.png
    ├── Desktop_1920x1080_3.png
    └── Desktop_1920x1080_4.png
```

#### Store Listing Information
- **Description** (minimum 200 characters)
- **Features** (5-10 bullet points)
- **Screenshots** (minimum 1, recommended 4+)
- **Privacy Policy URL** (required)
- **Support Contact** (email or website)
- **Age Rating** (ESRB, PEGI, etc.)
- **Category** (Productivity)

### 3. Package Upload

1. **Create Submission**
   ```
   Partner Center → Your App → Start submission
   ```

2. **Upload Package**
   - Upload signed `.msix` or `.msixbundle`
   - System will validate:
     - Digital signature
     - Manifest validity
     - API usage compliance
     - Content policy compliance

3. **Configure Properties**
   - Display name: "Memory Timeline"
   - Category: Productivity
   - Pricing: Free (or set price)
   - Markets: Select target countries
   - Age rating: Complete questionnaire

4. **Submit for Certification**
   - Review all sections
   - Submit for review
   - Typical review time: 1-3 business days

### 4. Certification Process

Microsoft will test:
- **Security**: Malware scan, signature validation
- **Performance**: Launch time, memory usage, CPU usage
- **Compatibility**: Windows version compatibility
- **Content**: Policy compliance
- **Functionality**: Basic app functionality

### 5. Publishing

Once approved:
- App goes live in Microsoft Store (usually within 24 hours)
- Users can download and install
- Auto-updates handled by Microsoft Store

---

## Side-loading

For enterprise deployment or testing without Microsoft Store.

### 1. Enable Side-loading on Target Machine

```powershell
# Check if side-loading is enabled
Get-AppxPackage -Name "*YourPublisher*"

# Enable Developer Mode (Settings → Update & Security → For developers)
# Or use PowerShell (requires admin)
reg add "HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock" /t REG_DWORD /f /v "AllowDevelopmentWithoutDevLicense" /d "1"
```

### 2. Install Certificate

```powershell
# Import signing certificate to trusted root (admin required)
$cert = Get-PfxCertificate -FilePath ".\SigningCert.pfx"
Import-Certificate -CertStoreLocation Cert:\LocalMachine\Root -Certificate $cert
```

### 3. Install Package

```powershell
# Using Add-AppxPackage
Add-AppxPackage -Path ".\MemoryTimeline.msix"

# With dependencies
Add-AppxPackage -Path ".\MemoryTimeline.msix" -DependencyPath ".\Dependencies\*.appx"

# Verify installation
Get-AppxPackage -Name "MemoryTimeline"
```

### 4. PowerShell Install Script

Create `Install.ps1`:

```powershell
#Requires -RunAsAdministrator

param(
    [string]$PackagePath = ".\MemoryTimeline.msix",
    [string]$CertificatePath = ".\SigningCert.cer"
)

Write-Host "Installing Memory Timeline..." -ForegroundColor Green

# Check if package exists
if (-not (Test-Path $PackagePath)) {
    Write-Error "Package not found: $PackagePath"
    exit 1
}

# Install certificate
if (Test-Path $CertificatePath) {
    Write-Host "Installing certificate..."
    Import-Certificate -CertStoreLocation Cert:\LocalMachine\Root -FilePath $CertificatePath
}

# Install package
Write-Host "Installing package..."
try {
    Add-AppxPackage -Path $PackagePath -ErrorAction Stop
    Write-Host "Installation successful!" -ForegroundColor Green
    Write-Host "You can now launch Memory Timeline from the Start menu."
}
catch {
    Write-Error "Installation failed: $_"
    exit 1
}
```

### 5. Uninstall Script

Create `Uninstall.ps1`:

```powershell
#Requires -RunAsAdministrator

$AppName = "MemoryTimeline"

Write-Host "Uninstalling $AppName..." -ForegroundColor Yellow

$package = Get-AppxPackage -Name "*$AppName*"

if ($package) {
    Remove-AppxPackage -Package $package.PackageFullName
    Write-Host "Uninstallation successful!" -ForegroundColor Green
}
else {
    Write-Host "$AppName is not installed." -ForegroundColor Yellow
}
```

---

## CI/CD Pipeline

### Current workflow: `.github/workflows/windows-native-build.yml`

The repository has a **real** GitHub Actions workflow that compiles and tests the native Windows implementation. Its key design decisions mirror the constraints described above:

- Runs on **`windows-latest`** (the WinUI target cannot build on Linux).
- Installs the **.NET 8 SDK** (`actions/setup-dotnet`) and **Visual Studio MSBuild** (`microsoft/setup-msbuild`).
- Builds the **whole solution for `Release | x64`** via `msbuild MemoryTimeline.sln /t:Restore,Build` — **not** `dotnet build`, to avoid the WinUI PRI/XAML task failure.
- Runs tests with **`dotnet vstest`** against the already-built `MemoryTimeline.Tests.dll` — **not** `dotnet test`, which would rebuild the WinUI app and hit the same failure.
- Compilation is the gate; the headless VSTest run of the self-contained WinUI test assembly is best-effort (`continue-on-error`) and its results/logs are uploaded as artifacts.

Core of the build and test steps:

```yaml
- name: Setup .NET 8
  uses: actions/setup-dotnet@v4
  with:
    dotnet-version: '8.0.x'

- name: Setup MSBuild (Visual Studio)
  uses: microsoft/setup-msbuild@v2

- name: Build (Release | x64) via VS MSBuild
  shell: pwsh
  run: |
    msbuild MemoryTimeline.sln /t:Restore,Build `
      /p:Configuration=Release /p:Platform=x64 /m /v:minimal

- name: Test (Release | x64)
  shell: pwsh
  run: |
    $dll = Get-ChildItem -Recurse -Path MemoryTimeline.Tests/bin/x64/Release `
             -Filter MemoryTimeline.Tests.dll | Select-Object -First 1
    dotnet vstest $dll.FullName --logger:"trx;LogFileName=test.trx" `
      --ResultsDirectory:TestResults
```

(The working directory is `windows-native/src`.)

### Not yet automated
Packaging, signing, and Store publishing are **not** part of the current workflow — they are Phase 7 work in progress (see the MSIX and Microsoft Store sections above).

---

## Post-Deployment

### Monitoring
- Monitor Partner Center for crash reports
- Review user feedback and ratings
- Track download statistics
- Monitor API usage and costs for the cloud features (Anthropic LLM extraction, OpenAI embeddings). Note: speech-to-text runs locally via Whisper.net and incurs no per-use API cost.

### Updates
1. Increment version in `Package.appxmanifest`
2. Build and test new version
3. Create new submission in Partner Center
4. Upload new package
5. Microsoft Store handles auto-updates for users

### Rollback
If critical issues found:
1. Suspend availability in Partner Center
2. Roll back to previous version
3. Fix issues and resubmit

---

## Troubleshooting

### Common Issues

**Certificate Not Trusted**
```powershell
# Solution: Install certificate to trusted root
Import-Certificate -CertStoreLocation Cert:\LocalMachine\Root -FilePath SigningCert.cer
```

**Package Installation Failed**
```powershell
# Check logs
Get-AppxLog -ActivityId <ID>

# Common fixes:
# 1. Ensure certificate is trusted
# 2. Check Windows version compatibility
# 3. Verify package integrity
```

**Store Submission Rejected**
- Review certification report in Partner Center
- Common reasons:
  - Invalid manifest
  - Missing privacy policy
  - Content policy violations
  - Performance issues
  - Incomplete metadata

---

## Resources

- [Windows App SDK Documentation](https://docs.microsoft.com/windows/apps/windows-app-sdk/)
- [MSIX Packaging Documentation](https://docs.microsoft.com/windows/msix/)
- [Microsoft Partner Center](https://partner.microsoft.com/dashboard)
- [Code Signing Best Practices](https://docs.microsoft.com/windows/win32/seccrypto/cryptography-tools)

---

## Support

For deployment issues:
- Email: support@memorytimeline.com
- GitHub Issues: https://github.com/yourusername/memory-timeline/issues
- Documentation: https://docs.memorytimeline.com

---

**Last Updated**: 2026-07-10
