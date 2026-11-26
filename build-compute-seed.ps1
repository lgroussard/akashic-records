# Assembles the "compute seed" for the hub: the upscale program for each platform and the models, in the layout
# the hub expects under its data folder (compute/tools/realesrgan/<rid>/..., compute/models/...).
#
# Output: D:\workspace\akashic-hub\compute-seed (next to the hub builds, outside git).
# Then on the mini-PC:  sudo bash setup-compute.sh ~/compute-seed   (script shipped with the hub, linux-x64).
#
# Sources:
#   - Windows program + models: D:\workspace\tools\realesrgan (models\*.param/.bin)
#   - Linux program: fetched once from Real-ESRGAN's GitHub release into D:\workspace\tools\realesrgan-linux
#
# Usage, from the repo root:
#   .\build-compute-seed.ps1                                   # the default models
#   .\build-compute-seed.ps1 -Models high-fidelity-4x,remacri-4x

param(
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'akashic-hub\compute-seed'),
    [string]$Source = (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\realesrgan'),
    [string]$LinuxSource = (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\realesrgan-linux'),
    [string[]]$Models = @('high-fidelity-4x', 'ultrasharp-4x', 'ultramix-balanced-4x', 'remacri-4x')
)

$ErrorActionPreference = 'Stop'
$release = 'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-ubuntu.zip'

if (-not (Test-Path (Join-Path $Source 'realesrgan-ncnn-vulkan.exe'))) { throw "Programme Windows introuvable dans $Source" }

$linuxExe = Join-Path $LinuxSource 'realesrgan-ncnn-vulkan'
if (-not (Test-Path $linuxExe)) {
    Write-Host "== Programme Linux : telechargement depuis GitHub ==" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $LinuxSource | Out-Null
    $zip = Join-Path $LinuxSource 're.zip'
    Invoke-WebRequest $release -OutFile $zip
    Expand-Archive $zip -DestinationPath (Join-Path $LinuxSource 'unzipped') -Force
    Copy-Item (Join-Path $LinuxSource 'unzipped\realesrgan-ncnn-vulkan') $linuxExe
    Remove-Item $zip, (Join-Path $LinuxSource 'unzipped') -Recurse -Force
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
$win = Join-Path $OutDir 'tools\realesrgan\win-x64'
$lin = Join-Path $OutDir 'tools\realesrgan\linux-x64'
$mod = Join-Path $OutDir 'models'
New-Item -ItemType Directory -Force -Path $win, $lin, $mod | Out-Null

Copy-Item (Join-Path $Source 'realesrgan-ncnn-vulkan.exe'), (Join-Path $Source 'vcomp140.dll') $win
Copy-Item $linuxExe $lin
foreach ($m in $Models) {
    foreach ($ext in 'param', 'bin') {
        $f = Join-Path $Source "models\$m.$ext"
        if (-not (Test-Path $f)) { throw "Modele introuvable : $f" }
        Copy-Item $f $mod
    }
}
# setup-compute.sh travels with the seed, with Unix line endings.
$script = Join-Path $PSScriptRoot 'src\AkashicRecords.Hub\deploy\linux\setup-compute.sh'
$text = [IO.File]::ReadAllText($script) -replace "`r`n", "`n"
[IO.File]::WriteAllText((Join-Path $OutDir 'setup-compute.sh'), $text, (New-Object Text.UTF8Encoding $false))

$size = [math]::Round(((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 0)
Write-Host "Graine prete : $OutDir ($size Mo, $($Models.Count) modeles)" -ForegroundColor Green
Write-Host "Sur le mini-PC :  scp -r compute-seed lgroussard@<mini-PC>:~/   puis   sudo bash ~/compute-seed/setup-compute.sh ~/compute-seed"
