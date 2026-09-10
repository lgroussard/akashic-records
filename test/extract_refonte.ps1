$ErrorActionPreference = 'Stop'
$transcript = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\285436bc-2f51-4439-b3db-d28d40b6bfde.jsonl"
$lines = Get-Content $transcript

# Collect all create_file tool_use calls with their file content
$files = @{}
foreach($l in $lines){
    try{ $o = $l | ConvertFrom-Json }catch{ continue }
    if($o.type -ne 'assistant'){ continue }
    $msg = $o.data.message
    if(-not $msg -or -not $msg.content){ continue }
    foreach($c in $msg.content){
        if($c.type -eq 'tool_use' -and $c.name -eq 'create_file'){
            $fp = $c.input.filePath
            $ct = $c.input.content
            $files[$fp] = $ct
        }
    }
}

# Write out the target files
$targets = @(
    "d:\workspace\akashic-records\src\AkashicRecords.App\Views\CollectionsView.xaml",
    "d:\workspace\akashic-records\src\AkashicRecords.App\Views\JournauxView.xaml",
    "d:\workspace\akashic-records\src\AkashicRecords.App\Views\OrganisationView.xaml"
)
foreach($t in $targets){
    if($files.ContainsKey($t)){
        Set-Content -Path $t -Value $files[$t] -Encoding UTF8
        Write-Output "WROTE: $t ($(($files[$t]).Length) chars)"
    } else {
        Write-Output "NOT FOUND in transcript: $t"
    }
}