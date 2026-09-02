$ErrorActionPreference = 'Stop'

$Repo = 'Bendemen-Studios/HVMC'
$Branch = 'main'
$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$BootstrapperPath = Join-Path $Root 'HVBootstrapper.ps1'
$RemoteBootstrapper = "https://raw.githubusercontent.com/$Repo/$Branch/HVBootstrapper.ps1"
$RemoteStart = "https://raw.githubusercontent.com/$Repo/$Branch/start.bat"
$RemoteVersion = "https://raw.githubusercontent.com/$Repo/$Branch/version.txt"
$LocalVersionPath = Join-Path $Root 'version.txt'
$RemotePackUrl = "https://api.github.com/repos/$Repo/contents/HV.mrpack?ref=$Branch"

New-Item -ItemType Directory -Force -Path $Root | Out-Null

function Write-Line([string]$Text) { Write-Host "[HVMC] $Text" }
function Download([string]$Url,[string]$Path) {
    $tmp = "$Path.download"
    try {
        Invoke-WebRequest -Uri $Url -OutFile $tmp -UseBasicParsing -Headers @{'User-Agent'='HVMC-Updater'} -TimeoutSec 180
        Move-Item $tmp $Path -Force
    } finally { Remove-Item $tmp -Force -ErrorAction SilentlyContinue }
}

try {
    Write-Line 'HVMC updater gestart.'
    $remoteVersion = ([string](Invoke-RestMethod -Uri $RemoteVersion -Headers @{'User-Agent'='HVMC-Updater'} -TimeoutSec 20)).Trim()
    $localVersion = if(Test-Path $LocalVersionPath){(Get-Content $LocalVersionPath -Raw).Trim()}else{''}

    $remotePack = Invoke-RestMethod -Uri $RemotePackUrl -Headers @{'User-Agent'='HVMC-Updater'} -TimeoutSec 20
    $remotePackSha = [string]$remotePack.sha
    $statePath = Join-Path $Root 'state.json'
    $localPackSha = ''
    if(Test-Path $statePath){try{$localPackSha=[string]((Get-Content $statePath -Raw)|ConvertFrom-Json).packSha}catch{}}

    $updateNeeded = (-not $localVersion) -or ($localVersion -ne $remoteVersion) -or (-not $localPackSha) -or ($localPackSha -ne $remotePackSha)

    if(-not $updateNeeded){
        Write-Line "HVMC is up-to-date ($localVersion)."
        exit 0
    }

    Write-Line "Update gevonden: $localVersion -> $remoteVersion"
    Write-Line 'Updater en bootstrapper downloaden...'
    Download $RemoteBootstrapper $BootstrapperPath

    Write-Line 'Modpack, mods, configs, Minecraft en Fabric synchroniseren...'
    $process = Start-Process powershell.exe -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-File',$BootstrapperPath,'-UpdateOnly') -Wait -PassThru
    if($process.ExitCode -ne 0){throw "Bootstrapper eindigde met foutcode $($process.ExitCode)."}

    Set-Content -LiteralPath $LocalVersionPath -Value $remoteVersion -Encoding UTF8
    Write-Line "Update voltooid. Versie $remoteVersion is geïnstalleerd."
    exit 10
}
catch {
    Write-Line "Updater mislukt: $($_.Exception.Message)"
    exit 20
}
