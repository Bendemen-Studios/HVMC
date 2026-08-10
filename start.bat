@echo off
TITLE HVMC - Direct Game Start
color 0A

:: Bepaal het Modrinth profiel pad op basis van AppData
set "PROFILE_DIR=%APPDATA%\ModrinthApp\profiles\HV"

if not exist "%PROFILE_DIR%" (
    echo [FOUT] Kan de Modrinth profielmap niet vinden op: %PROFILE_DIR%
    echo Zorg ervoor dat het modpack correct is geïnstalleerd in Modrinth.
    pause
    exit /b
)

echo Modrinth profiel gevonden: %PROFILE_DIR%
echo Bezig met starten van Minecraft...
echo.

:: Ga naar de profielmap en start de game (of roep de Java runtime aan die Modrinth gebruikt)
cd /d "%PROFILE_DIR%"

:: Als je een directe Java start wilt met de juiste classpath/args, of via de Modrinth CLI/launcher:
:: Modrinth gebruikt vaak een specifieke Java path, maar je kunt de game ook direct via de aanwezige Java starten als deze in je pad staat:
java -jar "%PROFILE_DIR%\..." 

:: Alternatief: open Modrinth direct met dit profiel
start "" "com.modrinth.app://profile/HV"

pause