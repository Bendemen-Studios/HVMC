@echo off
setlocal
cd /d "%~dp0"

title HVMC Update Checker
color 0F

echo.
echo ==================================================
echo          HVMC - UPDATE CHECKER
echo ==================================================
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0HVMCUpdateChecker.ps1"
set "EXITCODE=%ERRORLEVEL%"

echo.
if "%EXITCODE%"=="0" (
    echo [HVMC] Geen update beschikbaar.
) else if "%EXITCODE%"=="10" (
    echo [HVMC] Er is een update beschikbaar of dit is de eerste installatie.
) else (
    echo [HVMC] Updatecontrole mislukt. Foutcode: %EXITCODE%
)
echo.
echo ==================================================
echo.
pause
exit /b %EXITCODE%
