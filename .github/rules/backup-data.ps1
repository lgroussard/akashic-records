# Backup the app's runtime data before any build/clean.
# Usage: powershell -ExecutionPolicy Bypass -File .github/rules/backup-data.ps1
#
# Copies data\, music\, config\ from the active bin output into .github/data-backup/
# so a clean build can never destroy user data.

$ErrorActionPreference = 'Stop'

$root = Split-Path $MyInvocation.MyCommand.Path -Parent
$root = Split-Path $root -Parent
$root = Split-Path $root -Parent   # repo root

$bin = Join-Path $root "src\AkashicRecords.App\bin\Debug\net8.0-windows"
$backupRoot = Join-Path $root ".github\data-backup"

if (-not (Test-Path $bin)) {
    Write-Output "No bin output found at $bin"
    Write-Output "Nothing to back up."
    exit 0
}

$backupDir = Join-Path $backupRoot (Get-Date -Format "yyyy-MM-dd_HH-mm-ss")
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null

$folders = @('data', 'music', 'config')
foreach ($f in $folders) {
    $src = Join-Path $bin $f
    if (Test-Path $src) {
        $dest = Join-Path $backupDir $f
        Copy-Item -Path $src -Destination $dest -Recurse -Force
        Write-Output "Backed up $f -> $dest"
    }
}

Write-Output "Backup complete: $backupDir"