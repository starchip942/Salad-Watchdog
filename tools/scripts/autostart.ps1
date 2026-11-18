# Makes GPU Watchdog start by itself when you sign in to Windows, hidden in the tray.
# Uses a Windows scheduled task, so it starts as administrator WITHOUT a permission prompt at every boot.
# Run again any time; it just updates the task. "-Remove" takes it away again.
param([switch]$Remove)

$root = Split-Path (Split-Path $PSScriptRoot)
$exe  = Join-Path $root 'agent\GPU-Watchdog-Agent.exe'
$cfg  = Join-Path $root 'config\agent.config.json'
$name = 'GPU Watchdog'

# Needs administrator once (to create the task). Ask for it if we don't have it.
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
  $a = @('-NoProfile','-ExecutionPolicy','Bypass','-File',"`"$PSCommandPath`"")
  if ($Remove) { $a += '-Remove' }
  Start-Process powershell -ArgumentList $a -Verb RunAs
  exit
}

try {
  if ($Remove) {
    Unregister-ScheduledTask -TaskName $name -Confirm:$false -ErrorAction Stop
    Unregister-ScheduledTask -TaskName "$name keep-alive" -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Done. GPU Watchdog will no longer start with Windows." -ForegroundColor Green
  } else {
    if (-not (Test-Path $exe)) { throw "Can't find $exe" }
    $user = "$env:USERDOMAIN\$env:USERNAME"
    $action    = New-ScheduledTaskAction -Execute $exe -WorkingDirectory (Split-Path $exe)
    $trigger   = New-ScheduledTaskTrigger -AtLogOn -User $user
    $trigger.Delay = 'PT20S'   # give Windows 20 seconds to settle first
    $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
    $settings  = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                   -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
    Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Principal $principal -Settings $settings `
      -Description 'Starts GPU Watchdog in the tray when you sign in.' -Force | Out-Null

    # Keep-alive: every 5 minutes, start the watchdog again if it closed by itself (crash, failed update).
    # It does nothing while the watchdog runs, and nothing after you Quit it on purpose (until you start it again).
    $kaAction  = New-ScheduledTaskAction -Execute $exe -Argument '--keepalive' -WorkingDirectory (Split-Path $exe)
    $kaTrigger = New-ScheduledTaskTrigger -AtLogOn -User $user
    $kaTrigger.Delay = 'PT3M'
    $kaTrigger.Repetition = (New-ScheduledTaskTrigger -Once -At (Get-Date) -RepetitionInterval (New-TimeSpan -Minutes 5)).Repetition
    $kaSettings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                   -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -StartWhenAvailable
    Register-ScheduledTask -TaskName "$name keep-alive" -Action $kaAction -Trigger $kaTrigger -Principal $principal -Settings $kaSettings `
      -Description 'Starts GPU Watchdog again if it closed by itself. Does nothing while it runs or after you Quit it.' -Force | Out-Null

    # Start hidden in the tray (same as the panel's "Start hidden in the tray" switch)
    if (Test-Path $cfg) {
      $j = Get-Content $cfg -Raw
      if ($j -match '"StartHiddenInTray"\s*:\s*false') {
        [IO.File]::WriteAllText($cfg, ($j -replace '"StartHiddenInTray"\s*:\s*false', '"StartHiddenInTray":true'))
      }
    }
    Write-Host "Done. GPU Watchdog will now start by itself in the tray when you sign in to Windows." -ForegroundColor Green
  }
} catch {
  Write-Host "Something went wrong: $($_.Exception.Message)" -ForegroundColor Red
}
Write-Host ''
Read-Host 'Press Enter to close'
