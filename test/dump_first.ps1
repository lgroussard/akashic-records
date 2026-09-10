$ErrorActionPreference = 'Stop'
$transcript = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\285436bc-2f51-4439-b3db-d28d40b6bfde.jsonl"
$lines = Get-Content $transcript

$count = 0
foreach($l in $lines){
    try{ $o = $l | ConvertFrom-Json }catch{ continue }
    if($o.type -ne 'assistant'){ continue }
    $count++
    # Dump the raw JSON of the first assistant message's data
    if($count -eq 1){
        $o.data | ConvertTo-Json -Depth 20 | Out-String | Write-Output
        break
    }
}