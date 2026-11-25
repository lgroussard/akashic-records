# Publishes the Akashic sync hub, ready to drop on the mini-PC.
#
# Output (by default D:\workspace\akashic-hub\, next to the repo, outside git):
#   win-x64\      -> the hub for Windows + install-service.ps1 / uninstall-service.ps1
#   linux-x64\    -> the hub for Linux   + install.sh / uninstall.sh / akashic-hub.service
#   akashic-hub-win-x64.zip, akashic-hub-linux-x64.zip  -> the same folders, to carry on a USB key
#
# Both builds are self-contained: nothing to install on the mini-PC (no .NET needed).
# On the mini-PC: copy the folder for its system, then run the install script inside it
# (see src\AkashicRecords.Hub\README.md).
#
# Usage, from the repo root:
#   .\build-hub.ps1                     # tests, then both builds
#   .\build-hub.ps1 -SkipTests          # builds only
#   .\build-hub.ps1 -Runtimes win-x64   # one system only
#   .\build-hub.ps1 -OutDir D:\autre\dossier
#
# Only builds the hub projects: it never touches the desktop app, its bin folder or its data,
# so it can run while Akashic is open.

param(
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'akashic-hub'),
    [string[]]$Runtimes = @('win-x64', 'linux-x64'),
    [switch]$SkipTests
)

$ErrorActionPreference = 'Stop'
$dotnet = 'C:\Program Files\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }

$hub = Join-Path $PSScriptRoot 'src\AkashicRecords.Hub'
$project = Join-Path $hub 'AkashicRecords.Hub.csproj'
$tests = Join-Path $PSScriptRoot 'src\AkashicRecords.Hub.SmokeTest\AkashicRecords.Hub.SmokeTest.csproj'

if (-not $SkipTests) {
    Write-Host "== Tests du hub ==" -ForegroundColor Cyan
    & $dotnet run --project $tests -c Release -nologo | Select-Object -Last 3
    if ($LASTEXITCODE -ne 0) { throw "Les tests du hub echouent : rien n'est publie. Relance '$dotnet run --project $tests' pour le detail." }
}

New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

foreach ($rid in $Runtimes) {
    Write-Host "== Publication $rid ==" -ForegroundColor Cyan
    $target = Join-Path $OutDir $rid

    # Replace the previous build, and only that: the folder must look like one of ours.
    if (Test-Path $target) {
        $known = (Get-ChildItem $target -Filter 'AkashicRecords.Hub*' | Select-Object -First 1)
        if (-not $known -and (Get-ChildItem $target -Force | Select-Object -First 1)) {
            throw "$target existe et ne ressemble pas a une publication du hub : je n'y touche pas."
        }
        Remove-Item $target -Recurse -Force
    }

    & $dotnet publish $project -c Release -r $rid --self-contained true -o $target -nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish a echoue pour $rid" }

    # A build made on this PC must not carry this PC's data or settings.
    Remove-Item (Join-Path $target 'hub-data') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item (Join-Path $target 'hub.local.json') -Force -ErrorAction SilentlyContinue
    Get-ChildItem $target -Filter '*.pdb' | Remove-Item -Force
    Remove-Item (Join-Path $target 'web.config') -Force -ErrorAction SilentlyContinue # IIS only, unused

    if ($rid -like 'win-*') {
        Copy-Item (Join-Path $hub 'deploy\windows\*') $target -Force
    } else {
        # Shell scripts and the unit file must have Unix line endings, whatever git or an editor did.
        foreach ($f in Get-ChildItem (Join-Path $hub 'deploy\linux')) {
            $text = [IO.File]::ReadAllText($f.FullName) -replace "`r`n", "`n"
            [IO.File]::WriteAllText((Join-Path $target $f.Name), $text, (New-Object Text.UTF8Encoding $false))
        }
    }
    Copy-Item (Join-Path $hub 'README.md') (Join-Path $target 'LISEZMOI.md') -Force

    $zip = Join-Path $OutDir "akashic-hub-$rid.zip"
    Remove-Item $zip -Force -ErrorAction SilentlyContinue
    Compress-Archive -Path (Join-Path $target '*') -DestinationPath $zip
    $size = [math]::Round(((Get-ChildItem $target -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 0)
    Write-Host "   $target  ($size Mo)  +  $(Split-Path $zip -Leaf)"
}

Write-Host ""
Write-Host "Termine. Sur le mini-PC :" -ForegroundColor Green
Write-Host "  Windows : copier win-x64, puis (PowerShell administrateur)  powershell -ExecutionPolicy Bypass -File .\install-service.ps1"
Write-Host "  Linux   : copier linux-x64, puis  sudo bash install.sh"
