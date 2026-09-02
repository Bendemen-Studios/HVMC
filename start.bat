@echo off
setlocal
cd /d "%~dp0"

title HVMC Bootstrapper

echo.
echo ==========================================
echo          HVMC Bootstrapper
 echo ==========================================
echo.
echo Start.bat blijft open zodat je de voortgang kunt zien.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0HVBootstrapper.ps1"
set "EXITCODE=%ERRORLEVEL%"

echo.
echo ==========================================
if "%EXITCODE%"=="0" (
    echo HVMC bootstrapper is klaar.
) else (
    echo HVMC bootstrapper is gestopt met foutcode %EXITCODE%.
)
echo ==========================================
echo.
pause
exit /b %EXITCODE%
