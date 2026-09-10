$ErrorActionPreference = 'Stop'
$transcript = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\285436bc-2f51-4439-b3db-d28d40b6bfde.jsonl"
$lines = Get-Content $transcript

# Find first assistant message and dump its structure
foreach($l in $lines){
    try{ $o = $l | ConvertFrom-Json }catch{ continue }
    if($o.type -ne 'assistant'){ continue }
    Write-Output "type=$($o.type)"
    Write-Output "keys=$($o.PSObject.Properties.Name -join ', ')"
    if($o.data){
        Write-Output "data keys=$($o.data.PSObject.Properties.Name -join ', ')"
        if($o.data.message){
            Write-Output "message keys=$($o.data.message.PSObject.Properties.Name -join ', ')"
            if($o.data.message.content){
                Write-Output "content type=$($o.data.message.content.GetType().Name)"
                if($o.data.message.content -is [array]){
                    Write-Output "content count=$($o.data.message.content.Count)"
                    foreach($c in $o.data.message.content){
                        Write-Output "  - $($c.GetType().Name): name=$($c.name): $($c.PSObject.Properties.Name -join ', ')"
                    }
                }
            }
        }
    }
    break
}