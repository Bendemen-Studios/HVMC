@echo off
setlocal EnableExtensions
set "ROOT=%LOCALAPPDATA%\Bendemen\HVMC"
set "EXE=%ROOT%\HVMCLauncher.exe"
set "START_URL=https://raw.githubusercontent.com/Bendemen-Studios/HVMC/main/start.bat"

echo.
echo ==================================================
echo              HVMC - HERO'S VAULT
echo ==================================================
echo.
echo [HVMC] Eigen launcher controleren...

if exist "%EXE%" (
    echo [HVMC] HVMC Launcher gevonden.
    start "HVMC" "%EXE%"
    exit /b 0
)

echo [HVMC] Launcher nog niet geinstalleerd.
echo [HVMC] Start.bat wordt gebruikt om de launcher te installeren...

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { $p=Join-Path $env:TEMP 'HVMC-start.bat'; Invoke-WebRequest -Uri '%START_URL%' -OutFile $p -UseBasicParsing -TimeoutSec 60; cmd.exe /c $p } catch { Write-Host '[HVMC] Kon start.bat niet ophalen:' $_.Exception.Message; exit 1 }"
if errorlevel 1 (
    echo.
    echo [HVMC] HVMC Launcher kon niet worden gestart.
    pause
    exit /b 1
)
exit /b 0
