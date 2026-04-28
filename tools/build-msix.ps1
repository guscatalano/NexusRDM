# Builds a signed .msix of NexusRDM. Default flow (no args):
#
#   1. Generates a self-signed dev cert if one doesn't exist yet
#      (CN=Gus Catalano — matches Package.appxmanifest <Publisher>).
#   2. Resizes Assets\AppIcon.ico into the five tile/splash PNGs the
#      manifest references, when they're missing.
#   3. Calls msbuild with /p:MsixPackage=true to produce the package.
#
# For real release builds, point at a trusted cert via -Thumbprint
# and skip the dev-cert step:
#
#   tools\build-msix.ps1 -Thumbprint <your-cert-thumbprint>
#
# Output lands in artifacts\msix\.

[CmdletBinding()]
param(
    # Thumbprint of an installed code-signing cert (LocalMachine\My
    # or CurrentUser\My). When omitted, a self-signed dev cert is
    # used / created — fine for sideload-on-this-box testing, not
    # for redistribution.
    [string]$Thumbprint,

    # Configuration / Platform passed straight through to msbuild.
    [string]$Configuration = "Release",
    [string]$Platform      = "x64",

    # Where to look for / create the dev cert when -Thumbprint
    # isn't supplied. Default matches the Store-assigned Publisher
    # in Package.appxmanifest — sideload installs require the
    # signing cert's subject to equal the manifest <Identity Publisher>.
    [string]$DevCertSubject = "CN=119E0257-3B74-437C-A728-AC7C50256853",

    # Submission build for the Microsoft Store. Skips signing: the
    # Store re-signs with its own cert during certification, and an
    # uploaded package signed by the publisher would be rejected.
    # Outputs an .msixupload-friendly bundle.
    [switch]$ForStore,

    # Four-segment version (Major.Minor.Build.Revision). When set,
    # the script patches Package.appxmanifest's Identity Version
    # before building so every CI run produces a unique package
    # version (Store rejects re-uploads of an already-accepted
    # version). Empty = leave manifest as-is.
    [string]$Version
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$proj     = Join-Path $repoRoot "src\NexusRDM\NexusRDM.csproj"
$assets   = Join-Path $repoRoot "src\NexusRDM\Assets"
$manifest = Join-Path $repoRoot "src\NexusRDM\Package.appxmanifest"

# ── Step 0: version stamp (if requested) ────────────────────────────

if ($Version) {
    if ($Version -notmatch '^\d+\.\d+\.\d+\.\d+$') {
        throw "Version must be Major.Minor.Build.Revision (e.g. 1.0.0.42), got '$Version'."
    }
    Write-Host "Stamping Package.appxmanifest with Version=$Version"
    # Read raw text + regex-replace the Version attribute. We
    # deliberately don't load the XML through XmlDocument because the
    # manifest's xml-namespace declarations get reformatted and break
    # MSBuild's XML diff against the cached AppxManifest item — the
    # rebuild ends up clobbering modifications.
    $xml = Get-Content -LiteralPath $manifest -Raw
    $xml = [System.Text.RegularExpressions.Regex]::Replace(
        $xml,
        '(<Identity\b[^>]*\bVersion=")[^"]*(")',
        "`${1}$Version`${2}",
        [System.Text.RegularExpressions.RegexOptions]::Singleline)
    Set-Content -LiteralPath $manifest -Value $xml -NoNewline
}

# ── Step 1: dev cert (if needed) ────────────────────────────────────

# Store submissions skip local signing entirely — the Store re-signs
# with its own cert during certification, and uploading a package
# signed by the publisher fails ingestion. Just build and upload the
# unsigned .msixupload that comes out of artifacts\msix\.
if ($ForStore) {
    Write-Host "ForStore mode: skipping local sign."
    $Thumbprint = $null
}
elseif (-not $Thumbprint) {
    $existing = Get-ChildItem Cert:\CurrentUser\My |
        Where-Object { $_.Subject -eq $DevCertSubject } |
        Select-Object -First 1
    if (-not $existing) {
        Write-Host "Creating self-signed dev cert ($DevCertSubject)…"
        $existing = New-SelfSignedCertificate `
            -Type CodeSigningCert `
            -Subject $DevCertSubject `
            -KeyUsage DigitalSignature `
            -FriendlyName "NexusRDM Dev Cert" `
            -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.3", "2.5.29.19={text}")
        Write-Host "  Thumbprint: $($existing.Thumbprint)"
        Write-Host "  Trust it locally before installing the .msix:"
        Write-Host "    Export-Certificate -Cert Cert:\CurrentUser\My\$($existing.Thumbprint) -FilePath nexusrdm-dev.cer"
        Write-Host "    Import-Certificate -FilePath nexusrdm-dev.cer -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    }
    $Thumbprint = $existing.Thumbprint
}

# ── Step 2: tile / splash PNGs ──────────────────────────────────────

# Manifest references Square44x44Logo.png, Square150x150Logo.png,
# Wide310x150Logo.png, StoreLogo.png (50x50), SplashScreen.png
# (620x300). When any are missing we synthesize them from AppIcon.ico
# so the package builds without external asset work.

Add-Type -AssemblyName System.Drawing

function New-PngFromIco {
    param([string]$IcoPath, [string]$OutPath, [int]$Width, [int]$Height,
          [System.Drawing.Color]$Background)
    if (Test-Path $OutPath) { return }
    Write-Host "  → $OutPath ($Width x $Height)"
    $bitmap = New-Object System.Drawing.Bitmap($Width, $Height)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $g.Clear($Background)
    $g.SmoothingMode     = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode   = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $icon  = New-Object System.Drawing.Icon($IcoPath, ([Math]::Min($Width, $Height)), ([Math]::Min($Width, $Height)))
    $iconBmp = $icon.ToBitmap()
    # Center the icon at ~70% size on the tile so there's breathing
    # room around the glyph (mirrors Microsoft's tile guidelines).
    $iconSize = [Math]::Min($Width, $Height) * 0.7
    $x = ($Width  - $iconSize) / 2
    $y = ($Height - $iconSize) / 2
    $g.DrawImage($iconBmp, $x, $y, $iconSize, $iconSize)
    $bitmap.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bitmap.Dispose(); $iconBmp.Dispose(); $icon.Dispose()
}

$ico = Join-Path $assets "AppIcon.ico"
if (-not (Test-Path $ico)) { throw "AppIcon.ico not found at $ico" }

# Brand background. Adjust to match your palette if you want a
# different tile color in Start.
$bg = [System.Drawing.Color]::FromArgb(255, 30, 30, 40)

Write-Host "Generating tile / splash assets…"
New-PngFromIco $ico (Join-Path $assets "Square44x44Logo.png")  44   44   $bg
New-PngFromIco $ico (Join-Path $assets "Square150x150Logo.png") 150 150 $bg
New-PngFromIco $ico (Join-Path $assets "Wide310x150Logo.png")   310 150 $bg
New-PngFromIco $ico (Join-Path $assets "StoreLogo.png")          50  50 $bg
New-PngFromIco $ico (Join-Path $assets "SplashScreen.png")      620 300 $bg

# ── Step 3: msbuild ─────────────────────────────────────────────────

$msbuild = "C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    # Fall back to whichever msbuild is on PATH.
    $msbuild = "msbuild"
}

Write-Host "Building MSIX…"
$msbuildArgs = @(
    $proj,
    "/p:MsixPackage=true",
    "/p:Configuration=$Configuration",
    "/p:Platform=$Platform",
    "/v:minimal", "/nologo"
)
if ($ForStore) {
    # Store ingestion requires unsigned input (it signs); also emit
    # an .msixupload bundle which is what Partner Center accepts.
    $msbuildArgs += "/p:AppxPackageSigningEnabled=false"
    $msbuildArgs += "/p:GenerateAppxUploadOnPackagingFinished=true"
    $msbuildArgs += "/p:UapAppxPackageBuildMode=StoreUpload"
} else {
    $msbuildArgs += "/p:PackageCertificateThumbprint=$Thumbprint"
}
& $msbuild @msbuildArgs

if ($LASTEXITCODE -ne 0) {
    throw "msbuild failed with exit code $LASTEXITCODE"
}

$out = Join-Path $repoRoot "artifacts\msix"
Write-Host ""
Write-Host "Done. Package(s) under:"
Write-Host "  $out"
Get-ChildItem $out -Recurse -Include *.msix, *.msixbundle -ErrorAction SilentlyContinue |
    ForEach-Object { Write-Host "  $($_.FullName)" }
