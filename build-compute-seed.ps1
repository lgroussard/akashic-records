# Assembles the "compute seed" for the hub: the upscale model, in the layout the hub expects under its data
# folder (compute/models/<name>.onnx). One model per feature: NomosWebPhoto (4xNomosWebPhoto_RealPLKSR, by
# Philip Hofmann, CC-BY-4.0, https://github.com/Phhofm/models).
#
# Output: D:\workspace\akashic-hub\compute-seed (next to the hub builds, outside git).
# Then on the mini-PC:  sudo bash ~/compute-seed/setup-compute.sh ~/compute-seed
#
# Usage, from the repo root:
#   .\build-compute-seed.ps1

param(
    [string]$OutDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'akashic-hub\compute-seed'),
    [string]$ModelDir = (Join-Path (Split-Path $PSScriptRoot -Parent) 'tools\onnx-models')
)

$ErrorActionPreference = 'Stop'
$release = 'https://github.com/Phhofm/models/releases/download/4xNomosWebPhoto_RealPLKSR/4xNomosWebPhoto_RealPLKSR_fp32_opset17.onnx'
$source = Join-Path $ModelDir '4xNomosWebPhoto_RealPLKSR.onnx'

if (-not (Test-Path $source)) {
    Write-Host "== Modele : telechargement depuis GitHub ==" -ForegroundColor Cyan
    New-Item -ItemType Directory -Force -Path $ModelDir | Out-Null
    Invoke-WebRequest $release -OutFile $source
}

if (Test-Path $OutDir) { Remove-Item $OutDir -Recurse -Force }
$models = Join-Path $OutDir 'models'
New-Item -ItemType Directory -Force -Path $models | Out-Null
Copy-Item $source (Join-Path $models 'nomos-webphoto-4x.onnx')

# setup-compute.sh travels with the seed, with Unix line endings.
$script = Join-Path $PSScriptRoot 'src\AkashicRecords.Hub\deploy\linux\setup-compute.sh'
$text = [IO.File]::ReadAllText($script) -replace "`r`n", "`n"
[IO.File]::WriteAllText((Join-Path $OutDir 'setup-compute.sh'), $text, (New-Object Text.UTF8Encoding $false))

$size = [math]::Round(((Get-ChildItem $OutDir -Recurse -File | Measure-Object Length -Sum).Sum / 1MB), 0)
Write-Host "Graine prete : $OutDir ($size Mo)" -ForegroundColor Green
Write-Host "Sur le mini-PC :  scp -r compute-seed lgroussard@<mini-PC>:~/   puis   sudo bash ~/compute-seed/setup-compute.sh ~/compute-seed"
