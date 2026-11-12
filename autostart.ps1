# Starts the bridge through Windows Task Scheduler and keeps it running.
#
# A per-user scheduled task starts the bridge at logon and checks every 5 minutes, starting it again if it
# stopped for any reason. Starting it from Task Scheduler also keeps it out of whatever launched the installer:
# a bridge started from a terminal inside the Claude app belongs to Claude's process group, and Windows ends it
# silently when Claude updates.
$TaskName = "ClaudeGHubBridge"

# Packaged apps (like the Claude desktop app) see a private, redirected copy of AppData, and so does a terminal
# started from inside one, even without an app identity of its own. Files the installer writes to %LOCALAPPDATA%
# then really land in that app's LocalCache folder, which Task Scheduler only sees under its physical path.
# Finds out by writing a marker file and looking for where it physically landed.
function Get-PhysicalPath([string]$path) {
  $local = $env:LOCALAPPDATA.TrimEnd('\')
  $dir = Split-Path $path
  if (-not $dir.StartsWith($local + '\', [StringComparison]::OrdinalIgnoreCase)) { return $path }
  $marker = Join-Path $dir ".where-am-i"
  $id = [guid]::NewGuid().ToString()
  Set-Content $marker $id -Encoding ASCII
  try {
    $rel = $dir.Substring($local.Length + 1)
    foreach ($pkg in Get-ChildItem "$local\Packages" -Directory -ErrorAction SilentlyContinue) {
      $candidate = Join-Path $pkg.FullName "LocalCache\Local\$rel\.where-am-i"
      if ((Test-Path $candidate) -and ((Get-Content $candidate -ErrorAction SilentlyContinue) -eq $id)) {
        return Join-Path (Split-Path $candidate) (Split-Path $path -Leaf)
      }
    }
    return $path
  }
  finally { Remove-Item $marker -Force -ErrorAction SilentlyContinue }
}

function Register-BridgeTask([string]$exe) {
  $action = New-ScheduledTaskAction -Execute $exe
  $user = "$env:USERDOMAIN\$env:USERNAME"
  $atLogon = New-ScheduledTaskTrigger -AtLogOn -User $user
  $watchdog = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
  $settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
  $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
  Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $atLogon, $watchdog -Settings $settings `
    -Principal $principal -Description "Claude G HUB bridge: starts at logon and restarts it if it stops." -Force | Out-Null
  # Versions before 1.2 used a Run key; the task replaces it.
  Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" ClaudeGHubBridge -ErrorAction SilentlyContinue
}

function Start-Bridge {
  Start-ScheduledTask -TaskName $TaskName
}

function Unregister-BridgeTask {
  Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
  Remove-ItemProperty "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" ClaudeGHubBridge -ErrorAction SilentlyContinue
}
