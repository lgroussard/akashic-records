$ErrorActionPreference = 'Stop'
$transcript = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\285436bc-2f51-4439-b3db-d28d40b6bfde.jsonl"
$lines = Get-Content $transcript
$names = @{}
foreach($l in $lines){
    try{ $o = $l | ConvertFrom-Json }catch{ continue }
    if($o.type -ne 'assistant'){ continue }
    $msg = $o.data.message
    if(-not $msg -or -not $msg.content -or $msg.content -is [string]){ continue }
    foreach($c in $msg.content){
        if($c.type -eq 'tool_use'){
            $n = $c.name
            if(-not $names.ContainsKey($n)){ $names[$n] = 0 }
            $names[$n]++
        }
    }
}
$names.GetEnumerator() | Sort-Object Value -Descending | ForEach-Object { Write-Output "$($_.Name): $($_.Value)" }