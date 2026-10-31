# weekly skin update, started by the scheduled task (register-weekly-task.ps1)
#   powershell -NoProfile -ExecutionPolicy Bypass -File tools\weekly-update.ps1 [-DryRun]
# -DryRun only checks what's new and writes the report, nothing is downloaded or built
# -NoWait skips the "press any key" pause at the end (the summary also goes to data\weekly\last-run.txt)
# needs STEAM_API_KEY as a user env var (setx STEAM_API_KEY <key>)
# log: data\weekly\logs\<date>.log, report: data\weekly\<date>\report.md
# exit code: 0 fine or nothing new, 1 some skins need a look, 2 couldn't run
param([switch]$DryRun, [switch]$NoWait)

$repo = Split-Path -Parent $PSScriptRoot
Set-Location $repo
$logDir = Join-Path $repo 'data\weekly\logs'
New-Item -ItemType Directory -Force $logDir | Out-Null
$log = Join-Path $logDir ("{0:yyyy-MM-dd_HHmm}.log" -f (Get-Date))

# rebuild first so it runs the latest code
& dotnet build RustSkinToGlb -c Release -v q 2>&1 | Tee-Object -FilePath $log
if ($LASTEXITCODE -ne 0) { "BUILD FAILED" | Tee-Object -FilePath $log -Append; exit 2 }

$exe = Join-Path $repo 'RustSkinToGlb\bin\Release\net8.0\RustSkinToGlb.exe'
$opts = @('--weekly')
if ($DryRun) { $opts += '--dry-run' }
& $exe @opts 2>&1 | Tee-Object -FilePath $log -Append
$code = $LASTEXITCODE

# the summary block is the last thing the exe prints; keep the window open so it can be read after the run.
# Gives up after 15 minutes so a scheduled run never blocks next week's one (the task ignores a second instance)
if (-not $NoWait) {
    Write-Host "Exit code $code. Press any key to close (closes by itself in 15 min)..."
    $end = (Get-Date).AddMinutes(15)
    try { while ((Get-Date) -lt $end -and -not [Console]::KeyAvailable) { Start-Sleep -Seconds 1 } } catch { }
}
exit $code
