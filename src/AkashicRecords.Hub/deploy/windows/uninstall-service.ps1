# Removes the Akashic hub service, its firewall rule and its program folder.
# The DATA folder (C:\ProgramData\AkashicHub by default) is kept: delete it by hand if you
# really want to lose the journal, the paired devices and the media.
#
#   powershell -ExecutionPolicy Bypass -File .\uninstall-service.ps1

#Requires -RunAsAdministrator
param(
    [string]$InstallDir = (Join-Path $env:ProgramFiles 'AkashicHub')
)

$ErrorActionPreference = 'Stop'
$ServiceName = 'AkashicHub'

$svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($svc) {
    if ($svc.Status -ne 'Stopped') {
        Stop-Service -Name $ServiceName -Force
        $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
    }
    & sc.exe delete $ServiceName | Out-Null
    Write-Host "Service $ServiceName supprime."
} else {
    Write-Host "Service $ServiceName absent."
}

Get-NetFirewallRule -DisplayName 'Akashic Hub' -ErrorAction SilentlyContinue | Remove-NetFirewallRule

$dataDir = $null
$local = Join-Path $InstallDir 'hub.local.json'
if (Test-Path $local) { $dataDir = (Get-Content $local -Raw | ConvertFrom-Json).Hub.DataDir }

# Only remove a folder that really is a hub install.
if (Test-Path (Join-Path $InstallDir 'AkashicRecords.Hub.exe')) {
    if ($PSScriptRoot.TrimEnd('\') -eq $InstallDir.TrimEnd('\')) { Set-Location $env:TEMP }
    Remove-Item -Path $InstallDir -Recurse -Force
    Write-Host "Dossier $InstallDir supprime."
}

if ($dataDir) { Write-Host "Donnees conservees dans : $dataDir" }
