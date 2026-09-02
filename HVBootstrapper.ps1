$ErrorActionPreference = 'Stop'

$Repo = 'Bendemen-Studios/HVMC'
$Branch = 'main'
$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$PackPath = Join-Path $Root 'HV.mrpack'
$ShortcutPath = Join-Path $Root 'HV.lnk'
$StatePath = Join-Path $Root 'state.json'
$LogPath = Join-Path $Root 'bootstrapper.log'

New-Item -ItemType Directory -Force -Path $Root | Out-Null

function Write-Log([string]$Message) {
    try {
        Add-Content -LiteralPath $LogPath -Value ("{0:u} {1}" -f (Get-Date), $Message) -Encoding UTF8
    } catch {}
}

function Get-GitHubFile([string]$Name) {
    $api = "https://api.github.com/repos/$Repo/contents/$Name?ref=$Branch"
    return Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'HVMC-Bootstrapper' } -Method Get -TimeoutSec 15
}

function Download-File([string]$Url, [string]$Destination) {
    $tmp = "$Destination.download"
    try {
        Invoke-WebRequest -Uri $Url -OutFile $tmp -Headers @{ 'User-Agent' = 'HVMC-Bootstrapper' } -UseBasicParsing -TimeoutSec 60
        if (-not (Test-Path -LiteralPath $tmp)) { throw 'Download did not create a file.' }
        Move-Item -LiteralPath $tmp -Destination $Destination -Force
        return $true
    } catch {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        Write-Log "Download failed: $Url :: $($_.Exception.Message)"
        return $false
    }
}

function Read-State {
    if (Test-Path -LiteralPath $StatePath) {
        try { return Get-Content -LiteralPath $StatePath -Raw | ConvertFrom-Json } catch {}
    }
    return [pscustomobject]@{ PackSha = ''; ShortcutSha = '' }
}

function Save-State([string]$PackSha, [string]$ShortcutSha) {
    [pscustomobject]@{ PackSha = $PackSha; ShortcutSha = $ShortcutSha; Updated = (Get-Date).ToUniversalTime().ToString('o') } |
        ConvertTo-Json | Set-Content -LiteralPath $StatePath -Encoding UTF8
}

Write-Log 'Bootstrapper started.'
$state = Read-State

# Check and update the packaged modpack/launcher files. Failure is non-fatal so
# an existing installation can still be launched while offline.
try {
    $remotePack = Get-GitHubFile 'HV.mrpack'
    $remoteShortcut = Get-GitHubFile 'HV.lnk'

    if ($remotePack.sha -and $remotePack.sha -ne $state.PackSha) {
        if (Download-File $remotePack.download_url $PackPath) {
            $state.PackSha = $remotePack.sha
            Write-Log "Updated HV.mrpack to $($remotePack.sha)."
        }
    }

    if ($remoteShortcut.sha -and $remoteShortcut.sha -ne $state.ShortcutSha) {
        if (Download-File $remoteShortcut.download_url $ShortcutPath) {
            $state.ShortcutSha = $remoteShortcut.sha
            Write-Log "Updated HV.lnk to $($remoteShortcut.sha)."
        }
    }

    Save-State $state.PackSha $state.ShortcutSha
} catch {
    Write-Log "Update check skipped: $($_.Exception.Message)"
}

# The shortcut remains the compatibility launch mechanism for the installed
# Modrinth profile. Use WScript Shell so this bootstrapper does not leave a
# visible cmd/PowerShell window behind.
if (Test-Path -LiteralPath $ShortcutPath) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shell.Run('"' + $ShortcutPath + '"', 0, $false)
        Write-Log 'HV.lnk launched.'
        exit 0
    } catch {
        Write-Log "Shortcut launch failed: $($_.Exception.Message)"
    }
}

# First-run/fallback: download the shortcut even if the state file was corrupt.
try {
    $remoteShortcut = Get-GitHubFile 'HV.lnk'
    if (Download-File $remoteShortcut.download_url $ShortcutPath) {
        $shell = New-Object -ComObject WScript.Shell
        $shell.Run('"' + $ShortcutPath + '"', 0, $false)
        Write-Log 'HV.lnk downloaded and launched.'
        exit 0
    }
} catch {
    Write-Log "Fallback launch failed: $($_.Exception.Message)"
}

Write-Log 'No launcher available.'
exit 1
