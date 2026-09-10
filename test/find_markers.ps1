$ErrorActionPreference = 'Stop'
$transcript = "c:\Users\Lohan G\AppData\Roaming\Code\User\workspaceStorage\c9eb6473a52a4c9bbc4483e3c152d32c\GitHub.copilot-chat\transcripts\285436bc-2f51-4439-b3db-d28d40b6bfde.jsonl"
$lines = Get-Content $transcript

# Find lines that contain the refonte XAML content (search for distinctive refonte strings)
$markers = @('SubTabStyle', 'TagChipStyle', 'AlbumList', 'STierItems', 'JeuxVideoTab', 'ShowTabForScreenshot', 'ProjectEmptyState', 'TransitionEmptyState', 'AddOverlay', 'FicheTierPillsPanel')
foreach($marker in $markers){
    $matches = $lines | Where-Object { $_ -match [regex]::Escape($marker) }
    Write-Output "=== $marker : $($matches.Count) matches ==="
}