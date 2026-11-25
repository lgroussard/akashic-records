# Backup the app's runtime data before any build/clean.
# Usage: powershell -ExecutionPolicy Bypass -File .github/rules/backup-data.ps1
#
# Copies data\, media\, config\ from the active bin output into a dated folder under
# .github/data-backup/, so a clean build can never destroy user data. media\ holds the imported
# images (journal photos, covers, archive): the database only keeps their paths.
#
# music\ (16 GB) is not copied again each time: it is kept as ONE copy, .github/data-backup/music,
# brought up to date on each run (new and changed files only). A file deleted from the app's music
# folder stays in that copy.

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

$folders = @('data', 'media', 'config')
foreach ($f in $folders) {
    $src = Join-Path $bin $f
    if (Test-Path $src) {
        $dest = Join-Path $backupDir $f
        Copy-Item -Path $src -Destination $dest -Recurse -Force
        Write-Output "Backed up $f -> $dest"
    }
}

$music = Join-Path $bin 'music'
if (Test-Path $music) {
    $musicDest = Join-Path $backupRoot 'music'
    # /E: with subfolders; no /PURGE: nothing is ever removed from the copy. Exit codes below 8 are successes.
    robocopy $music $musicDest /E /R:1 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "La copie de la musique a echoue (robocopy $LASTEXITCODE)." }
    $copied = if ($LASTEXITCODE -band 1) { 'new files copied' } else { 'already up to date' }
    Write-Output "Backed up music -> $musicDest ($copied)"
}

Write-Output "Backup complete: $backupDir"
exit 0
