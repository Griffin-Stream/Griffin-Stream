<#
.SYNOPSIS
    Builds release artifacts for the Griffin Stream Windows server:
      1. Self-contained win-x64 publish (.NET runtime + ffmpeg bundled)
      2. Optional Authenticode code signing of the executables
      3. Portable .zip
      4. Inno Setup installer (if Inno Setup is installed), optionally signed

.PARAMETER Version
    Version stamped into artifact names and the installer. Default: 1.0.0

.PARAMETER CertPath
    Path to a code-signing certificate (.pfx). If supplied (with CertPassword),
    Server.exe and the generated installer are signed with signtool.

.PARAMETER CertPassword
    Password for the .pfx certificate.

.PARAMETER TimestampUrl
    RFC3161 timestamp server URL used during signing.

.EXAMPLE
    .\build-release.ps1 -Version 1.0.0
    .\build-release.ps1 -Version 1.0.0 -CertPath C:\keys\codesign.pfx -CertPassword 'secret'
#>
param(
    [string]$Version = "1.0.0",
    [string]$CertPath = "",
    [string]$CertPassword = "",
    [string]$TimestampUrl = "http://timestamp.digicert.com"
)

$ErrorActionPreference = 'Stop'
$windowsServerDir = $PSScriptRoot                          # ...\PC Remote Tools\windows-server
$root             = Split-Path -Parent $windowsServerDir   # repository root: ...\PC Remote Tools
$serverProj       = Join-Path $windowsServerDir "Server\Server.csproj"
$installerDir     = Join-Path $windowsServerDir "installer"
$distDir          = Join-Path $root "dist"
$publishDir       = Join-Path $distDir "server"

Write-Host "== Griffin Stream Server release build (v$Version) ==" -ForegroundColor Cyan

# --- 1. Publish (self-contained, single folder, ffmpeg bundled) ---
Write-Host "`n[1/4] Publishing self-contained win-x64..." -ForegroundColor Yellow
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
dotnet publish $serverProj -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=false -o $publishDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed." }

function Invoke-Sign([string]$file) {
    if (-not $CertPath) { return }
    $signtool = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if (-not $signtool) {
        $signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
                    Sort-Object FullName -Descending | Select-Object -First 1
    }
    if (-not $signtool) { Write-Warning "signtool.exe not found; skipping signing of $file"; return }
    $exe = if ($signtool.Source) { $signtool.Source } else { $signtool.FullName }
    & $exe sign /fd SHA256 /f $CertPath /p $CertPassword /tr $TimestampUrl /td SHA256 $file
    if ($LASTEXITCODE -ne 0) { throw "Signing failed for $file" }
    Write-Host "  signed: $file"
}

# --- 2. Optional code signing of the server executable ---
Write-Host "`n[2/4] Code signing..." -ForegroundColor Yellow
if ($CertPath) {
    Invoke-Sign (Join-Path $publishDir "Server.exe")
} else {
    Write-Host "  (no -CertPath provided; skipping. See docs\WINDOWS_DISTRIBUTION.md)"
}

# --- 3. Portable zip ---
Write-Host "`n[3/4] Creating portable zip..." -ForegroundColor Yellow
$zipPath = Join-Path $distDir "GriffinStreamServer-$Version-portable.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path (Join-Path $publishDir '*') -DestinationPath $zipPath
Write-Host "  created: $zipPath"

# --- 4. Inno Setup installer (optional, requires Inno Setup) ---
Write-Host "`n[4/4] Building installer..." -ForegroundColor Yellow
$iscc = Get-Command iscc.exe -ErrorAction SilentlyContinue
if (-not $iscc) {
    foreach ($p in @("C:\Program Files (x86)\Inno Setup 6\ISCC.exe", "C:\Program Files\Inno Setup 6\ISCC.exe", "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe")) {
        if (Test-Path $p) { $iscc = Get-Item $p; break }
    }
}
if ($iscc) {
    $isccPath = if ($iscc.Source) { $iscc.Source } else { $iscc.FullName }
    & $isccPath "/DAppVersion=$Version" (Join-Path $installerDir "griffin-server.iss")
    if ($LASTEXITCODE -ne 0) { throw "Inno Setup compilation failed." }
    # Stable, unversioned filename so griffinstream.app/download -> GitHub "latest" always resolves.
    $setupExe = Join-Path $distDir "GriffinStreamServer-Setup.exe"
    Invoke-Sign $setupExe
    Write-Host "  installer: $setupExe"
} else {
    Write-Warning "Inno Setup (ISCC.exe) not found. Install it from https://jrsoftware.org/isdl.php then re-run."
    Write-Host    "Portable zip was still produced at: $zipPath"
}

Write-Host "`nDone. Artifacts in: $distDir" -ForegroundColor Green
