# Installs (or upgrades) the Akashic hub as a Windows service on this machine.
#
# Run from the published folder, in a PowerShell opened "as administrator":
#   powershell -ExecutionPolicy Bypass -File .\install-service.ps1
#
# What it does:
#   - copies this folder to C:\Program Files\AkashicHub (an upgrade keeps hub.local.json)
#   - keeps the data apart, in C:\ProgramData\AkashicHub (never touched by an upgrade or an uninstall)
#   - registers the service "AkashicHub": starts with Windows, restarts on failure,
#     runs as the low-privilege account "Local Service"
#   - opens the port in the Windows firewall for PRIVATE networks only (skip with -NoFirewall)
#   - starts the service, checks /health and prints a first pairing code
#
# Options:  -Port 5199   -InstallDir <folder>   -DataDir <folder>   -NoFirewall

#Requires -RunAsAdministrator
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'AkashicHub'),
    [string]$DataDir = (Join-Path $env:ProgramData 'AkashicHub'),
    [int]$Port = 5199,
    [switch]$NoFirewall
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'AkashicHub'
$ExeName = 'AkashicRecords.Hub.exe'
$Source = $PSScriptRoot

if (-not (Test-Path (Join-Path $Source $ExeName))) {
    throw "$ExeName introuvable a cote de ce script. Lance-le depuis le dossier publie (win-x64)."
}

# --- stop a previous version -------------------------------------------------------------
$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($existing -and $existing.Status -ne 'Stopped') {
    Write-Host "Arret du service existant..."
    Stop-Service -Name $ServiceName -Force
    $existing.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
}

# --- program files -----------------------------------------------------------------------
Write-Host "Copie du hub vers $InstallDir"
New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
if ((Resolve-Path $Source).Path.TrimEnd('\') -ne (Resolve-Path $InstallDir).Path.TrimEnd('\')) {
    # hub.local.json is this machine's settings: never overwritten.
    Get-ChildItem -Path $Source -Force | Where-Object Name -ne 'hub.local.json' |
        Copy-Item -Destination $InstallDir -Recurse -Force
}

# --- settings of this machine ------------------------------------------------------------
$local = Join-Path $InstallDir 'hub.local.json'
if (-not (Test-Path $local)) {
    $settings = @{ Hub = @{ DataDir = $DataDir; Port = $Port } } | ConvertTo-Json
    [IO.File]::WriteAllText($local, $settings, (New-Object Text.UTF8Encoding $false))
    Write-Host "Reglages ecrits dans $local"
} else {
    Write-Host "Reglages existants conserves : $local"
    $kept = (Get-Content $local -Raw | ConvertFrom-Json).Hub
    if ($kept.DataDir) { $DataDir = $kept.DataDir }
    if ($kept.Port) { $Port = [int]$kept.Port }
}

# --- data folder, writable by the service account ----------------------------------------
New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
# *S-1-5-19 = "Local Service", by SID so it works on a French Windows too.
& icacls.exe $DataDir /grant '*S-1-5-19:(OI)(CI)M' /T /Q | Out-Null
if ($LASTEXITCODE -ne 0) { throw "icacls a echoue sur $DataDir" }

# --- service -----------------------------------------------------------------------------
$exe = Join-Path $InstallDir $ExeName
if (-not $existing) {
    New-Service -Name $ServiceName -BinaryPathName "`"$exe`"" -DisplayName 'Akashic Hub' `
        -Description 'Hub de synchronisation Akashic Records (PC et telephone).' -StartupType Automatic | Out-Null
    Write-Host "Service $ServiceName cree."
}

# Least privilege: run as Local Service instead of SYSTEM. Not fatal if Windows refuses.
try {
    $svc = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
    $r = Invoke-CimMethod -InputObject $svc -MethodName Change -Arguments @{ StartName = 'NT AUTHORITY\LocalService'; StartPassword = '' }
    if ($r.ReturnValue -ne 0) { throw "code $($r.ReturnValue)" }
} catch {
    Write-Warning "Impossible de passer le service sous 'Local Service' ($_). Il tournera sous le compte Systeme : cela marche, avec plus de droits que necessaire."
}

# Restart on failure: after 5 s, 5 s, then 60 s.
& sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/60000 | Out-Null

# --- firewall ----------------------------------------------------------------------------
if (-not $NoFirewall) {
    Get-NetFirewallRule -DisplayName 'Akashic Hub' -ErrorAction SilentlyContinue | Remove-NetFirewallRule
    New-NetFirewallRule -DisplayName 'Akashic Hub' -Direction Inbound -Action Allow -Protocol TCP `
        -LocalPort $Port -Profile Private | Out-Null
    Write-Host "Pare-feu : port $Port ouvert sur les reseaux prives uniquement."
    $profiles = Get-NetConnectionProfile | Where-Object NetworkCategory -ne 'Private'
    foreach ($p in $profiles) {
        Write-Warning "Le reseau '$($p.Name)' est classe '$($p.NetworkCategory)' : le telephone n'y verra pas le hub. Passe-le en 'Prive' dans Parametres > Reseau si c'est ton reseau domestique."
    }
}

# --- start and check ---------------------------------------------------------------------
Start-Service -Name $ServiceName
$health = $null
foreach ($i in 1..20) {
    try { $health = Invoke-RestMethod "http://127.0.0.1:$Port/health" -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 }
}
if (-not $health) {
    throw "Le service est lance mais /health ne repond pas sur le port $Port. Regarde $DataDir\logs et l'Observateur d'evenements (source AkashicHub)."
}

Write-Host ""
Write-Host "Hub $($health.version) en service." -ForegroundColor Green
Write-Host "Donnees : $DataDir"
$ips = Get-NetIPAddress -AddressFamily IPv4 | Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' }
foreach ($ip in $ips) { Write-Host "Adresse a saisir sur les appareils : http://$($ip.IPAddress):$Port" }
Write-Host ""
Write-Host "Premier code d'appairage :"
& $exe pair
Write-Host ""
Write-Host "Un code par appareil. Pour en obtenir un autre plus tard (PowerShell administrateur) :"
Write-Host "  & `"$exe`" pair"
