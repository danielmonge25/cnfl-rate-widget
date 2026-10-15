# Removes the CNFL Rate Widget from Startup and stops it. Your settings.json and logs stay in this
# folder; delete the folder afterwards to remove everything.
$here = $PSScriptRoot
$startup = [Environment]::GetFolderPath('Startup')

# Cancel a pending "turn off when Claude is done" first, if one is armed.
$pidFile = Join-Path $here 'logs\shutdown-watch.pid'
if (Test-Path $pidFile) {
    $p = Get-Process -Id ([int](Get-Content $pidFile -Raw).Trim()) -ErrorAction SilentlyContinue
    if ($p -and $p.ProcessName -eq 'powershell') { Stop-Process -Id $p.Id -Force; shutdown.exe /a 2>$null }
}

foreach ($script in 'tariff-widget.ps1', 'power-log.ps1', 'shutdown-when-done.ps1') {
    $path = Join-Path $here $script
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -like "*$path*" -and $_.ProcessId -ne $PID } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}
foreach ($name in 'CNFL rate widget.lnk', 'CNFL power log.lnk') {
    $lnk = Join-Path $startup $name
    if (Test-Path $lnk) { Remove-Item $lnk }
}
Write-Host 'The CNFL rate widget is stopped and removed from Startup.'
Write-Host "Your settings and logs are still in $here. Delete the folder to remove everything."
