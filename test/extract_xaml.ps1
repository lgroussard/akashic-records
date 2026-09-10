$ErrorActionPreference = 'Stop'
$transcript = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\285436bc-2f51-4439-b3db-d28d40b6bfde.jsonl"
$lines = Get-Content $transcript

# Extract all assistant text content
$texts = @()
foreach($l in $lines){
    try{ $o=$l|ConvertFrom-Json }catch{continue}
    if($o.type -ne 'assistant'){continue}
    $msg = $o.data.message
    if(-not $msg){continue}
    if($msg.content -is [array]){
        foreach($c in $msg.content){
            if($c.type -eq 'text'){ $texts += $c.text }
        }
    }
    elseif($msg.content -is [string]){ $texts += $msg.content }
}

Write-Output "Total text chunks: $($texts.Count)"

# Find XAML blocks for each target file
foreach($key in @('CollectionsView','JournauxView','OrganisationView')){
    foreach($t in $texts){
        if($t -match "x:Class=""AkashicRecords\.App\.Views\.$key\b"){
            Write-Output "FOUND $key, length=$($t.Length)"
            # Save it
            $outPath = "d:\workspace\akashic-records\src\AkashicRecords.App\Views\$key.xaml"
            Set-Content -Path $outPath -Value $t -Encoding UTF8
            Write-Output "  Wrote: $outPath"
        }
    }
}