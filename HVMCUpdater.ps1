$ErrorActionPreference = 'Stop'

$Repo = 'Bendemen-Studios/HVMC'
$Branch = 'main'
$MinecraftDir = Join-Path $env:APPDATA '.minecraft'
$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$ManifestPath = Join-Path $Root 'content-manifest.json'
$StatePath = Join-Path $Root 'state.json'
$LogPath = Join-Path $Root 'bootstrapper.log'
$McVersion = '1.21.11'
$FabricLoader = '0.18.1'

foreach ($dir in @($Root,$MinecraftDir,$MinecraftDir+'\mods',$MinecraftDir+'\config',$MinecraftDir+'\resourcepacks',$MinecraftDir+'\shaderpacks',$MinecraftDir+'\datapacks',$MinecraftDir+'\kubejs')) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

function Log([string]$Message) {
    Write-Host "[HVMC] $Message"
    try { Add-Content -LiteralPath $LogPath -Value (("{0:u} {1}" -f (Get-Date),$Message)) -Encoding UTF8 } catch {}
}
function GitHub([string]$Path) {
    Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/contents/$Path?ref=$Branch" -Headers @{'User-Agent'='HVMC-Updater'} -TimeoutSec 30
}
function Safe([string]$Path) {
    $p = $Path.Replace('/','\')
    if ([IO.Path]::IsPathRooted($p) -or $p.Contains('..')) { throw "Unsafe path: $p" }
    return $p
}
function Download([string]$Url,[string]$Destination) {
    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $tmp = "$Destination.download"
    try {
        Invoke-WebRequest -Uri $Url -OutFile $tmp -Headers @{'User-Agent'='HVMC-Updater'} -UseBasicParsing -TimeoutSec 180
        if (-not (Test-Path $tmp)) { throw 'Download did not create a file.' }
        Move-Item $tmp $Destination -Force
    } catch {
        Remove-Item $tmp -Force -ErrorAction SilentlyContinue
        throw "Download failed for ${Destination}: $($_.Exception.Message)"
    }
}
function ReadJson([string]$Path) {
    if (-not (Test-Path $Path)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null }
}
function SaveJson($Value,[string]$Path) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
}
function CollectFiles([string]$RemotePath='content') {
    $result = @()
    foreach ($item in @(GitHub $RemotePath)) {
        if ($item.type -eq 'dir') { $result += CollectFiles ([string]$item.path) }
        elseif ($item.type -eq 'file') { $result += [pscustomobject]@{ path=[string]$item.path; sha=[string]$item.sha; download=[string]$item.download_url } }
    }
    return $result
}
function Test-GitBlobSha([string]$FilePath,[string]$ExpectedSha) {
    if (-not (Test-Path -LiteralPath $FilePath)) { return $false }
    try {
        $bytes = [IO.File]::ReadAllBytes($FilePath)
        $header = [Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0")
        $all = New-Object byte[] ($header.Length + $bytes.Length)
        [Array]::Copy($header,0,$all,0,$header.Length)
        [Array]::Copy($bytes,0,$all,$header.Length,$bytes.Length)
        $hash = [Security.Cryptography.SHA1]::HashData($all)
        $hex = -join ($hash | ForEach-Object { $_.ToString('x2') })
        return $hex -ieq $ExpectedSha
    } catch { return $false }
}
function Ensure-OfficialLauncher {
    $paths = @(
        (Join-Path ${env:ProgramFiles(x86)} 'Minecraft Launcher\MinecraftLauncher.exe'),
        (Join-Path $env:ProgramFiles 'Minecraft Launcher\MinecraftLauncher.exe'),
        (Join-Path $env:LOCALAPPDATA 'Programs\Minecraft Launcher\MinecraftLauncher.exe')
    )
    foreach ($p in $paths) { if ($p -and (Test-Path $p)) { Log 'Official Minecraft Launcher gevonden.'; return $p } }
    $winget = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($winget) {
        Log 'Official Minecraft Launcher ontbreekt; installeren via winget...'
        try { & $winget.Source install --id Mojang.MinecraftLauncher --exact --silent --accept-package-agreements --accept-source-agreements | Out-Null } catch { Log "WinGet fout: $($_.Exception.Message)" }
        foreach ($p in $paths) { if ($p -and (Test-Path $p)) { Log 'Official Minecraft Launcher geïnstalleerd.'; return $p } }
    }
    Start-Process 'https://www.minecraft.net/download'
    throw 'Official Minecraft Launcher kon niet automatisch worden geïnstalleerd.'
}
function Ensure-Fabric {
    $target = Join-Path $MinecraftDir "versions\fabric-loader-$FabricLoader-$McVersion\fabric-loader-$FabricLoader-$McVersion.json"
    if (Test-Path $target) { Log "Fabric $FabricLoader voor Minecraft $McVersion gevonden."; return }
    Log "Fabric $FabricLoader voor Minecraft $McVersion installeren..."
    $meta = Invoke-RestMethod -Uri 'https://meta.fabricmc.net/v2/versions/installer' -Headers @{'User-Agent'='HVMC-Updater'} -TimeoutSec 30
    $installer = $meta | Where-Object { $_.stable -eq $true -and $_.exe } | Select-Object -First 1
    if (-not $installer) { throw 'Geen stabiele Fabric installer gevonden.' }
    $installerPath = Join-Path $Root 'fabric-installer.exe'
    Download ([string]$installer.exe) $installerPath
    $proc = Start-Process $installerPath -ArgumentList 'client','-dir',('"'+$MinecraftDir+'"'),'-mcversion',$McVersion,'-loader',$FabricLoader -Wait -PassThru
    if ($proc.ExitCode -ne 0 -or -not (Test-Path $target)) { throw 'Fabric installatie mislukt.' }
    Log 'Fabric installatie voltooid.'
}

try {
    Log 'HVMC updater gestart.'
    $remoteVersion = ([string](Invoke-RestMethod -Uri "https://raw.githubusercontent.com/$Repo/$Branch/version.txt" -Headers @{'User-Agent'='HVMC-Updater'} -TimeoutSec 20)).Trim()
    Log "Beschikbare HVMC versie: $remoteVersion"
    $remoteFiles = @(CollectFiles 'content')
    $oldManifest = ReadJson $ManifestPath
    $oldEntries = @{}
    if ($oldManifest -and $oldManifest.files) { foreach ($entry in @($oldManifest.files)) { $oldEntries[[string]$entry.path] = [string]$entry.sha } }
    $newManifest = @{}
    foreach ($file in $remoteFiles) {
        $relative = Safe ([string]$file.path).Substring(8)
        $destination = Join-Path $MinecraftDir $relative
        $needsUpdate = -not (Test-GitBlobSha $destination ([string]$file.sha))
        if ($needsUpdate) { Download ([string]$file.download) $destination; Log "Updated: $relative" } else { Log "Unchanged: $relative" }
        $newManifest[$relative] = [string]$file.sha
    }
    foreach ($oldPath in @($oldEntries.Keys)) {
        if (-not $newManifest.ContainsKey($oldPath)) {
            $obsolete = Join-Path $MinecraftDir (Safe $oldPath)
            if (Test-Path $obsolete) {
                try { Remove-Item -LiteralPath $obsolete -Force; Log "Removed obsolete: $oldPath" } catch { Log "Could not remove obsolete ${oldPath}: $($_.Exception.Message)" }
            }
        }
    }
    $manifestFiles = foreach ($key in ($newManifest.Keys | Sort-Object)) { [pscustomobject]@{ path=$key; sha=$newManifest[$key] } }
    SaveJson ([pscustomobject]@{ version=$remoteVersion; files=@($manifestFiles); updated=(Get-Date).ToUniversalTime().ToString('o') }) $ManifestPath
    SaveJson ([pscustomobject]@{ installedVersion=$remoteVersion; updated=(Get-Date).ToUniversalTime().ToString('o') }) $StatePath
    Ensure-OfficialLauncher | Out-Null
    Ensure-Fabric
    Log 'HVMC update/installatie voltooid.'
    exit 0
} catch {
    Log "Updater mislukt: $($_.Exception.Message)"
    exit 1
} finally {
    Log 'HVMC updater afgerond.'
}
