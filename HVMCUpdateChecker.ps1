$ErrorActionPreference = 'Stop'

$Repo = 'Bendemen-Studios/HVMC'
$Branch = 'main'
$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$Updater = Join-Path $Root 'HVMCUpdater.ps1'
$UpdaterUrl = "https://raw.githubusercontent.com/$Repo/$Branch/HVMCUpdater.ps1"

New-Item -ItemType Directory -Force -Path $Root | Out-Null

function Log([string]$Message) {
    Write-Host "[HVMC] $Message"
}

try {
    Log 'HVMC update checker gestart.'
    $tmp = "$Updater.download"
    Invoke-WebRequest -Uri $UpdaterUrl -OutFile $tmp -UseBasicParsing -Headers @{'User-Agent'='HVMC-Update-Checker'} -TimeoutSec 60
    Move-Item $tmp $Updater -Force
    Log 'Nieuwste updater geladen.'

    $process = Start-Process powershell.exe -ArgumentList @(
        '-NoProfile',
        '-ExecutionPolicy','Bypass',
        '-File',$Updater
    ) -Wait -PassThru -WindowStyle Hidden

    if ($process.ExitCode -ne 0) {
        throw "HVMC updater eindigde met foutcode $($process.ExitCode)."
    }

    Log 'HVMC is bijgewerkt.'
    exit 0
}
catch {
    Remove-Item "$Updater.download" -Force -ErrorAction SilentlyContinue
    Log "Updatecontrole/updater mislukt: $($_.Exception.Message)"
    exit 1
}
