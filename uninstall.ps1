# Removes the Claude G HUB bridge for the current Windows user.
$dest = Join-Path $env:LOCALAPPDATA "ClaudeGHubBridge"
Get-Process ClaudeGHubBridge -ErrorAction SilentlyContinue | Stop-Process -Force
Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" ClaudeGHubBridge -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 500
Remove-Item $dest -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Uninstalled. Any G HUB button you set to Claude > Toggle dictation does nothing now; reassign it in G HUB." -ForegroundColor Green
