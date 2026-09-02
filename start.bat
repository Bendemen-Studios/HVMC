@echo off
setlocal EnableExtensions
set "ROOT=%LOCALAPPDATA%\Bendemen\HVMC"
set "SCRIPT=%ROOT%\HVMCLauncher.ps1"
set "URL=https://raw.githubusercontent.com/Bendemen-Studios/HVMC/main/HVMCLauncher.ps1"

if not exist "%ROOT%" mkdir "%ROOT%" >nul 2>&1

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { Invoke-WebRequest -Uri '%URL%' -OutFile '%SCRIPT%.download' -UseBasicParsing -TimeoutSec 60; Move-Item '%SCRIPT%.download' '%SCRIPT%' -Force } catch { if (-not (Test-Path '%SCRIPT%')) { exit 1 } }"
if errorlevel 1 (
    powershell.exe -NoProfile -Command "Add-Type -AssemblyName PresentationFramework; [System.Windows.MessageBox]::Show('HVMC Launcher kon niet worden geladen.','HVMC','OK','Error')"
    exit /b 1
)

start "HVMC" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%SCRIPT%"
exit /b 0
