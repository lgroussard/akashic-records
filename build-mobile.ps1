# Builds the Android APK and packs the current desktop data for the phone.
#
# Local output (by default D:\workspace\akashic-mobile\):
#   akashic-records.apk          -> to install on the phone
#   akashic-data-<date>.zip      -> data\akashic.db + media\ (dated copies stay on the PC)
#
# Google Drive (automatic once "Google Drive for desktop" is installed):
#   <Mon Drive>\Akashic\akashic-records.apk
#   <Mon Drive>\Akashic\akashic-data-latest.zip   -> replaced at each run, no pile-up
#   <Mon Drive>\Akashic\cle-signature\debug.keystore -> backup of the APK signing key (copied once)
#   On the phone: Drive app -> the APK to install; in Akashic, gear -> Choisir un fichier -> Drive -> the zip.
#
# Usage, from the repo root (or double-click build-mobile.cmd):
#   .\build-mobile.ps1                  # APK + data dump (+ Drive if found)
#   .\build-mobile.ps1 -SkipData        # APK only
#   .\build-mobile.ps1 -SkipApk         # data dump only
#   .\build-mobile.ps1 -NoDrive         # do not copy to Google Drive
#   .\build-mobile.ps1 -DriveDir "G:\Mon Drive\Autre"   # Drive folder chosen by hand
#   .\build-mobile.ps1 -OutDir D:\autre\dossier
#
# Reads the desktop data, never modifies or deletes it. If the desktop app is running the
# script stops, because its database may not be written to disk yet.

param(
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'akashic-mobile'),
    [string]$DriveDir,
    [switch]$NoDrive,
    [switch]$SkipApk,
    [switch]$SkipData
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$android = Join-Path $repo 'android'
$desktopOut = Join-Path $repo 'src\AkashicRecords.App\bin\Debug\net8.0-windows'

New-Item -ItemType Directory -Force $OutDir | Out-Null

# ---- Google Drive folder ----------------------------------------------------
# Google Drive for desktop: "stream" mode mounts a drive letter (G: by default) holding
# "Mon Drive" / "My Drive"; "mirror" mode puts that folder in the user profile.
function Find-DriveFolder {
    $roots = @((Get-PSDrive -PSProvider FileSystem | ForEach-Object { $_.Root }) + $env:USERPROFILE)
    foreach ($root in $roots) {
        foreach ($name in 'Mon Drive', 'My Drive') {
            $p = Join-Path $root $name
            if (Test-Path $p) { return (Join-Path $p 'Akashic') }
        }
    }
    return $null
}

$drive = $null
if (-not $NoDrive) {
    $drive = if ($DriveDir) { $DriveDir } else { Find-DriveFolder }
    if ($drive) {
        New-Item -ItemType Directory -Force $drive | Out-Null
        Write-Host "Google Drive : $drive" -ForegroundColor Cyan
    }
    else {
        Write-Host "Google Drive introuvable (Google Drive for desktop pas installe ?) : sortie locale seulement." -ForegroundColor Yellow
    }
}

# ---- APK -------------------------------------------------------------------
if (-not $SkipApk) {
    Write-Host "== Build de l'APK ==" -ForegroundColor Cyan

    # Gradle fails with "Unable to establish loopback connection" when its temp dir is under the
    # user profile in some shells; a temp dir inside android\.gradle (gitignored) avoids it.
    $tmp = Join-Path $android '.gradle\tmp'
    New-Item -ItemType Directory -Force $tmp | Out-Null
    $env:TEMP = $tmp
    $env:TMP = $tmp
    $env:JAVA_TOOL_OPTIONS = "-Djdk.net.unixdomain.tmpdir=$tmp -Djava.io.tmpdir=$tmp"

    Push-Location $android
    try {
        & .\gradlew.bat assembleDebug --console=plain
        if ($LASTEXITCODE -ne 0) { throw "Echec du build Gradle (code $LASTEXITCODE)." }
    }
    finally {
        Pop-Location
    }

    $apk = Join-Path $android 'app\build\outputs\apk\debug\app-debug.apk'
    Copy-Item $apk (Join-Path $OutDir 'akashic-records.apk') -Force
    Write-Host "APK : $(Join-Path $OutDir 'akashic-records.apk')" -ForegroundColor Green

    if ($drive) {
        Copy-Item $apk (Join-Path $drive 'akashic-records.apk') -Force
        Write-Host "APK -> Drive" -ForegroundColor Green

        # The phone only accepts an update signed with the same key. Keep a copy of it, once:
        # an existing backup is never overwritten.
        $key = Join-Path $env:USERPROFILE '.android\debug.keystore'
        $keyDir = Join-Path $drive 'cle-signature'
        $keyBackup = Join-Path $keyDir 'debug.keystore'
        if ((Test-Path $key) -and -not (Test-Path $keyBackup)) {
            New-Item -ItemType Directory -Force $keyDir | Out-Null
            Copy-Item $key $keyBackup
            Write-Host "Cle de signature sauvegardee : $keyBackup" -ForegroundColor Green
        }
    }
}

# ---- Data dump -------------------------------------------------------------
if (-not $SkipData) {
    Write-Host "== Dump des donnees du PC ==" -ForegroundColor Cyan

    if (Get-Process -Name 'AkashicRecords.App' -ErrorAction SilentlyContinue) {
        throw "L'app Akashic tourne sur le PC : quitte-la (icone de la barre des taches -> Exit) puis relance le script."
    }

    $db = Join-Path $desktopOut 'data\akashic.db'
    if (-not (Test-Path $db)) { throw "Base introuvable : $db" }

    # Zipped straight from the desktop folders (read only, nothing copied or deleted there).
    # The app being closed, data\ holds a complete akashic.db.
    $paths = @(Join-Path $desktopOut 'data')
    $media = Join-Path $desktopOut 'media'
    if (Test-Path $media) { $paths += $media }

    # Timestamped name: never overwrites an earlier dump.
    $zip = Join-Path $OutDir ("akashic-data-" + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss') + ".zip")
    Compress-Archive -Path $paths -DestinationPath $zip -CompressionLevel Optimal

    $mb = [math]::Round((Get-Item $zip).Length / 1MB, 1)
    Write-Host "Donnees : $zip ($mb Mo)" -ForegroundColor Green

    if ($drive) {
        # Single replaced file on Drive; the dated history stays on the PC.
        Copy-Item $zip (Join-Path $drive 'akashic-data-latest.zip') -Force
        Write-Host "Donnees -> Drive (akashic-data-latest.zip)" -ForegroundColor Green
    }
}

Write-Host ""
Write-Host "Sur le telephone :" -ForegroundColor Cyan
if ($drive) {
    Write-Host "  1. App Google Drive -> dossier Akashic -> akashic-records.apk -> installer"
    Write-Host "  2. Dans Akashic : engrenage -> Choisir un fichier -> Drive -> Akashic -> akashic-data-latest.zip -> Remplacer"
}
else {
    Write-Host "  1. Copier le dossier $OutDir (cable USB) dans Download"
    Write-Host "  2. Ouvrir akashic-records.apk et autoriser l'installation"
    Write-Host "  3. Dans l'app : engrenage -> Choisir un fichier -> le .zip -> Remplacer"
}
