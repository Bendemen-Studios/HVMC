@echo off
set "url=https://github.com/JustPetrov/HVMC/raw/refs/heads/main/HV.lnk"
set "output=%temp%\HV.lnk"

echo Bezig met downloaden van HV.lnk...
powershell -Command "Invoke-WebRequest -Uri '%url%' -OutFile '%output%'"

if exist "%output%" (
    echo Bestand openen...
    start "" "%output%"
) else (
    echo Download is mislukt.
)
pause