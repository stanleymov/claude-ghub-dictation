# Installs the Claude G HUB bridge for the current Windows user.
#   - compiles the bridge with the C# compiler that ships with Windows (.NET Framework 4.x), nothing to download
#   - installs it to %LOCALAPPDATA%\ClaudeGHubBridge and starts it with Windows
#   - the bridge adds a "Claude" integration to Logitech G HUB with a "Toggle dictation" action
param([switch]$NoAutostart)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dest = Join-Path $env:LOCALAPPDATA "ClaudeGHubBridge"
$csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$wpf = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\WPF"

function Say($text, $color = "Gray") { Write-Host $text -ForegroundColor $color }

Say "Claude G HUB bridge - installer" "Cyan"

# Prerequisites ------------------------------------------------------------------------------------
if (-not (Test-Path $csc)) { throw "The .NET Framework 4 C# compiler was not found ($csc). It ships with Windows 10 and 11." }
if (-not (Get-Process lghub_agent -ErrorAction SilentlyContinue)) {
  Say "Logitech G HUB does not seem to be running. Install or start G HUB; the bridge connects to it whenever it runs." "Yellow"
}
$claudePkg = Get-AppxPackage -Name "Claude" -ErrorAction SilentlyContinue | Select-Object -Last 1
$claudeExe = Join-Path $env:LOCALAPPDATA "AnthropicClaude\claude.exe"
if (-not $claudePkg -and -not (Test-Path $claudeExe)) {
  Say "The Claude desktop app was not found. Install it from claude.ai/download; dictation is toggled in its Code tab." "Yellow"
}

# Build --------------------------------------------------------------------------------------------
$build = Join-Path $root "build"
New-Item -ItemType Directory -Force $build, $dest | Out-Null
Get-Process ClaudeGHubBridge -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500
Say "Building..."
& $csc /nologo /target:winexe /optimize+ /out:"$build\ClaudeGHubBridge.exe" /r:System.Core.dll /r:System.Web.Extensions.dll `
  /r:"$wpf\UIAutomationClient.dll" /r:"$wpf\UIAutomationTypes.dll" /r:"$wpf\WindowsBase.dll" "$root\src\ClaudeGHubBridge.cs" "$root\src\Diagnostics.cs"
if ($LASTEXITCODE) { throw "Build failed." }

# Icon for G HUB: Claude's own logo from the local Claude install (nothing is bundled) -------------
$iconSrc = $null
if ($claudePkg) { $iconSrc = Join-Path $claudePkg.InstallLocation "app\resources\ion-dist\images\claude_app_icon.png" }
if ($iconSrc -and (Test-Path $iconSrc)) {
  Add-Type -AssemblyName System.Drawing
  $img = [Drawing.Image]::FromFile($iconSrc); $bmp = New-Object Drawing.Bitmap 64, 64
  $g = [Drawing.Graphics]::FromImage($bmp); $g.InterpolationMode = "HighQualityBicubic"; $g.DrawImage($img, 0, 0, 64, 64)
  $bmp.Save("$build\claude.png", [Drawing.Imaging.ImageFormat]::Png); $g.Dispose(); $bmp.Dispose(); $img.Dispose()
}

# Install ------------------------------------------------------------------------------------------
Copy-Item "$build\ClaudeGHubBridge.exe" $dest -Force
if (Test-Path "$build\claude.png") { Copy-Item "$build\claude.png" $dest -Force }
$run = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
if ($NoAutostart) { Remove-ItemProperty $run ClaudeGHubBridge -ErrorAction SilentlyContinue }
else { Set-ItemProperty $run ClaudeGHubBridge "`"$dest\ClaudeGHubBridge.exe`"" }
Start-Process "$dest\ClaudeGHubBridge.exe"

# Confirm it reached G HUB -------------------------------------------------------------------------
$log = Join-Path $dest "bridge.log"
$ok = $false
foreach ($i in 1..20) { Start-Sleep -Milliseconds 500; if ((Test-Path $log) -and (Get-Content $log -Tail 3) -match "activated as") { $ok = $true; break } }
Say ""
if ($ok) { Say "Installed. The bridge is connected to G HUB." "Green" }
else { Say "Installed, but the bridge has not reached G HUB yet. It keeps retrying while G HUB is closed." "Yellow" }
Say ""
Say "Next: assign the button in G HUB" "Cyan"
Say "  1. Open G HUB and click the device (headset, mouse or keyboard) you want to use."
Say "  2. Open Assignments, find Claude > Toggle dictation, and drag it onto a button."
Say "  3. Open a Claude Code session in the Claude app, go back to your game and press the button."
Say ""
Say "Log: $log"
