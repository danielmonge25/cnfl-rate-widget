@echo off
rem Double-click to stop the CNFL rate widget and remove it from Startup.
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0uninstall.ps1"
pause
