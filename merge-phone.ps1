# Merges what was written on the phone into the PC data, keeping what was written on the PC.
#
# On the phone first: gear -> "Exporter les donnees du telephone" -> Exporter... -> Drive, folder Akashic.
#
# Usage, from the repo root:
#   .\merge-phone.ps1                         # dry run: writes a merged copy and a report, the PC data is untouched
#   .\merge-phone.ps1 -Apply                  # same, then puts the merged database and the phone's new media in place
#   .\merge-phone.ps1 -PhoneWins Poem.25      # a row changed on both sides: take the phone's version (repeatable: Poem.25,Recipe.2)
#   .\merge-phone.ps1 -PhoneZip D:\x\akashic-telephone-....zip
#
# The export used is the latest akashic-telephone-*.zip of the Drive folder Akashic (or of -OutDir).
# The common starting point is found among the dated dumps build-mobile.ps1 keeps in -OutDir.
# Everything goes to <OutDir>\fusion\<date>\ : rapport.txt, akashic-fusion.db and, with -Apply,
# pc-avant-fusion.db (the PC database as it was: copy it back over data\akashic.db to undo).
#
# After -Apply: .\build-mobile.ps1 -SkipApk, then on the phone gear -> Choisir un fichier -> akashic-data-latest.zip.

param(
    [string]$PhoneZip,
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'akashic-mobile'),
    [string[]]$PhoneWins = @(),
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
$repo = $PSScriptRoot
$desktopOut = Join-Path $repo 'src\AkashicRecords.App\bin\Debug\net8.0-windows'
$pcDb = Join-Path $desktopOut 'data\akashic.db'
$pcMedia = Join-Path $desktopOut 'media'
Add-Type -AssemblyName System.IO.Compression.FileSystem

if (-not (Test-Path $pcDb)) { throw "Base du PC introuvable : $pcDb" }
if ($Apply -and (Get-Process -Name 'AkashicRecords.App' -ErrorAction SilentlyContinue)) {
    throw "L'app Akashic tourne sur le PC : quitte-la (icone de la barre des taches -> Exit) puis relance le script."
}

# ---- The phone export ---------------------------------------------------------
if (-not $PhoneZip) {
    $folders = @($OutDir)
    foreach ($root in @((Get-PSDrive -PSProvider FileSystem | ForEach-Object { $_.Root }) + $env:USERPROFILE)) {
        foreach ($name in 'Mon Drive', 'My Drive') {
            $p = Join-Path (Join-Path $root $name) 'Akashic'
            if (Test-Path $p) { $folders += $p }
        }
    }
    $latest = Get-ChildItem $folders -Filter 'akashic-telephone-*.zip' -ErrorAction SilentlyContinue | Sort-Object Name | Select-Object -Last 1
    if (-not $latest) { throw "Aucun akashic-telephone-*.zip trouve (Drive\Akashic ou $OutDir). Sur le telephone : engrenage -> Exporter..." }
    $PhoneZip = $latest.FullName
}
Write-Host "Export du telephone : $PhoneZip" -ForegroundColor Cyan

$work = Join-Path $OutDir ("fusion\" + (Get-Date -Format 'yyyy-MM-dd_HH-mm-ss'))
$phoneDir = Join-Path $work 'telephone'
New-Item -ItemType Directory -Force $phoneDir | Out-Null
[IO.Compression.ZipFile]::ExtractToDirectory($PhoneZip, $phoneDir)
$phoneDb = Join-Path $phoneDir 'data\akashic.db'
if (-not (Test-Path $phoneDb)) { throw "Pas de data\akashic.db dans $PhoneZip" }

# ---- Candidate starting points: the database of every dated dump ---------------
$baseDir = Join-Path $work 'bases'
New-Item -ItemType Directory -Force $baseDir | Out-Null
$bases = @()
foreach ($dump in Get-ChildItem $OutDir -Filter 'akashic-data-*.zip' | Sort-Object Name) {
    $zip = [IO.Compression.ZipFile]::OpenRead($dump.FullName)
    try {
        $entry = $zip.Entries | Where-Object { $_.FullName -match 'akashic\.db$' } | Select-Object -First 1
        if ($entry) {
            $target = Join-Path $baseDir ($dump.BaseName + '.db')
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
            $bases += $target
        }
    }
    finally { $zip.Dispose() }
}
if ($bases.Count -eq 0) { throw "Aucun dump akashic-data-*.zip dans $OutDir : impossible de savoir d'ou le telephone est parti." }

# ---- Merge, on a copy ---------------------------------------------------------
$merged = Join-Path $work 'akashic-fusion.db'
$report = Join-Path $work 'rapport.txt'
$toolArgs = @('--pc', $pcDb, '--phone', $phoneDb, '--out', $merged)
foreach ($b in $bases) { $toolArgs += @('--base', $b) }
foreach ($w in $PhoneWins) { $toolArgs += @('--phone-wins', $w) }

dotnet build (Join-Path $repo 'tools\MergePhone') -c Release --nologo -v quiet | Out-Null
if ($LASTEXITCODE -ne 0) { throw "Echec du build de tools\MergePhone." }
[Console]::OutputEncoding = [Text.Encoding]::UTF8
dotnet (Join-Path $repo 'tools\MergePhone\bin\Release\net8.0\MergePhone.dll') @toolArgs | Tee-Object -FilePath $report
$code = $LASTEXITCODE
if ($code -eq 1) { throw "La fusion a echoue : rien n'a ete modifie sur le PC." }

# ---- Media the phone has and the PC lacks ---------------------------------------
$phoneMedia = Join-Path $phoneDir 'media'
$newMedia = @()
if (Test-Path $phoneMedia) {
    $newMedia = @(Get-ChildItem $phoneMedia -Recurse -File | Where-Object {
        $_.Length -gt 0 -and -not (Test-Path (Join-Path $pcMedia $_.FullName.Substring($phoneMedia.Length + 1)))
    })
}
"`nFichiers media du telephone absents du PC : $($newMedia.Count)" | Tee-Object -FilePath $report -Append
$newMedia | ForEach-Object { "  " + $_.FullName.Substring($phoneMedia.Length + 1) } | Tee-Object -FilePath $report -Append

Write-Host ""
if (-not $Apply) {
    Write-Host "Essai a blanc : rien n'a ete modifie sur le PC. Rapport : $report" -ForegroundColor Yellow
    Write-Host "Pour appliquer : .\merge-phone.ps1 -Apply" -ForegroundColor Yellow
    return
}

# ---- Apply --------------------------------------------------------------------
Copy-Item $pcDb (Join-Path $work 'pc-avant-fusion.db')
Copy-Item $merged $pcDb -Force
foreach ($f in $newMedia) {
    $target = Join-Path $pcMedia $f.FullName.Substring($phoneMedia.Length + 1)
    New-Item -ItemType Directory -Force (Split-Path $target -Parent) | Out-Null
    Copy-Item $f.FullName $target
}
Write-Host "Fusion appliquee. Base d'avant : $(Join-Path $work 'pc-avant-fusion.db')" -ForegroundColor Green
if ($code -eq 2) { Write-Host "Des conflits restent sur la version du PC : voir $report" -ForegroundColor Yellow }
Write-Host "Ensuite : .\build-mobile.ps1 -SkipApk, puis sur le telephone engrenage -> Choisir un fichier -> akashic-data-latest.zip" -ForegroundColor Cyan
