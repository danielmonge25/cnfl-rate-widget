# Installs the CNFL Rate Widget for the current Windows user. No admin rights needed.
#   - creates settings.json from settings.default.json, if you don't have one yet
#   - adds the widget to Startup, so it runs every time you sign in
#   - adds the power log to Startup too, if powerLog.enabled is true in settings.json
#   - starts (or restarts) them now
# Run it again after changing settings.json.
$ErrorActionPreference = 'Stop'
$here = $PSScriptRoot

Get-ChildItem $here -File | Unblock-File   # downloaded files are marked "from the internet"
if (-not (Test-Path (Join-Path $here 'settings.json'))) {
    Copy-Item (Join-Path $here 'settings.default.json') (Join-Path $here 'settings.json')
    Write-Host 'Created settings.json (your settings; edit it to change rates or options).'
}
. (Join-Path $here 'settings.ps1')
$cfg = Get-WidgetSettings $here

$startup = [Environment]::GetFolderPath('Startup')
$ps = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$conhost = Join-Path $env:WINDIR 'System32\conhost.exe'   # --headless: runs PowerShell without a console window

function Set-StartupShortcut($name, $script, $description) {
    $shell = New-Object -ComObject WScript.Shell
    $lnk = $shell.CreateShortcut((Join-Path $startup "$name.lnk"))
    $lnk.TargetPath = $conhost
    $lnk.Arguments = "--headless `"$ps`" -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $here $script)`""
    $lnk.WorkingDirectory = $here
    $lnk.WindowStyle = 7
    $lnk.Description = $description
    $lnk.Save()
}

function Stop-Ours($script) {
    $path = Join-Path $here $script
    Get-CimInstance Win32_Process -Filter "Name='powershell.exe'" |
        Where-Object { $_.CommandLine -like "*$path*" -and $_.ProcessId -ne $PID } |
        ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
}

function Start-Ours($script) {
    Start-Process $conhost -ArgumentList "--headless `"$ps`" -NoProfile -ExecutionPolicy Bypass -File `"$(Join-Path $here $script)`""
}

Set-StartupShortcut 'CNFL rate widget' 'tariff-widget.ps1' 'CNFL T-REH electricity rate widget in the taskbar.'
Stop-Ours 'tariff-widget.ps1'
Start-Ours 'tariff-widget.ps1'
Write-Host 'Widget: added to Startup and started. Look for the colored icon next to the clock.'

$logLink = Join-Path $startup 'CNFL power log.lnk'
Stop-Ours 'power-log.ps1'
if ($cfg.powerLog.enabled) {
    Set-StartupShortcut 'CNFL power log' 'power-log.ps1' 'Logs PC power use once a minute for the CNFL rate widget.'
    Start-Ours 'power-log.ps1'
    Write-Host 'Power log: added to Startup and started (the panel shows what your PC cost after a few minutes).'
} elseif (Test-Path $logLink) {
    Remove-Item $logLink
    Write-Host 'Power log: off (removed from Startup).'
}

Write-Host ''
Write-Host "Installed in: $here"
Write-Host 'Keep this folder where it is: Startup runs the widget from here. To remove it, run uninstall.cmd.'
