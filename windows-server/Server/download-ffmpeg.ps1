# PowerShell script to download FFmpeg with NVENC support
# Uses BtbN builds which include NVENC support

$ErrorActionPreference = "Stop"

Write-Host "Downloading FFmpeg with NVENC support..."
Write-Host ""

$serverDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$ffmpegPath = Join-Path $serverDir "ffmpeg.exe"

# Check if already exists
if (Test-Path $ffmpegPath) {
    Write-Host "FFmpeg already exists at: $ffmpegPath"
    Write-Host "Checking NVENC support..."
    $output = & $ffmpegPath -encoders 2>&1 | Out-String
    if ($output -match "h264_nvenc") {
        Write-Host "FFmpeg with NVENC support is already installed!"
        exit 0
    } else {
        Write-Host "FFmpeg exists but NVENC support not found. Downloading new version..."
        Remove-Item $ffmpegPath -Force
    }
}

# Download from BtbN (includes NVENC)
$downloadUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip"
$zipPath = Join-Path $env:TEMP "ffmpeg-nvenc.zip"

Write-Host "Downloading FFmpeg with NVENC support from BtbN..."
Write-Host "URL: $downloadUrl"
Write-Host ""

try {
    # Download using Invoke-WebRequest
    Invoke-WebRequest -Uri $downloadUrl -OutFile $zipPath -UseBasicParsing
    
    Write-Host "Extracting FFmpeg..."
    
    # Extract zip
    $extractPath = Join-Path $env:TEMP "ffmpeg-extract"
    if (Test-Path $extractPath) {
        Remove-Item $extractPath -Recurse -Force
    }
    Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force
    
    # Find ffmpeg.exe in the extracted folder
    $ffmpegInZip = Get-ChildItem -Path $extractPath -Filter "ffmpeg.exe" -Recurse | Select-Object -First 1
    
    if ($ffmpegInZip) {
        Copy-Item $ffmpegInZip.FullName -Destination $ffmpegPath -Force
        Write-Host "FFmpeg copied to: $ffmpegPath"
        
        # Verify NVENC support
        Write-Host ""
        Write-Host "Verifying NVENC support..."
        $output = & $ffmpegPath -encoders 2>&1 | Out-String
        if ($output -match "h264_nvenc") {
            Write-Host "NVENC support confirmed!"
            Write-Host ""
            Write-Host "FFmpeg with NVENC is ready to use!"
        } else {
            Write-Host "Warning: NVENC support not found. You may need to install NVIDIA drivers."
        }
        
        # Cleanup
        Remove-Item $zipPath -Force
        Remove-Item $extractPath -Recurse -Force
        
        Write-Host ""
        Write-Host "Setup complete!"
    } else {
        throw "ffmpeg.exe not found in downloaded archive"
    }
} catch {
    Write-Host "Error: $_" -ForegroundColor Red
    Write-Host ""
    Write-Host "Alternative: Download manually from:"
    Write-Host "  https://www.gyan.dev/ffmpeg/builds/"
    Write-Host "  Look for builds with NVENC support"
    Write-Host ""
    Write-Host "Or build from source with:"
    Write-Host "  --enable-nvenc --enable-cuda"
    exit 1
}
