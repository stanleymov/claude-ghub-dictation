# Removes the Claude G HUB bridge for the current Windows user.
$dest = Join-Path $env:LOCALAPPDATA "ClaudeGHubBridge"
. (Join-Path $PSScriptRoot "autostart.ps1")
Unregister-BridgeTask
Get-Process ClaudeGHubBridge -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Uninstalled (bridge, its Task Scheduler task and its log). Any G HUB button you set to Claude > Toggle dictation does nothing now; reassign it in G HUB." -ForegroundColor Green
