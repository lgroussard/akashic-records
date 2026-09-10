$ErrorActionPreference = 'Stop'
$transcript = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\285436bc-2f51-4439-b3db-d28d40b6bfde.jsonl"
$lines = Get-Content $transcript

$seen = @{}
foreach($l in $lines){
    try{ $o = $l | ConvertFrom-Json }catch{ continue }
    if($o.type -ne 'assistant'){ continue }
    $msg = $o.data.message
    if(-not $msg -or -not $msg.content){ continue }
    foreach($c in $msg.content){
        if($c.type -eq 'tool_use'){
            $name = $c.name
            $fp = $c.input.filePath
            if(-not $fp){ $fp = $c.input.path }
            if($fp){
                if(-not $seen.ContainsKey($fp)){ $seen[$fp] = @() }
                $seen[$fp] += $name
            }
        }
    }
}

# Show unique file paths and which tools touched them
$seen.Keys | Sort-Object | ForEach-Object {
    Write-Output "$_  =>  $($seen[$_] -join ', ')"
}