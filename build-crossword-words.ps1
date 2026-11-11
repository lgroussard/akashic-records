# Rebuilds the crossword word list of the phone app from the latest French Wiktionary.
#
# Downloads kaikki.org's extraction of the Wiktionnaire (one file, about 3 GB) into a temporary folder, keeps the
# common words of 3 to 9 letters with a short definition (tools/CrosswordWords), writes them to
# android/app/src/main/assets/crossword/fr-wiktionnaire.tsv, then deletes the downloaded file.
# The hand-written clues (fr.tsv) stay and take precedence in the app. Definitions: Wiktionnaire, CC BY-SA.
#
# Usage: powershell -ExecutionPolicy Bypass -File build-crossword-words.ps1 [-Source <existing .jsonl>] [-MinTranslations 3]
# Afterwards: build-mobile.ps1 to put the new list in the APK.
param(
    [string]$Source,            # an already downloaded .jsonl (then nothing is downloaded or deleted)
    [int]$MinTranslations = 3   # higher = fewer, more common words
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$url = 'https://kaikki.org/frwiktionary/Fran%C3%A7ais/kaikki.org-dictionary-Fran%C3%A7ais.jsonl'
$output = Join-Path $root 'android\app\src\main\assets\crossword\fr-wiktionnaire.tsv'
$tool = Join-Path $root 'tools\CrosswordWords'

$downloaded = $false
if (-not $Source) {
    $work = Join-Path $env:TEMP 'akashic-wiktionnaire'
    New-Item -ItemType Directory -Force $work | Out-Null
    $Source = Join-Path $work 'fr.jsonl'
    Write-Host "== Téléchargement du Wiktionnaire (~3 Go) ==" -ForegroundColor Cyan
    & curl.exe -L -S -s --fail -A 'AkashicRecords/1.0 (personal app)' -o $Source $url
    if ($LASTEXITCODE -ne 0) { throw "Téléchargement impossible ($url)." }
    $downloaded = $true
}

Write-Host "== Extraction des mots ==" -ForegroundColor Cyan
& dotnet run --project $tool -c Release -- $Source $output $MinTranslations
if ($LASTEXITCODE -ne 0) { throw "L'extraction a échoué." }

if ($downloaded) {
    # Only the file this script downloaded, in its own temporary folder.
    Remove-Item -LiteralPath $Source -Force
    Write-Host "Fichier téléchargé supprimé."
}
$kb = [math]::Round((Get-Item $output).Length / 1KB)
Write-Host "Liste écrite : $output ($kb Ko). Lance build-mobile.ps1 pour la mettre dans l'APK." -ForegroundColor Green
