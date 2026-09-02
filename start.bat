@echo off
setlocal EnableExtensions
set "ROOT=%LOCALAPPDATA%\Bendemen\HVMC"
set "EXE=%ROOT%\HVMCLauncher.exe"
set "API=https://api.github.com/repos/Bendemen-Studios/HVMC/releases/latest"

if not exist "%ROOT%" mkdir "%ROOT%" >nul 2>&1

echo.
echo ==================================================
echo              HVMC - HERO'S VAULT
echo ==================================================
echo.
echo [HVMC] Eigen launcher controleren...

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; $root=$env:LOCALAPPDATA+'\Bendemen\HVMC'; $api='https://api.github.com/repos/Bendemen-Studios/HVMC/releases/latest'; $j=Invoke-RestMethod -Uri $api -Headers @{'User-Agent'='HVMC-Launcher-Bootstrap'} -TimeoutSec 30; $asset=$j.assets | Where-Object { $_.name -eq 'HVMCLauncher.exe' } | Select-Object -First 1; if(-not $asset){throw 'HVMCLauncher.exe ontbreekt in de nieuwste release.'}; Invoke-WebRequest -Uri $asset.browser_download_url -OutFile ($root+'\HVMCLauncher.exe.download') -Headers @{'User-Agent'='HVMC-Launcher-Bootstrap'} -UseBasicParsing -TimeoutSec 180; Move-Item ($root+'\HVMCLauncher.exe.download') ($root+'\HVMCLauncher.exe') -Force"

if errorlevel 1 (
    echo [HVMC] Eigen launcher kon niet worden bijgewerkt.
    pause
    exit /b 1
)

if not exist "%EXE%" (
    echo [HVMC] Eigen launcher ontbreekt na download.
    pause
    exit /b 1
)

echo [HVMC] Eigen HVMC-launcher starten...
start "HVMC" "%EXE%"
exit /b 0
