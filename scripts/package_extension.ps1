# ImageRotater Extension Packaging Script
# Creates a .pext package for Playnite installation
#
# Usage: .\package_extension.ps1 [-Configuration Release|Debug]
#
# Note: This packages an already-built project. Build first with:
#   dotnet build -c Release

param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "  ImageRotater Extension Packaging" -ForegroundColor Green
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Configuration: $Configuration" -ForegroundColor Yellow
Write-Host ""

# Project root is one level up from scripts/
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $scriptDir
Set-Location $projectRoot

# version.txt is the single source of truth; extension.yaml and AssemblyInfo.cs
# are rewritten from it so they can never drift.
$versionFile = Join-Path $projectRoot "version.txt"
if (-not (Test-Path $versionFile)) {
    Write-Host "ERROR: version.txt not found." -ForegroundColor Red
    exit 1
}
$versionFull = (Get-Content $versionFile -Raw).Trim()
$version = $versionFull -replace '\.', '_'

$extensionName = "ImageRotater"
$extensionId = "72b7d457-0621-429b-8368-665bc53ff896"
$outputDir = "src\bin\$Configuration\net4.6.2"
# Absolute, anchored to the project root rather than the caller's working
# directory. As a bare relative path this resolved against wherever the shell
# happened to be, so packaging from anywhere but the project root wrote the
# staging folder somewhere else and then failed to validate a manifest it had
# just written.
$packageDir = Join-Path $projectRoot "package"

# Writes UTF-8 with NO byte-order mark. Windows PowerShell 5.1's `-Encoding utf8`
# always emits a BOM (utf8NoBOM only exists in PowerShell 6+), and a leading BOM
# makes Playnite fail to parse extension.yaml — the first key becomes unreadable.
function Write-Utf8NoBom {
    param([string]$Path, [string]$Content)
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($Path, $Content, $utf8NoBom)
}

# Stamp AssemblyInfo.cs
$assemblyInfoPath = Join-Path $projectRoot "src\AssemblyInfo.cs"
if (Test-Path $assemblyInfoPath) {
    $c = Get-Content $assemblyInfoPath -Raw
    $c = $c -replace '\[assembly:\s*AssemblyVersion\("[\d\.]+"\)\]', "[assembly: AssemblyVersion(`"$versionFull`")]"
    $c = $c -replace '\[assembly:\s*AssemblyFileVersion\("[\d\.]+"\)\]', "[assembly: AssemblyFileVersion(`"$versionFull`")]"
    $c = $c -replace '\[assembly:\s*AssemblyInformationalVersion\("[\d\.]+"\)\]', "[assembly: AssemblyInformationalVersion(`"$versionFull`")]"
    Write-Utf8NoBom -Path $assemblyInfoPath -Content $c
    Write-Host "Stamped AssemblyInfo.cs -> $versionFull" -ForegroundColor Gray
}

# Stamp extension.yaml
$manifestPath = Join-Path $projectRoot "extension.yaml"
if (Test-Path $manifestPath) {
    $m = Get-Content $manifestPath -Raw
    $m = $m -replace '(?m)^Version:\s*[\d\.]+\s*$', "Version: $versionFull"
    Write-Utf8NoBom -Path $manifestPath -Content $m
    Write-Host "Stamped extension.yaml -> $versionFull" -ForegroundColor Gray
}

# Verify the build output exists before packaging anything
$dllPath = Join-Path $outputDir "$extensionName.dll"
if (-not (Test-Path $dllPath)) {
    Write-Host ""
    Write-Host "ERROR: $dllPath not found. Build first:" -ForegroundColor Red
    Write-Host "  dotnet build -c $Configuration" -ForegroundColor Yellow
    exit 1
}

$dllVersion = (Get-Item $dllPath).VersionInfo.FileVersion
Write-Host "Built DLL version: $dllVersion" -ForegroundColor Gray

# FAIL, do not warn.
#
# Stamping happens above, so a build that ran BEFORE this script produced a DLL
# carrying the PREVIOUS version - the stamp only takes effect on the next build.
# This was previously a warning, and the resulting .pext shipped stale code that
# still reported the old version in its own log. Hours were spent testing
# behaviour that was not in the assembly being run.
if ($dllVersion -and $dllVersion -notlike "$versionFull*") {
    Write-Host ""
    Write-Host "ERROR: DLL is $dllVersion but version.txt says $versionFull." -ForegroundColor Red
    Write-Host "       The stamp above only applies to the NEXT build. Re-run:" -ForegroundColor Yellow
    Write-Host "         dotnet build -c $Configuration" -ForegroundColor Yellow
    Write-Host "       then package again." -ForegroundColor Yellow
    exit 1
}

# Fresh staging directory
if (Test-Path $packageDir) { Remove-Item $packageDir -Recurse -Force }
New-Item -ItemType Directory -Path $packageDir -Force | Out-Null

# Build artifacts Playnite doesn't need in the package.
$excludePatterns = @("*.pdb", "*.xml", "*.config")

# Assemblies the Playnite host already provides at runtime. Shipping our own copy
# of Playnite.SDK.dll makes the plugin resolve SDK types from its own assembly
# instead of the host's, so casts across that boundary fail and Playnite rejects
# the extension. The WPF/BCL assemblies below are framework-provided for the same
# reason. This list mirrors the exclusions in the UniPlaySong packaging script.
$excludeDlls = @(
    "Playnite.SDK.dll",
    "System.Net.Http.dll",
    "WindowsBase.dll",
    "PresentationCore.dll",
    "PresentationFramework.dll"
)

Get-ChildItem -Path $outputDir -File | Where-Object {
    $name = $_.Name
    (-not ($excludePatterns | Where-Object { $name -like $_ })) -and
    ($excludeDlls -notcontains $name) -and
    ($name -notlike "System.*.dll")
} | ForEach-Object {
    Copy-Item $_.FullName -Destination $packageDir
}

# WebView2's native loader, which lives in a runtimes/ subtree rather than
# beside the managed DLLs - so the file copy above misses it entirely. Without
# it the managed wrapper ships fine and then cannot reach the runtime at all,
# and every animated preview silently falls back to a still frame.
$runtimesDir = Join-Path $outputDir "runtimes"
if (Test-Path $runtimesDir) {
    Copy-Item $runtimesDir -Destination $packageDir -Recurse -Force
    Write-Host "  (included WebView2 native loaders)" -ForegroundColor Gray
}

# Playnite reads these from the extension folder at runtime; they are loose
# files, not embedded in the DLL. Without them every label shows its raw key.
$localizationDir = Join-Path $outputDir "Localization"
if (Test-Path $localizationDir) {
    Copy-Item $localizationDir -Destination $packageDir -Recurse -Force
    Write-Host "  (included Localization files)" -ForegroundColor Gray
}

# extension.yaml and icon.png must sit at the package root
Copy-Item $manifestPath -Destination $packageDir -Force
$iconPath = Join-Path $projectRoot "icon.png"
if (Test-Path $iconPath) {
    Copy-Item $iconPath -Destination $packageDir -Force
} else {
    Write-Host "WARNING: icon.png not found at project root - Playnite will show a blank icon." -ForegroundColor Yellow
}
$licensePath = Join-Path $projectRoot "LICENSE"
if (Test-Path $licensePath) { Copy-Item $licensePath -Destination $packageDir -Force }

Write-Host ""
Write-Host "Package contents:" -ForegroundColor Cyan
Get-ChildItem -Path $packageDir -File | ForEach-Object {
    Write-Host ("  - {0} ({1:N2} KB)" -f $_.Name, ($_.Length / 1KB)) -ForegroundColor Gray
}

# Validate the staged package before zipping it. A package that Playnite refuses
# to load looks identical to a good one until you try to install it, so these
# checks fail the build rather than shipping something broken.
Write-Host ""
Write-Host "Validating package..." -ForegroundColor Cyan
$validationErrors = @()

# The manifest must not start with a UTF-8 BOM: Playnite's YAML parser reads the
# BOM as part of the first key and the manifest becomes unreadable.
$manifestInPackage = Join-Path $packageDir "extension.yaml"
if (Test-Path $manifestInPackage) {
    $firstBytes = [System.IO.File]::ReadAllBytes($manifestInPackage)
    if ($firstBytes.Length -ge 3 -and $firstBytes[0] -eq 0xEF -and $firstBytes[1] -eq 0xBB -and $firstBytes[2] -eq 0xBF) {
        $validationErrors += "extension.yaml starts with a UTF-8 BOM - Playnite cannot parse it."
    }
} else {
    $validationErrors += "extension.yaml is missing from the package root."
}

# Shipping the host's own assemblies breaks type identity across the plugin boundary.
foreach ($forbidden in $excludeDlls) {
    if (Test-Path (Join-Path $packageDir $forbidden)) {
        $validationErrors += "$forbidden must not be packaged - Playnite provides it at runtime."
    }
}

# The plugin assembly named by the manifest has to actually be there.
if (-not (Test-Path (Join-Path $packageDir "$extensionName.dll"))) {
    $validationErrors += "$extensionName.dll is missing from the package root."
}

# Playnite only merges localization files that sit loose in Localization\.
# Packaged into the DLL they do nothing, and every label renders as its key.
if (-not (Test-Path (Join-Path $packageDir "Localization\en_US.xaml"))) {
    $validationErrors += "Localization\en_US.xaml is missing - labels will show raw LOC keys."
}

if ($validationErrors.Count -gt 0) {
    Write-Host ""
    Write-Host "PACKAGE VALIDATION FAILED:" -ForegroundColor Red
    foreach ($err in $validationErrors) {
        Write-Host "  - $err" -ForegroundColor Red
    }
    exit 1
}

Write-Host "  All checks passed." -ForegroundColor Green

# Build the .pext (a zip with a different extension)
$pextDir = Join-Path $projectRoot "pext"
if (-not (Test-Path $pextDir)) { New-Item -ItemType Directory -Path $pextDir -Force | Out-Null }

$releaseAsset = Join-Path $pextDir "$extensionName-$version.pext"
$buildArtifact = Join-Path $pextDir ($extensionName + "." + $extensionId + "_" + $version + ".pext")

foreach ($target in @($releaseAsset, $buildArtifact)) {
    if (Test-Path $target) { Remove-Item $target -Force }
}

$tempZip = Join-Path $pextDir "$extensionName-$version.zip"
if (Test-Path $tempZip) { Remove-Item $tempZip -Force }

Write-Host ""
Write-Host "Creating .pext archive..." -ForegroundColor Cyan

# Compress-Archive can fail while a virus scanner still holds freshly-written DLLs.
# Retry briefly rather than failing the whole run.
$attempts = 0
$maxAttempts = 3
while ($true) {
    $attempts++
    try {
        Compress-Archive -Path "$packageDir\*" -DestinationPath $tempZip -Force
        break
    } catch {
        if ($attempts -ge $maxAttempts) {
            Write-Host "ERROR: Failed to create archive after $maxAttempts attempts." -ForegroundColor Red
            Write-Host $_.Exception.Message -ForegroundColor Red
            exit 1
        }
        Write-Host "  Archive attempt $attempts failed (file lock?), retrying..." -ForegroundColor Yellow
        Start-Sleep -Seconds 2
    }
}

Copy-Item $tempZip -Destination $releaseAsset -Force
Copy-Item $tempZip -Destination $buildArtifact -Force
Remove-Item $tempZip -Force

Write-Host ""
Write-Host "  PACKAGE CREATED SUCCESSFULLY!" -ForegroundColor Green
Write-Host "  Release asset:  $(Split-Path -Leaf $releaseAsset)" -ForegroundColor Gray
Write-Host "  Build artifact: $(Split-Path -Leaf $buildArtifact)" -ForegroundColor Gray
Write-Host "  Version: $versionFull" -ForegroundColor Gray
Write-Host ""
Write-Host "To install in Playnite:" -ForegroundColor Cyan
Write-Host "  1. Open Playnite"
Write-Host "  2. Add-ons -> Extensions"
Write-Host "  3. 'Add extension' and select the .pext file"
Write-Host ""
