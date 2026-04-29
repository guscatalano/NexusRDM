<#
.SYNOPSIS
    Build a Windows Installer (.msi) for NexusRDM via WiX 5.

.DESCRIPTION
    Two-step pipeline:
      1. msbuild /t:Publish — produces a self-contained x64 build
         under publish-msi/ that the installer harvests verbatim.
      2. wix build — feeds tools\installer\NexusRDM.wxs through
         the WiX 5 toolchain to emit the .msi.

    WiX is delivered as a dotnet global tool (`dotnet tool install
    --global wix`). First run installs it; subsequent runs reuse
    the cached toolchain. No system-wide WiX install is required.

    Output: artifacts\msi\NexusRDM-v<Version>-x64.msi

.PARAMETER Version
    Four-segment Major.Minor.Build.Revision used as the MSI
    ProductVersion. Mirrors what build-msix.ps1 expects, so CI
    can pass the same `numeric` output to both.
#>

param(
    [string]$Configuration = "Release",
    [string]$Platform      = "x64",
    [Parameter(Mandatory=$true)]
    [string]$Version
)

$ErrorActionPreference = "Stop"

if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "Version must be Major.Minor.Build.Revision (e.g. 1.0.0.42), got '$Version'."
}

$repoRoot   = Split-Path -Parent $PSScriptRoot
$proj       = Join-Path $repoRoot "src\NexusRDM\NexusRDM.csproj"
$publishDir = Join-Path $repoRoot "src\NexusRDM\publish-msi"
$wxs        = Join-Path $repoRoot "tools\installer\NexusRDM.wxs"
$outDir     = Join-Path $repoRoot "artifacts\msi"
$outFile    = Join-Path $outDir "NexusRDM-v$Version-x64.msi"

# ── Step 1: publish the app ─────────────────────────────────────────
# Self-contained so the installed app needs no external .NET
# runtime. Same shape as the release zip. We use a separate
# publish-msi\ directory to avoid colliding with parallel publish
# steps (release zip uses publish\).

$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) { $msbuild = "msbuild" }

Write-Host "Publishing app for MSI…"
& $msbuild $proj `
    /t:Publish `
    /p:Configuration=$Configuration `
    /p:Platform=$Platform `
    /p:RuntimeIdentifier=win-x64 `
    "/p:PublishDir=$publishDir\" `
    "/p:Version=$Version" `
    "/p:AssemblyVersion=$Version" `
    "/p:FileVersion=$Version" `
    "/p:InformationalVersion=$Version" `
    /v:minimal /nologo

if ($LASTEXITCODE -ne 0) { throw "msbuild publish failed with exit code $LASTEXITCODE" }

# ── Step 2: ensure WiX is installed ────────────────────────────────
# `dotnet tool install` errors if the tool is already installed,
# so we squelch stderr and inspect the exit code separately. The
# install path lands in $env:USERPROFILE\.dotnet\tools — make sure
# it's on PATH for this process.

$wixCheck = & dotnet tool list --global 2>$null | Select-String -SimpleMatch "wix "
if (-not $wixCheck) {
    Write-Host "Installing WiX 5 as a dotnet global tool…"
    & dotnet tool install --global wix --version 5.0.2
    if ($LASTEXITCODE -ne 0) { throw "wix tool install failed" }
}

$dotnetTools = Join-Path $env:USERPROFILE ".dotnet\tools"
# Parens are load-bearing: bare `Test-Path $x -and ...` makes
# PowerShell parse `-and` as a Test-Path parameter (which fails);
# wrapping forces it to evaluate the call as a sub-expression and
# THEN apply the logical -and.
if ((Test-Path $dotnetTools) -and ($env:PATH -notlike "*$dotnetTools*")) {
    $env:PATH = "$dotnetTools;$env:PATH"
}

# ── Step 3: compile the MSI ────────────────────────────────────────

New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Write-Host "Building MSI…"
& wix build $wxs `
    -d "PublishDir=$publishDir" `
    -d "ProductVersion=$Version" `
    -arch x64 `
    -out $outFile

if ($LASTEXITCODE -ne 0) { throw "wix build failed with exit code $LASTEXITCODE" }

Write-Host ""
Write-Host "Done. Installer:"
Write-Host "  $outFile"
Get-Item $outFile | ForEach-Object {
    Write-Host ("  Size: {0:N0} KB" -f ($_.Length / 1KB))
}
