@echo off
setlocal EnableExtensions
cd /d "%~dp0"

title HVMC - Hero's Vault Minecraft
color 0F

echo.
echo ==================================================
echo              HVMC - HERO'S VAULT
echo ==================================================
echo.
echo [HVMC] Minecraft starten...
echo.

set "LAUNCHER="
if exist "%ProgramFiles(x86)%\Minecraft Launcher\MinecraftLauncher.exe" set "LAUNCHER=%ProgramFiles(x86)%\Minecraft Launcher\MinecraftLauncher.exe"
if not defined LAUNCHER if exist "%ProgramFiles%\Minecraft Launcher\MinecraftLauncher.exe" set "LAUNCHER=%ProgramFiles%\Minecraft Launcher\MinecraftLauncher.exe"
if not defined LAUNCHER if exist "%LOCALAPPDATA%\Programs\Minecraft Launcher\MinecraftLauncher.exe" set "LAUNCHER=%LOCALAPPDATA%\Programs\Minecraft Launcher\MinecraftLauncher.exe"

if not defined LAUNCHER (
    echo [HVMC] De officiele Minecraft Launcher is niet gevonden.
    echo [HVMC] Voer eerst HVMCUpdateChecker.bat uit.
    echo.
    pause
    exit /b 1
)

echo [HVMC] Officiele Minecraft Launcher gevonden.
echo [HVMC] Launcher starten...
start "" "%LAUNCHER%"
set "EXITCODE=%ERRORLEVEL%"

echo.
echo ==================================================
if "%EXITCODE%"=="0" (
    echo [HVMC] Minecraft Launcher is gestart.
) else (
    echo [HVMC] Launcher kon niet worden gestart. Foutcode: %EXITCODE%
)
echo ==================================================
echo.
pause
exit /b %EXITCODE%
