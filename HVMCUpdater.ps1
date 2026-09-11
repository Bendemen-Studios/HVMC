$ErrorActionPreference = 'Stop'

$Repo = 'Bendemen-Studios/HVMC'
$Branch = 'main'
$MinecraftDir = Join-Path $env:APPDATA '.minecraft'
$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$ManifestPath = Join-Path $Root 'content-manifest.json'
$StatePath = Join-Path $Root 'state.json'
$LogPath = Join-Path $Root 'bootstrapper.log'
$McVersion = '1.21.11'
$FabricLoader = '0.19.2'
$FabricProfile = "fabric-loader-$FabricLoader-$McVersion"

foreach ($dir in @($Root, $MinecraftDir, $MinecraftDir+'\mods', $MinecraftDir+'\config', $MinecraftDir+'\resourcepacks', $MinecraftDir+'\shaderpacks', $MinecraftDir+'\datapacks', $MinecraftDir+'\kubejs', (Join-Path $MinecraftDir 'versions'))) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

function Log([string]$Message) { Write-Host "[HVMC] $Message"; try { Add-Content -LiteralPath $LogPath -Value (("{0:u} {1}" -f (Get-Date),$Message)) -Encoding UTF8 } catch {} }
function Get-GitHubHeaders { @{ 'User-Agent' = 'HVMC-School-Launcher'; 'Accept' = 'application/vnd.github+json' } }
function ReadJson([string]$Path) { if (-not (Test-Path -LiteralPath $Path)) { return $null }; try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null } }
function SaveJson($Value,[string]$Path) { $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8 }
function Safe([string]$Path) { $p=$Path.Replace('/','\'); if ([IO.Path]::IsPathRooted($p) -or $p.Contains('..')) { throw "Unsafe path: $p" }; return $p }
function Download([string]$Url,[string]$Destination) { $parent=Split-Path -Parent $Destination; New-Item -ItemType Directory -Force -Path $parent | Out-Null; $tmp="$Destination.download"; try { Invoke-WebRequest -Uri $Url -OutFile $tmp -Headers (Get-GitHubHeaders) -UseBasicParsing -TimeoutSec 180; Move-Item -LiteralPath $tmp -Destination $Destination -Force } catch { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue; throw "Download failed for ${Destination}: $($_.Exception.Message)" } }
function Test-GitBlobSha([string]$FilePath,[string]$ExpectedSha) { if (-not (Test-Path -LiteralPath $FilePath)) { return $false }; try { $bytes=[IO.File]::ReadAllBytes($FilePath); $header=[Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0"); $all=New-Object byte[] ($header.Length+$bytes.Length); [Array]::Copy($header,0,$all,0,$header.Length); [Array]::Copy($bytes,0,$all,$header.Length,$bytes.Length); $hash=[Security.Cryptography.SHA1]::HashData($all); $hex=-join ($hash | ForEach-Object { $_.ToString('x2') }); return $hex -ieq $ExpectedSha } catch { return $false } }
function Get-RemoteFiles { $headers=Get-GitHubHeaders; $ref=Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/git/ref/heads/$Branch" -Headers $headers -TimeoutSec 30; $treeSha=[string]$ref.object.sha; $tree=Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/git/trees/$treeSha`?recursive=1" -Headers $headers -TimeoutSec 30; @($tree.tree | Where-Object { $_.type -eq 'blob' -and $_.path -like 'content/*' } | ForEach-Object { [pscustomobject]@{path=[string]$_.path;sha=[string]$_.sha;download="https://raw.githubusercontent.com/$Repo/$Branch/$($_.path)"} }) }

try {
    Log 'HVMC School Launcher updater gestart.'
    $versionResponse=Invoke-WebRequest -Uri "https://raw.githubusercontent.com/$Repo/$Branch/version.txt" -Headers @{'User-Agent'='HVMC-School-Launcher'} -UseBasicParsing -TimeoutSec 20
    $remoteVersion=([string]$versionResponse.Content).Trim()
    if([string]::IsNullOrWhiteSpace($remoteVersion)){throw 'version.txt is leeg.'}
    Log "Beschikbare HVMC versie: $remoteVersion"
    Log "HVMC runtime wordt als content geleverd: Minecraft $McVersion / Fabric $FabricLoader"

    $state=ReadJson $StatePath
    $installedVersion=if($state){[string]$state.installedVersion}else{''}
    $oldManifest=ReadJson $ManifestPath
    $oldEntries=@{}
    if($oldManifest -and $oldManifest.files){foreach($entry in @($oldManifest.files)){$oldEntries[[string]$entry.path]=[string]$entry.sha}}

    # Fast path: behoud de snelle startup, maar controleer nu alle bestanden uit het
    # lokale manifest op aanwezigheid. Zo wordt een handmatig verwijderd bestand niet
    # stilzwijgend overgeslagen. Bij een ontbrekend bestand valt de updater terug op
    # de normale GitHub SHA-verificatie en synchronisatie.
    $fabricJson=Join-Path $MinecraftDir "versions\$FabricProfile\$FabricProfile.json"
    $criticalPresent = Test-Path -LiteralPath $fabricJson
    if($installedVersion -eq $remoteVersion -and $oldManifest -and $criticalPresent){
        $manifestComplete = $true
        foreach($entry in @($oldManifest.files)){
            try {
                $relative = Safe ([string]$entry.path)
                if(-not (Test-Path -LiteralPath (Join-Path $MinecraftDir $relative) -PathType Leaf)) { $manifestComplete = $false; break }
            } catch { $manifestComplete = $false; break }
        }
        if($manifestComplete){
            Log "HVMC versie $remoteVersion is al lokaal gesynchroniseerd. Content-sync overgeslagen."
            Log "Gebundelde Fabric $FabricLoader voor Minecraft $McVersion is aanwezig."
            exit 0
        }
        Log 'Lokale content is incompleet; volledige SHA-controle en synchronisatie wordt uitgevoerd.'
    }

    $remoteFiles=@(Get-RemoteFiles)
    if($remoteFiles.Count -eq 0){throw 'Geen HVMC content gevonden in content/. Upload de volledige Fabric-runtime onder content/ voordat deze launcher wordt gebruikt.'}

    $newManifest=@{}
    foreach($file in $remoteFiles){
        $relative=Safe ([string]$file.path).Substring(8)
        $destination=Join-Path $MinecraftDir $relative
        $expectedSha=[string]$file.sha
        if(-not(Test-GitBlobSha $destination $expectedSha)){Download ([string]$file.download) $destination;Log "Updated: $relative"}
        $newManifest[$relative]=$expectedSha
    }

    foreach($oldPath in @($oldEntries.Keys)){if(-not $newManifest.ContainsKey($oldPath)){$obsolete=Join-Path $MinecraftDir (Safe $oldPath);if(Test-Path -LiteralPath $obsolete){Remove-Item -LiteralPath $obsolete -Force}}}
    $manifestFiles=foreach($key in ($newManifest.Keys|Sort-Object)){[pscustomobject]@{path=$key;sha=$newManifest[$key]}}
    SaveJson ([pscustomobject]@{version=$remoteVersion;files=@($manifestFiles);updated=(Get-Date).ToUniversalTime().ToString('o')}) $ManifestPath
    SaveJson ([pscustomobject]@{installedVersion=$remoteVersion;updated=(Get-Date).ToUniversalTime().ToString('o')}) $StatePath

    if(-not(Test-Path -LiteralPath $fabricJson)){
        throw "Gebundelde Fabric-installatie ontbreekt: content/versions/$FabricProfile/$FabricProfile.json"
    }
    Log "Gebundelde Fabric $FabricLoader voor Minecraft $McVersion is aanwezig."
    Log "HVMC content + gebundelde Fabric-runtime synchronisatie voltooid."
    exit 0
} catch {
    Log "Updater mislukt: $($_.Exception.Message)"
    exit 1
} finally {Log 'HVMC updater afgerond.'}
