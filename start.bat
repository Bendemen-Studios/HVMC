@echo off
setlocal
cd /d "%~dp0"

title HVMC - Hero's Vault Minecraft
color 0F

echo.
echo ==================================================
echo              HVMC - HERO'S VAULT
echo ==================================================
echo.
echo [HVMC] Bootstrapper starten...
echo [HVMC] Je kunt in dit venster de voortgang volgen.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0HVBootstrapper.ps1"
set "EXITCODE=%ERRORLEVEL%"

echo.
echo ==================================================
if "%EXITCODE%"=="0" (
    echo [HVMC] Bootstrapper voltooid.
) else (
    echo [HVMC] Bootstrapper gestopt met foutcode %EXITCODE%.
)
echo ==================================================
echo.
pause
exit /b %EXITCODE%
