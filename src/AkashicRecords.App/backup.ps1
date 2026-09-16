# Backs up the user data of Akashic Records to a timestamped folder.
#
# Backs up everything the app can't recreate:
#   - data\akashic.db        (all text, metadata, organization)
#   - music\                 (all audio files)
#   - media\                 (all imported images)
#   - config\config.json     (settings, API keys)
#
# Excludes the app binaries and the tools\ folder (yt-dlp/ffmpeg), which are
# re-downloadable and not user data.
#
# Usage:
#   .\backup.ps1                 # backup to the default location
#   .\backup.ps1 -Destination X  # backup to a specific folder
#   .\backup.ps1 -Keep 5         # keep only the 5 most recent backups, delete the rest

[CmdletBinding()]
param(
    [string]$Destination,
    [int]$Keep = 0
)

$ErrorActionPreference = 'Stop'

# The app folder where AkashicRecords.App.exe lives (the build output directory).
$scriptDir = $PSScriptRoot
if ($scriptDir -like '*\src\AkashicRecords.App') {
    $appDir = Join-Path $scriptDir 'bin\Debug\net8.0-windows'
} elseif ($scriptDir -like '*\AkashicRecords.App') {
    $appDir = $scriptDir
} else {
    $appDir = $scriptDir
}

# Where to store backups. Default: a "backups" folder next to the app.
if (-not $Destination) {
    $Destination = Join-Path $appDir 'backups'
}
New-Item -ItemType Directory -Force -Path $Destination | Out-Null

# Timestamped backup folder name.
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$backupPath = Join-Path $Destination $stamp

# What to back up: the user-data folders/files.
$items = 'data', 'music', 'media', 'config'

$copied = 0
foreach ($item in $items) {
    $src = Join-Path $appDir $item
    if (-not (Test-Path $src)) {
        Write-Host "  skip  $item (not present)"
        continue
    }
    $dest = Join-Path $backupPath $item
    Copy-Item -Path $src -Destination $dest -Recurse -Force
    $copied++
    Write-Host "  copy  $item"
}

# Report size.
$totalBytes = 0
foreach ($f in (Get-ChildItem -Path $backupPath -Recurse -File -ErrorAction SilentlyContinue)) {
    $totalBytes += $f.Length
}
$totalMB = [math]::Round($totalBytes / 1MB, 1)

Write-Host ''
Write-Host "Backup complete: $backupPath"
Write-Host "Size: $totalMB MB"

# Optional retention: keep only the N most recent backups.
if ($Keep -gt 0) {
    $old = Get-ChildItem -Path $Destination -Directory |
        Where-Object { $_.Name -like '20*' } |
        Sort-Object Name -Descending |
        Select-Object -Skip ($Keep - 1)
    foreach ($o in $old) {
        Remove-Item -Recurse -Force $o.FullName
        Write-Host "  prune $o.Name"
    }
}