$ErrorActionPreference = 'Stop'
$transcript = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\285436bc-2f51-4439-b3db-d28d40b6bfde.jsonl"
$lines = Get-Content $transcript

# For each assistant message, extract text content and look for XAML blocks
# We'll find blocks that start with "<UserControl x:Class=...CollectionsView" etc.
$targets = @{
    'CollectionsView' = 'd:\workspace\akashic-records\src\AkashicRecords.App\Views\CollectionsView.xaml'
    'JournauxView'    = 'd:\workspace\akashic-records\src\AkashicRecords.App\Views\JournauxView.xaml'
    'OrganisationView' = 'd:\workspace\akashic-records\src\AkashicRecords.App\Views\OrganisationView.xaml'
}

foreach($l in $lines){
    try{ $o = $l | ConvertFrom-Json }catch{ continue }
    if($o.type -ne 'assistant'){ continue }
    $msg = $o.data.message
    if(-not $msg){ continue }

    # Gather all text from this message (content array or string)
    $texts = @()
    if($msg.content -is [array]){
        foreach($c in $msg.content){
            if($c.type -eq 'text'){ $texts += $c.text }
            elseif($c.type -eq 'tool_use' -and $c.name -eq 'read_file'){ $texts += $c.input.filePath }
        }
    } elseif($msg.content -is [string]){
        $texts += $msg.content
    }

    foreach($t in $texts){
        if(-not $t -is [string]){ continue }
        foreach($key in $targets.Keys){
            if($t -match "x:Class=""AkashicRecords\.App\.Views\.$key"){
                Write-Output "FOUND $key in assistant text, length=$($t.Length)"
            }
        }
    }
}