# FFmpeg with NVENC Setup Guide

## Quick Setup

1. **Download FFmpeg with NVENC:**
   - Visit: https://github.com/BtbN/FFmpeg-Builds/releases/latest
   - Download: `ffmpeg-master-latest-win64-gpl.zip`
   - Extract the zip file
   - Copy `ffmpeg.exe` from the `bin` folder to this directory:
     ```
     C:\Projects\PC Remote Tools\windows-server\Server\ffmpeg.exe
     ```

2. **Verify NVENC Support:**
   Open PowerShell in the Server directory and run:
   ```powershell
   .\ffmpeg.exe -encoders | findstr nvenc
   ```
   You should see `h264_nvenc` in the output.

3. **Start the Server:**
   The server will automatically detect and use NVENC if available.
   You'll see in the console:
   ```
   NVENC hardware encoder detected - will use for encoding
   Using NVENC hardware encoder
   ```

## Direct Download Link

https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip

## Requirements

- NVIDIA GPU with NVENC support (Kepler series or newer - RTX 3080 is perfect!)
- Latest NVIDIA drivers installed
- FFmpeg with NVENC compiled in (BtbN builds include it)

## Troubleshooting

If NVENC is not detected:
- Make sure NVIDIA drivers are up to date
- Verify GPU supports NVENC (RTX 3080 definitely does)
- Check that `ffmpeg.exe` is in the Server directory
- The server will automatically fall back to JPEG encoding if NVENC is unavailable
