$ErrorActionPreference = 'Stop'
$t = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\285436bc-2f51-4439-b3db-d28d40b6bfde.jsonl"
$lines = Get-Content $t
$targets = @('x:Class="AkashicRecords.App.Views.CollectionsView','x:Class="AkashicRecords.App.Views.JournauxView','x:Class="AkashicRecords.App.Views.OrganisationView','SubTabStyle','TagChipStyle','JeuxVideoTab','ProjectEmptyState','AddOverlay','FicheTierPillsPanel','ShowTabForScreenshot')

foreach($l in $lines){
    foreach($t in $targets){
        if($l -match [regex]::Escape($t)){
            # Extract timestamp
            if($l -match '"timestamp":"([^"]+)"'){
                Write-Output "$($matches[1]) : $t"
            }
            break
        }
    }
}