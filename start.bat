@echo off
setlocal EnableExtensions
set "ROOT=%LOCALAPPDATA%\Bendemen\HVMC"
set "EXE=%ROOT%\HVMCLauncher.exe"
set "API=https://api.github.com/repos/Bendemen-Studios/HVMC/releases/latest"
set "TMP=%TEMP%\HVMCLauncher-bootstrap.ps1"

if not exist "%ROOT%" mkdir "%ROOT%" >nul 2>&1

powershell.exe -NoProfile -ExecutionPolicy Bypass -Command ^
  "$ErrorActionPreference='Stop'; $root=$env:LOCALAPPDATA+'\Bendemen\HVMC'; $exe=Join-Path $root 'HVMCLauncher.exe'; $api='https://api.github.com/repos/Bendemen-Studios/HVMC/releases/latest'; try { $j=Invoke-RestMethod -Uri $api -Headers @{'User-Agent'='HVMC-Launcher-Bootstrap'} -TimeoutSec 30; $asset=$j.assets | Where-Object { $_.name -eq 'HVMCLauncher.exe' } | Select-Object -First 1; if(-not $asset){ throw 'HVMCLauncher.exe ontbreekt in de nieuwste release.' }; $tmp=$exe+'.download'; Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tmp -Headers @{'User-Agent'='HVMC-Launcher-Bootstrap'} -UseBasicParsing -TimeoutSec 180; if(!(Test-Path $tmp)){ throw 'Launcher download mislukt.' }; Move-Item $tmp $exe -Force } catch { if(!(Test-Path $exe)){ throw } }"

if errorlevel 1 (
    echo [HVMC] Launcher kon niet worden geladen.
    exit /b 1
)

if not exist "%EXE%" (
    echo [HVMC] HVMC Launcher ontbreekt.
    exit /b 1
)

start "" "%EXE%"
exit /b 0
