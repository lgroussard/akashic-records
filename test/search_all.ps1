$ErrorActionPreference = 'Stop'
$dir = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\"
$files = Get-ChildItem $dir -Filter "*.jsonl"
$targets = @('x:Class="AkashicRecords.App.Views.CollectionsView','x:Class="AkashicRecords.App.Views.JournauxView','x:Class="AkashicRecords.App.Views.OrganisationView','SubTabStyle','TagChipStyle','JeuxVideoTab','ProjectEmptyState','TransitionEmptyState','AddOverlay','FicheTierPillsPanel')

foreach($f in $files){
    $lines = Get-Content $f.FullName
    $matchCount = 0
    foreach($l in $lines){
        foreach($t in $targets){
            if($l -match [regex]::Escape($t)){ $matchCount++; break }
        }
    }
    Write-Output "$($f.Name) : $matchCount refonte matches"
}