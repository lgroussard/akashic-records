$ErrorActionPreference = 'Stop'
$dir = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\"
$targets = @(
    'x:Class="AkashicRecords.App.Views.CollectionsView',
    'x:Class="AkashicRecords.App.Views.JournauxView',
    'x:Class="AkashicRecords.App.Views.OrganisationView',
    'x:Class="AkashicRecords.App.Views.ArchivesView',
    'x:Class="AkashicRecords.App.MainWindow',
    'SubTabStyle',
    'TagChipStyle',
    'JeuxVideoTab',
    'ProjectEmptyState',
    'TransitionEmptyState',
    'AddOverlay',
    'FicheTierPillsPanel',
    'ShowTabForScreenshot',
    'OpenSectionForScreenshot'
)
foreach($f in (Get-ChildItem $dir -Filter "*.jsonl")){
    $lines = Get-Content $f.FullName
    $count = 0
    $firstMarker = $null
    foreach($l in $lines){
        foreach($t in $targets){
            if($l -match [regex]::Escape($t)){ $count++; if(-not $firstMarker){ $firstMarker = $t }; break }
        }
    }
    Write-Output "$($f.Name) : $count refonte matches"
}