$ErrorActionPreference = 'Stop'

$Repo = 'Bendemen-Studios/HVMC'
$Branch = 'main'
$MinecraftDir = Join-Path $env:APPDATA '.minecraft'
$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$ManifestPath = Join-Path $Root 'content-manifest.json'
$StatePath = Join-Path $Root 'state.json'
$LogPath = Join-Path $Root 'bootstrapper.log'
$McVersion = '26.2'
$FabricLoader = '0.19.3'
$FabricProfile = "fabric-loader-$FabricLoader-$McVersion"
$fabricJson = Join-Path $MinecraftDir "versions\$FabricProfile\$FabricProfile.json"

foreach ($dir in @($Root, $MinecraftDir, $MinecraftDir+'\mods', $MinecraftDir+'\config', $MinecraftDir+'\resourcepacks', $MinecraftDir+'\shaderpacks', $MinecraftDir+'\datapacks', $MinecraftDir+'\kubejs', (Join-Path $MinecraftDir 'versions'))) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

function Log([string]$Message) { Write-Host "[HVMC] $Message"; try { Add-Content -LiteralPath $LogPath -Value (("{0:u} {1}" -f (Get-Date),$Message)) -Encoding UTF8 } catch {} }
function Get-GitHubHeaders { @{ 'User-Agent' = 'HVMC-School-Launcher'; 'Accept' = 'application/vnd.github+json' } }
function ReadJson([string]$Path) { if (-not (Test-Path -LiteralPath $Path)) { return $null }; try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null } }
function SaveJson($Value,[string]$Path) { $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8 }
function Safe([string]$Path) { $p=$Path.Replace('/','\'); if ([IO.Path]::IsPathRooted($p) -or $p.Contains('..')) { throw "Unsafe path: $p" }; return $p }
function Download([string]$Url,[string]$Destination) { $parent=Split-Path -Parent $Destination; New-Item -ItemType Directory -Force -Path $parent | Out-Null; $tmp="$Destination.download"; try { Invoke-WebRequest -Uri $Url -OutFile $tmp -Headers (Get-GitHubHeaders) -UseBasicParsing -TimeoutSec 180; Move-Item -LiteralPath $tmp -Destination $Destination -Force } catch { Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue; throw "Download failed for \${Destination}: $($_.Exception.Message)" } }

function DownloadBatch($Files,[int]$BatchSize=6) {
    if($Files.Count -eq 0){ return }
    $client=New-Object System.Net.Http.HttpClient
    $client.DefaultRequestHeaders.UserAgent.ParseAdd('HVMC-School-Launcher')
    try {
        for($start=0; $start -lt $Files.Count; $start += $BatchSize){
            $end=[Math]::Min($start+$BatchSize-1,$Files.Count-1)
            $batch=@()
            for($i=$start; $i -le $end; $i++){ $batch += $Files[$i] }
            Log "Parallel downloaden: $($batch.Count) bestanden tegelijk."

            $responseTasks=@()
            foreach($file in $batch){
                $responseTasks += $client.GetAsync([string]$file.download,[System.Net.Http.HttpCompletionOption]::ResponseHeadersRead)
            }
            [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$responseTasks)

            $copyTasks=@()
            $handles=@()
            try {
                for($i=0; $i -lt $batch.Count; $i++){
                    $response=$responseTasks[$i].Result
                    if(-not $response.IsSuccessStatusCode){ throw "Download failed for $($batch[$i].path): HTTP $([int]$response.StatusCode) $($response.StatusCode)" }
                    $destination=[string]$batch[$i].destination
                    $tmp="$destination.download"
                    $parent=Split-Path -Parent $destination
                    New-Item -ItemType Directory -Force -Path $parent | Out-Null
                    Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
                    $stream=[IO.File]::Create($tmp)
                    $handles += [pscustomobject]@{Stream=$stream;Temp=$tmp;Destination=$destination;Path=[string]$batch[$i].relative}
                    $copyTasks += $response.Content.CopyToAsync($stream)
                }
                [System.Threading.Tasks.Task]::WaitAll([System.Threading.Tasks.Task[]]$copyTasks)
                foreach($handle in $handles){
                    $handle.Stream.Dispose()
                    Move-Item -LiteralPath $handle.Temp -Destination $handle.Destination -Force
                    Log "Updated: $($handle.Path)"
                }
            } finally {
                foreach($handle in $handles){
                    try { $handle.Stream.Dispose() } catch {}
                    if(Test-Path -LiteralPath $handle.Temp){ Remove-Item -LiteralPath $handle.Temp -Force -ErrorAction SilentlyContinue }
                }
                foreach($task in $responseTasks){
                    try { if($task.IsCompleted){ $task.Result.Dispose() } } catch {}
                }
            }
        }
    } finally { $client.Dispose() }
}
function Test-GitBlobSha([string]$FilePath,[string]$ExpectedSha) { if (-not (Test-Path -LiteralPath $FilePath)) { return $false }; try { $bytes=[IO.File]::ReadAllBytes($FilePath); $header=[Text.Encoding]::ASCII.GetBytes("blob $($bytes.Length)`0"); $all=New-Object byte[] ($header.Length+$bytes.Length); [Array]::Copy($header,0,$all,0,$header.Length); [Array]::Copy($bytes,0,$all,$header.Length,$bytes.Length); $hash=[Security.Cryptography.SHA1]::HashData($all); $hex=-join ($hash | ForEach-Object { $_.ToString('x2') }); return $hex -ieq $ExpectedSha } catch { return $false } }
function Get-RemoteContentIndex {
    $headers=Get-GitHubHeaders
    $ref=Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/git/ref/heads/$Branch" -Headers $headers -TimeoutSec 30
    $treeSha=[string]$ref.object.sha
    $tree=Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/git/trees/$treeSha`?recursive=1" -Headers $headers -TimeoutSec 30
    $files=@($tree.tree | Where-Object { $_.type -eq 'blob' -and $_.path -like 'content/*' } | ForEach-Object {
        [pscustomobject]@{path=[string]$_.path;sha=[string]$_.sha;download="https://raw.githubusercontent.com/$Repo/$Branch/$($_.path)"}
    })
    return [pscustomobject]@{TreeSha=$treeSha;Files=$files}
}

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

    # First compare the Git tree SHA. If the remote content tree has not changed
    # since the last successful sync, skip the expensive recursive tree/hash pass.
    $remoteIndex=Get-RemoteContentIndex
    $remoteTreeSha=[string]$remoteIndex.TreeSha
    $remoteFiles=@($remoteIndex.Files)
    if($remoteFiles.Count -eq 0){throw 'Geen HVMC content gevonden in content/. Upload de volledige Fabric-runtime onder content/ voordat deze launcher wordt gebruikt.'}

    $missingLocal=@($oldEntries.Keys | Where-Object {
        -not (Test-Path -LiteralPath (Join-Path $MinecraftDir (Safe $_)))
    })
    $fastPath=(
        -not [string]::IsNullOrWhiteSpace([string]$state.remoteTreeSha) -and
        [string]$state.remoteTreeSha -eq $remoteTreeSha -and
        $oldEntries.Count -gt 0 -and
        $missingLocal.Count -eq 0 -and
        (Test-Path -LiteralPath $fabricJson)
    )

    if($fastPath){
        Log "Geen contentwijzigingen gevonden. Snelle sync: bestaande HVMC-content wordt hergebruikt."
        SaveJson ([pscustomobject]@{
            installedVersion=$remoteVersion
            remoteTreeSha=$remoteTreeSha
            updated=(Get-Date).ToUniversalTime().ToString('o')
        }) $StatePath
        Log "HVMC content synchronisatie overgeslagen; alles is al actueel."
        exit 0
    }

    Log "Nieuwe of gewijzigde HVMC-content gevonden. Bestanden controleren..."
    $newManifest=@{}
    $downloadQueue=@()

    foreach($file in $remoteFiles){
        $relative=Safe ([string]$file.path).Substring(8)
        $destination=Join-Path $MinecraftDir $relative
        $expectedSha=[string]$file.sha

        # If our previous manifest already knows this exact Git blob and the file exists,
        # trust the manifest instead of re-reading and hashing the whole file.
        # The remote tree SHA already proved that this content has not changed upstream.
        $manifestMatch=($oldEntries.ContainsKey($relative) -and [string]$oldEntries[$relative] -eq $expectedSha)
        if(-not(Test-Path -LiteralPath $destination)){
            $downloadQueue += [pscustomobject]@{path=[string]$file.path;relative=$relative;destination=$destination;download=[string]$file.download}
        } elseif(-not $manifestMatch -and -not(Test-GitBlobSha $destination $expectedSha)){
            $downloadQueue += [pscustomobject]@{path=[string]$file.path;relative=$relative;destination=$destination;download=[string]$file.download}
        }
        $newManifest[$relative]=$expectedSha
    }

    if($downloadQueue.Count -gt 0){
        Log "Te downloaden bestanden: $($downloadQueue.Count). Parallelle downloads worden gebruikt."
        DownloadBatch $downloadQueue 6
    } else {
        Log "Alle bestaande bestanden komen overeen met de HVMC-manifestgegevens."
    }

    foreach($oldPath in @($oldEntries.Keys)){if(-not $newManifest.ContainsKey($oldPath)){$obsolete=Join-Path $MinecraftDir (Safe $oldPath);if(Test-Path -LiteralPath $obsolete){Remove-Item -LiteralPath $obsolete -Force}}}
    $manifestFiles=foreach($key in ($newManifest.Keys|Sort-Object)){[pscustomobject]@{path=$key;sha=$newManifest[$key]}}
    SaveJson ([pscustomobject]@{version=$remoteVersion;files=@($manifestFiles);updated=(Get-Date).ToUniversalTime().ToString('o')}) $ManifestPath
    SaveJson ([pscustomobject]@{installedVersion=$remoteVersion;remoteTreeSha=$remoteTreeSha;updated=(Get-Date).ToUniversalTime().ToString('o')}) $StatePath

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
