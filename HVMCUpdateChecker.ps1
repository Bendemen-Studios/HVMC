$ErrorActionPreference = 'Stop'

$Repo = 'Bendemen-Studios/HVMC'
$Branch = 'main'
$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$StatePath = Join-Path $Root 'state.json'
$RemoteUrl = "https://api.github.com/repos/$Repo/contents/HV.mrpack?ref=$Branch"
$VersionUrl = "https://raw.githubusercontent.com/$Repo/$Branch/version.txt"

function Write-Line([string]$Text) { Write-Host "[HVMC] $Text" }

try {
    Write-Line 'Update controleren...'

    $remote = Invoke-RestMethod -Uri $RemoteUrl -Headers @{'User-Agent'='HVMC-Update-Checker'} -TimeoutSec 20
    $remoteSha = [string]$remote.sha
    $remoteVersion = $null
    try {
        $remoteVersion = ([string](Invoke-RestMethod -Uri $VersionUrl -Headers @{'User-Agent'='HVMC-Update-Checker'} -TimeoutSec 20)).Trim()
    } catch {}

    $localSha = ''
    if (Test-Path $StatePath) {
        try {
            $state = Get-Content $StatePath -Raw | ConvertFrom-Json
            $localSha = [string]$state.packSha
        } catch {}
    }

    if (-not $localSha) {
        Write-Line 'Geen lokale HVMC-versie gevonden.'
        Write-Line "Beschikbare versie: $remoteVersion"
        Write-Line 'Dit is waarschijnlijk de eerste installatie.'
        exit 10
    }

    if ($localSha -ne $remoteSha) {
        Write-Line 'UPDATE BESCHIKBAAR!'
        if ($remoteVersion) { Write-Line "Nieuwe versie: $remoteVersion" }
        Write-Line 'Start.bat zal bij de volgende start de nieuwe bestanden synchroniseren.'
        exit 10
    }

    Write-Line 'HVMC is up-to-date.'
    if ($remoteVersion) { Write-Line "Versie: $remoteVersion" }
    exit 0
}
catch {
    Write-Line "Updatecontrole mislukt: $($_.Exception.Message)"
    exit 20
}
