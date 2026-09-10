$ErrorActionPreference = 'Stop'
$t = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\285436bc-2f51-4439-b3db-d28d40b6bfde.jsonl"
$lines = Get-Content $t
$targets = @('CollectionsView','JournauxView','OrganisationView')
$found = @{}
foreach($key in $targets){ $found[$key] = @() }

foreach($l in $lines){
    foreach($key in $targets){
        if($l -match [regex]::Escape("x:Class=""AkashicRecords.App.Views.$key")){
            $found[$key] += $l
        }
    }
}

foreach($key in $targets){
    Write-Output "$key : $($found[$key].Count) matches"
}