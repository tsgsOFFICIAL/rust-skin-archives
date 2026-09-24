# registers / removes the scheduled task that runs weekly-update.ps1 every Friday at 16:00
# (friday because the store sometimes rotates slowly and isn't fully updated on thursday)
#   powershell -NoProfile -ExecutionPolicy Bypass -File pipeline\tools\register-weekly-task.ps1
#   powershell -NoProfile -ExecutionPolicy Bypass -File pipeline\tools\register-weekly-task.ps1 -Time 18:30
#   powershell -NoProfile -ExecutionPolicy Bypass -File pipeline\tools\register-weekly-task.ps1 -Remove
# runs as the current user and only while logged in (needs STEAM_API_KEY), wakes the pc
# if it's asleep and catches up when a run was missed
param([string]$Time = '16:00', [switch]$Remove)

$name = 'Rust Skins weekly update'
if ($Remove) { Unregister-ScheduledTask -TaskName $name -Confirm:$false; "Removed task '$name'."; return }

$script = Join-Path $PSScriptRoot 'weekly-update.ps1'
$action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$script`""
$trigger = New-ScheduledTaskTrigger -Weekly -DaysOfWeek Friday -At $Time
$settings = New-ScheduledTaskSettingsSet -StartWhenAvailable -WakeToRun -ExecutionTimeLimit (New-TimeSpan -Hours 8) -MultipleInstances IgnoreNew
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
Register-ScheduledTask -TaskName $name -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
"Registered '$name': every Friday at $Time. Test it now with:  Start-ScheduledTask -TaskName '$name'"
