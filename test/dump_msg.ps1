$ErrorActionPreference = 'Stop'
$transcript = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\285436bc-2f51-4439-b3db-d28d40b6bfde.jsonl"
$lines = Get-Content $transcript

foreach($l in $lines){
    try{ $o=$l|ConvertFrom-Json }catch{continue}
    if($o.type -ne 'assistant'){continue}
    Write-Output "=== assistant msg ==="
    Write-Output "data keys: $($o.data.PSObject.Properties.Name -join ', ')"
    if($o.data.message){
        Write-Output "message keys: $($o.data.message.PSObject.Properties.Name -join ', ')"
        $c = $o.data.message.content
        Write-Output "content type: $($c.GetType().Name)"
        if($c -is [array]){
            Write-Output "content count: $($c.Count)"
            for($i=0;$i -lt [Math]::Min(5,$c.Count);$i++){
                Write-Output "  [$i] type=$($c[$i].GetType().Name) name=$($c[$i].name)"
            }
        }
    }
    break
}