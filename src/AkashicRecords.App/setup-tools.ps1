# Installs the command-line tools the Akashic Records downloader can drive:
#   - yt-dlp  (https://github.com/yt-dlp/yt-dlp)  - fetches media the user has the right to download
#   - ffmpeg  (https://github.com/yt-dlp/FFmpeg-Builds) - required for mp3 extraction / thumbnail embedding
# Both are open-source. This script only downloads their official release builds into a local "tools" folder
# next to the application. What you download with them, and respecting each source's terms of use, is up to you.
#
# Tools are installed into the build output directory (where the .exe lives), because the app resolves
# yt-dlp/ffmpeg from <exe>/tools/. Run this from anywhere.

$ErrorActionPreference = 'Stop'

# Resolve the build output directory (where AkashicRecords.App.exe is published).
$scriptDir = $PSScriptRoot
if ($scriptDir -like '*\src\AkashicRecords.App') {
    $repoAppDir = $scriptDir
} elseif ($scriptDir -like '*\AkashicRecords.App') {
    $repoAppDir = $scriptDir
} else {
    $candidate = Join-Path (Split-Path $scriptDir -Parent) 'AkashicRecords.App'
    $repoAppDir = if (Test-Path $candidate) { $candidate } else { Join-Path $scriptDir 'AkashicRecords.App' }
}

$buildOut = Join-Path $repoAppDir 'bin\Debug\net8.0-windows'
$tools = if (Test-Path $buildOut) { $buildOut } else { Join-Path $repoAppDir 'tools' }
New-Item -ItemType Directory -Force -Path $tools | Out-Null

Write-Host 'Downloading yt-dlp...'
Invoke-WebRequest -Uri 'https://github.com/yt-dlp/yt-dlp/releases/latest/download/yt-dlp.exe' `
    -OutFile (Join-Path $tools 'yt-dlp.exe')

Write-Host 'Downloading ffmpeg (this can take a minute)...'
$zip = Join-Path $env:TEMP 'akashic-ffmpeg.zip'
$extract = Join-Path $env:TEMP 'akashic-ffmpeg'
Invoke-WebRequest -Uri 'https://github.com/yt-dlp/FFmpeg-Builds/releases/latest/download/ffmpeg-master-latest-win64-gpl.zip' `
    -OutFile $zip

if (Test-Path $extract) { Remove-Item -Recurse -Force $extract }
Expand-Archive -Path $zip -DestinationPath $extract

foreach ($name in @('ffmpeg.exe', 'ffprobe.exe')) {
    $found = Get-ChildItem -Path $extract -Recurse -Filter $name | Select-Object -First 1
    if ($found) { Copy-Item $found.FullName (Join-Path $tools $name) -Force }
}

Remove-Item $zip -Force
Remove-Item -Recurse -Force $extract

Write-Host ''
Write-Host "Done. Tools installed in: $tools"
Write-Host 'Open Akashic Records -> Telechargements; it will detect these automatically.'
