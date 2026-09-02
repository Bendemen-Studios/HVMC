@echo off
setlocal EnableExtensions
set "ROOT=%LOCALAPPDATA%\Bendemen\HVMC"
set "EXE=%ROOT%\HVMCLauncher.exe"
set "API=https://api.github.com/repos/Bendemen-Studios/HVMC/releases/latest"
set "PS1=%ROOT%\HVMCLauncher.ps1"
set "PS1URL=https://raw.githubusercontent.com/Bendemen-Studios/HVMC/main/HVMCLauncher.ps1"

if not exist "%ROOT%" mkdir "%ROOT%" >nul 2>&1

echo.
echo ==================================================
echo              HVMC - HERO'S VAULT
echo ==================================================
echo.
echo [HVMC] Eigen launcher controleren...

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; $root=$env:LOCALAPPDATA+'\Bendemen\HVMC'; $api='https://api.github.com/repos/Bendemen-Studios/HVMC/releases/latest'; $j=Invoke-RestMethod -Uri $api -Headers @{'User-Agent'='HVMC-Launcher-Bootstrap'} -TimeoutSec 30; $asset=$j.assets | Where-Object { $_.name -eq 'HVMCLauncher.exe' } | Select-Object -First 1; if($asset){ Invoke-WebRequest -Uri $asset.browser_download_url -OutFile ($root+'\HVMCLauncher.exe.download') -Headers @{'User-Agent'='HVMC-Launcher-Bootstrap'} -UseBasicParsing -TimeoutSec 180; Move-Item ($root+'\HVMCLauncher.exe.download') ($root+'\HVMCLauncher.exe') -Force }"

if exist "%EXE%" (
    echo [HVMC] Eigen HVMC-launcher starten...
    start "HVMC" "%EXE%"
    exit /b 0
)

echo [HVMC] Native launcher nog niet beschikbaar; compatibiliteitslauncher starten...
powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "try { Invoke-WebRequest -Uri '%PS1URL%' -OutFile '%PS1%.download' -UseBasicParsing -TimeoutSec 60; Move-Item '%PS1%.download' '%PS1%' -Force } catch { if (-not (Test-Path '%PS1%')) { exit 1 } }"
if errorlevel 1 (
    echo [HVMC] Launcher kon niet worden geladen.
    pause
    exit /b 1
)
start "HVMC" powershell.exe -NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File "%PS1%"
exit /b 0
