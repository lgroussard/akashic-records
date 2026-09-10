$ErrorActionPreference = 'Stop'
$transcript = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\285436bc-2f51-4439-b3db-d28d40b6bfde.jsonl"
$lines = Get-Content $transcript
foreach($l in $lines){
    try{ $o=$l|ConvertFrom-Json }catch{continue}
    if($o.type -ne 'assistant'){continue}
    if($o.data.message.content -is [array]){
        foreach($c in $o.data.message.content){
            if($c.type -eq 'tool_use'){
                $fp = ''
                if($c.input.filePath){ $fp = $c.input.filePath }
                elseif($c.input.path){ $fp = $c.input.path }
                if($fp -match 'CollectionsView|JournauxView|OrganisationView'){
                    Write-Output "$($c.name) | $fp"
                }
            }
        }
    }
}