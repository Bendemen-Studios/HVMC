@echo off
setlocal
cd /d "%~dp0"

rem Hero's Vault MC Bootstrapper
rem No console/UI from the updater itself.
start "" /min powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File "%~dp0HVBootstrapper.ps1"
exit /b 0
