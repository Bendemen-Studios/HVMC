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

foreach ($dir in @($Root, $MinecraftDir, $MinecraftDir+'\mods', $MinecraftDir+'\config', $MinecraftDir+'\resourcepacks', $MinecraftDir+'\shaderpacks', $MinecraftDir+'\datapacks', $MinecraftDir+'\kubejs', (Join-Path $MinecraftDir 'versions'))) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

function Log([string]$Message) { Write-Host "[HVMC] $Message"; try { Add-Content -LiteralPath $LogPath -Value (("{0:u} {1}" -f (Get-Date),$Message)) -Encoding UTF8 } catch {} }
function Get-GitHubHeaders { @{ 'User-Agent' = 'HVMC-School-Launcher'; 'Accept' = 'application/vnd.github+json' } }
function ReadJson([string]$Path) { if (-not (Test-Path -LiteralPath $Path)) { return $null }; try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null } }
function SaveJson($Value,[string]$Path) { $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8 }
function Safe([string]$Path) { $p=$Path.Replace('/','\'); if ([IO.Path]::IsPathRooted($p) -or $p.Contains('..')) { throw "Unsafe path: $p" }; return $p }
function Download([string]$Url,[string]$Destination) { $parent=Split-Path -Parent $Destination; New-Item -ItemType Directory -Force -Path $parent | Out-Null; $tmp="$Destination.download"; try { Invoke-WebRequest -Uri $Url -OutFile $tmp -Headers (Get-GitHubHeaders) -UseBasicParsing -TimeoutSec 180; Move-Item -LiteralPath $tmp -Destination $Destination -Force } catch { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue; throw "Download failed for ${Destination}: $($_.Exception.Message)" } }
function Test-GitBlobSha([string]$FilePath,[string]$ExpectedSha) { if (-not (Test-Path -LiteralPath $FilePath)) { return $false }; try { $bytes=[IO.File]::ReadAllBytes($FilePath); $header=[Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0"); $all=New-Object byte[] ($header.Length+$bytes.Length); [Array]::Copy($header,0,$all,0,$header.Length); [Array]::Copy($bytes,0,$all,$header.Length,$bytes.Length); $hash=[Security.Cryptography.SHA1]::HashData($all); $hex=-join ($hash | ForEach-Object { $_.ToString('x2') }); return $hex -ieq $ExpectedSha } catch { return $false } }
function Get-RemoteFiles { $headers=Get-GitHubHeaders; $ref=Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/git/ref/heads/$Branch" -Headers $headers -TimeoutSec 30; $treeSha=[string]$ref.object.sha; $tree=Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/git/trees/$treeSha`?recursive=1" -Headers $headers -TimeoutSec 30; @($tree.tree | Where-Object { $_.type -eq 'blob' -and $_.path -like 'content/*' } | ForEach-Object { [pscustomobject]@{path=[string]$_.path;sha=[string]$_.sha;download="https://raw.githubusercontent.com/$Repo/$Branch/$($_.path)"} }) }
function Find-Java { $runtimeRoot=Join-Path $MinecraftDir 'runtime'; if (Test-Path -LiteralPath $runtimeRoot) { $r=Get-ChildItem -LiteralPath $runtimeRoot -Filter 'java.exe' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1; if($r){return $r.FullName} }; $j=Get-Command java.exe -ErrorAction SilentlyContinue; if($j){return $j.Source}; foreach($pattern in @((Join-Path ${env:ProgramFiles} 'Java\*\bin\java.exe'),(Join-Path ${env:ProgramFiles} 'Eclipse Adoptium\*\bin\java.exe'),(Join-Path ${env:ProgramFiles(x86)} 'Java\*\bin\java.exe'))){$c=Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue|Select-Object -First 1;if($c){return $c.FullName}}; return $null }
function Ensure-Fabric {
    $versionsRoot=Join-Path $MinecraftDir 'versions'
    $targetDir=Join-Path $versionsRoot "fabric-loader-$FabricLoader-$McVersion"
    $target=Join-Path $targetDir "fabric-loader-$FabricLoader-$McVersion.json"

    # The Fabric loader is part of the HVMC installation state. Remove every
    # other Fabric profile for this Minecraft version so an old loader can
    # never be selected accidentally.
    $fabricProfiles=@(Get-ChildItem -LiteralPath $versionsRoot -Directory -Filter "fabric-loader-*-$McVersion" -ErrorAction SilentlyContinue)
    foreach($profile in $fabricProfiles){
        if($profile.FullName -ne $targetDir){
            Log "Verouderd Fabric-profiel verwijderen: $($profile.Name)"
            Remove-Item -LiteralPath $profile.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    if(Test-Path -LiteralPath $target){
        Log "Fabric $FabricLoader voor Minecraft $McVersion is correct geïnstalleerd."
        return
    }

    Log "Fabric $FabricLoader voor Minecraft $McVersion installeren..."
    $meta=Invoke-RestMethod -Uri 'https://meta.fabricmc.net/v2/versions/installer' -Headers @{'User-Agent'='HVMC-School-Launcher';'Accept'='application/json'} -TimeoutSec 30
    $installer=@($meta|Where-Object{$_.stable -eq $true -and $_.url})|Select-Object -First 1
    if(-not $installer){throw 'Geen stabiele Fabric installer gevonden.'}
    $javaPath=Find-Java
    if(-not $javaPath){throw 'Java runtime niet gevonden.'}
    $installerVersion=[string]$installer.version
    $installerPath=Join-Path $Root "fabric-installer-$installerVersion.jar"
    Download ([string]$installer.url) $installerPath
    $args=@('-jar',$installerPath,'client','-dir',$MinecraftDir,'-mcversion',$McVersion,'-loader',$FabricLoader,'-noprofile')
    $proc=Start-Process -FilePath $javaPath -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
    if($proc.ExitCode -ne 0 -or -not(Test-Path -LiteralPath $target)){throw "Fabric CLI-installatie mislukt (exitcode $($proc.ExitCode))."}
    Log "Fabric $FabricLoader installatie voltooid."
}

try {
    Log 'HVMC School Launcher updater gestart.'
    $versionResponse=Invoke-WebRequest -Uri "https://raw.githubusercontent.com/$Repo/$Branch/version.txt" -Headers @{'User-Agent'='HVMC-School-Launcher'} -UseBasicParsing -TimeoutSec 20
    $remoteVersion=([string]$versionResponse.Content).Trim()
    if([string]::IsNullOrWhiteSpace($remoteVersion)){throw 'version.txt is leeg.'}
    Log "Beschikbare HVMC versie: $remoteVersion"

    # Always synchronize the runtime/loader together with HVMC content.
    Log "HVMC runtime controleren: Minecraft $McVersion / Fabric $FabricLoader"
    Ensure-Fabric

    $state=ReadJson $StatePath
    $installedVersion=if($state){[string]$state.installedVersion}else{''}
    $oldManifest=ReadJson $ManifestPath

    if($installedVersion -eq $remoteVersion -and $oldManifest -and $oldManifest.files){
        Log "HVMC content is al bijgewerkt naar $remoteVersion; verschilcontrole overgeslagen."
        Log "HVMC content + runtime synchronisatie voltooid."
        exit 0
    }

    $remoteFiles=@(Get-RemoteFiles)
    $oldEntries=@{}
    if($oldManifest -and $oldManifest.files){foreach($entry in @($oldManifest.files)){$oldEntries[[string]$entry.path]=[string]$entry.sha}}
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

    # Final verification after content synchronization as well.
    Ensure-Fabric
    Log "HVMC content + Minecraft $McVersion + Fabric $FabricLoader synchronisatie voltooid."
    exit 0
} catch {
    Log "Updater mislukt: $($_.Exception.Message)"
    exit 1
} finally {Log 'HVMC updater afgerond.'}