$ErrorActionPreference = 'Stop'

$Repo = 'Bendemen-Studios/HVMC'
$Branch = 'main'
$MinecraftDir = Join-Path $env:APPDATA '.minecraft'
$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$Instance = $MinecraftDir
$StatePath = Join-Path $Root 'state.json'
$LogPath = Join-Path $Root 'bootstrapper.log'
$PackPath = Join-Path $Root 'HV.mrpack'
$PoolConfigPath = Join-Path $Root 'pool.json'
$OAuthConfigPath = Join-Path $Root 'oauth.json'
$OAuthProfilePath = Join-Path $Root 'oauth-profile.json'
$ManagedPath = Join-Path $Root 'managed-files.json'
$McVersion = '1.21.11'
$FabricLoader = '0.18.1'

foreach ($dir in @($Root,$MinecraftDir,$MinecraftDir+'\mods',$MinecraftDir+'\config',$MinecraftDir+'\resourcepacks',$MinecraftDir+'\shaderpacks',$MinecraftDir+'\datapacks',$MinecraftDir+'\kubejs')) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }

function Write-Log([string]$Message) { try { Add-Content -LiteralPath $LogPath -Value ("{0:u} {1}" -f (Get-Date),$Message) -Encoding UTF8 } catch {} }
function Get-GitHubFile([string]$Name) { Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/contents/$Name?ref=$Branch" -Headers @{'User-Agent'='HVMC-Bootstrapper'} -TimeoutSec 20 }
function Download-File([string]$Url,[string]$Destination,[string]$ExpectedSha512='',[string]$ExpectedSha1='') {
    $parent=Split-Path -Parent $Destination; New-Item -ItemType Directory -Force -Path $parent | Out-Null; $tmp="$Destination.download"
    try {
        Invoke-WebRequest -Uri $Url -OutFile $tmp -Headers @{'User-Agent'='HVMC-Bootstrapper'} -UseBasicParsing -TimeoutSec 180
        if (-not (Test-Path $tmp)) { throw 'Download did not create a file.' }
        if ($ExpectedSha512) { if ((Get-FileHash $tmp -Algorithm SHA512).Hash.ToLowerInvariant() -ne $ExpectedSha512.ToLowerInvariant()) { throw 'SHA-512 verification failed.' } }
        elseif ($ExpectedSha1) { if ((Get-FileHash $tmp -Algorithm SHA1).Hash.ToLowerInvariant() -ne $ExpectedSha1.ToLowerInvariant()) { throw 'SHA-1 verification failed.' } }
        Move-Item $tmp $Destination -Force; return $true
    } catch { Remove-Item $tmp -Force -ErrorAction SilentlyContinue; Write-Log "Download failed: $Url :: $($_.Exception.Message)"; return $false }
}
function Read-JsonFile([string]$Path) { if (Test-Path $Path) { try { return Get-Content $Path -Raw | ConvertFrom-Json } catch {} }; return $null }
function Save-JsonFile($Object,[string]$Path) { $Object | ConvertTo-Json -Depth 20 | Set-Content $Path -Encoding UTF8 }
function Safe-Relative([string]$Relative) { $p=$Relative.Replace('/','\'); if ([IO.Path]::IsPathRooted($p) -or $p.Contains('..')) { throw "Unsafe path: $p" }; return $p }
function Sync-PackEntry($Entry) {
    $relative=Safe-Relative ([string]$Entry.path); $destination=Join-Path $Instance $relative; $sha512='';$sha1=''
    if ($Entry.hashes) { if ($Entry.hashes.sha512){$sha512=[string]$Entry.hashes.sha512};if($Entry.hashes.sha1){$sha1=[string]$Entry.hashes.sha1} }
    $valid=$false
    if(Test-Path $destination){try{if($sha512){$valid=((Get-FileHash $destination -Algorithm SHA512).Hash -eq $sha512)}elseif($sha1){$valid=((Get-FileHash $destination -Algorithm SHA1).Hash -eq $sha1)}}catch{}}
    if($valid){return 'unchanged'}
    foreach($url in @($Entry.downloads)){if(Download-File ([string]$url) $destination $sha512 $sha1){return 'updated'}}
    throw "Unable to download $relative"
}
function Remove-ManagedFiles([string[]]$OldFiles,[string[]]$NewFiles) {
    $set=@{};foreach($f in $NewFiles){$set[$f.ToLowerInvariant()]=$true}
    foreach($f in $OldFiles){if(-not $set.ContainsKey($f.ToLowerInvariant())){$path=Join-Path $Instance (Safe-Relative $f);if(Test-Path $path){try{Remove-Item $path -Force;Write-Log "Removed obsolete managed file: $f"}catch{Write-Log "Could not remove $f: $($_.Exception.Message)"}}}}
}
function Sync-Mrpack([string]$Mrpack) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip=[IO.Compression.ZipFile]::OpenRead($Mrpack);$newManaged=New-Object 'System.Collections.Generic.List[string]'
    try {
        $indexEntry=$zip.GetEntry('modrinth.index.json');if(-not $indexEntry){throw 'HV.mrpack does not contain modrinth.index.json.'}
        $tempIndex=Join-Path $Root ('index-'+[Guid]::NewGuid().ToString('N')+'.json');[IO.Compression.ZipFileExtensions]::ExtractToFile($indexEntry,$tempIndex,$true);$index=Read-JsonFile $tempIndex;Remove-Item $tempIndex -Force
        if(-not $index){throw 'Could not read modrinth.index.json.'}
        foreach($entry in @($index.files)){$result=Sync-PackEntry $entry;$relative=Safe-Relative ([string]$entry.path);$newManaged.Add($relative);Write-Log "$result: $relative"}
        foreach($overrideRoot in @('overrides','client-overrides')){
            foreach($entry in $zip.Entries){$prefix=$overrideRoot+'/';if(-not $entry.FullName.StartsWith($prefix)-or $entry.FullName.EndsWith('/')){continue};$relative=Safe-Relative $entry.FullName.Substring($prefix.Length);$destination=Join-Path $Instance $relative;$parent=Split-Path -Parent $destination;New-Item -ItemType Directory -Force -Path $parent|Out-Null;$stream=$entry.Open();$tmp="$destination.override";$out=[IO.File]::Open($tmp,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None);try{$stream.CopyTo($out)}finally{$out.Dispose();$stream.Dispose()};$same=$false;if(Test-Path $destination){try{$same=((Get-FileHash $destination -Algorithm SHA512).Hash -eq (Get-FileHash $tmp -Algorithm SHA512).Hash)}catch{}};if($same){Remove-Item $tmp -Force}else{Move-Item $tmp $destination -Force};$newManaged.Add($relative)}
        }
        $old=Read-JsonFile $ManagedPath;$oldFiles=if($old -and $old.files){@($old.files)}else{@()};Remove-ManagedFiles $oldFiles @($newManaged);Save-JsonFile ([pscustomobject]@{version=1;files=@($newManaged|Sort-Object -Unique);packVersion=[string]$index.versionId;synced=(Get-Date).ToUniversalTime().ToString('o')}) $ManagedPath;return $index
    }finally{$zip.Dispose()}
}
function Sync-CustomTree([string]$RemotePath='content',[string]$LocalRoot=$Instance) {
    try{$items=Get-GitHubFile $RemotePath}catch{return}
    foreach($item in @($items)){
        if($item.type -eq 'dir'){Sync-CustomTree ([string]$item.path) $LocalRoot;continue}
        if($item.type -ne 'file'){continue}
        $prefix='content/';$relative=Safe-Relative ([string]$item.path).Substring($prefix.Length);$destination=Join-Path $LocalRoot $relative
        $remoteSize=[int64]$item.size;$sameSize=(Test-Path $destination -and (Get-Item $destination).Length -eq $remoteSize)
        if(-not $sameSize){if(Download-File ([string]$item.download_url) $destination){Write-Log "Custom content updated: $relative"}}
    }
}
function Test-OfficialLauncher {
    $paths=@((Join-Path ${env:ProgramFiles(x86)} 'Minecraft Launcher\MinecraftLauncher.exe'),(Join-Path $env:ProgramFiles 'Minecraft Launcher\MinecraftLauncher.exe'),(Join-Path $env:LOCALAPPDATA 'Programs\Minecraft Launcher\MinecraftLauncher.exe'))
    foreach($p in $paths){if($p -and (Test-Path $p)){return $p}};return $null
}
function Ensure-OfficialLauncher {
    $launcher=Test-OfficialLauncher;if($launcher){return $launcher}
    $winget=Get-Command winget.exe -ErrorAction SilentlyContinue
    if($winget){try{& $winget.Source install --id Mojang.MinecraftLauncher --exact --silent --accept-package-agreements --accept-source-agreements | Out-Null}catch{Write-Log "WinGet launcher install failed: $($_.Exception.Message)"};$launcher=Test-OfficialLauncher;if($launcher){return $launcher}}
    Start-Process 'https://www.minecraft.net/download';throw 'Official Minecraft Launcher is not installed. The official download page has been opened.'
}
function Ensure-Fabric {
    $fabricVersion=Join-Path $MinecraftDir "versions\fabric-loader-$FabricLoader-$McVersion\fabric-loader-$FabricLoader-$McVersion.json";if(Test-Path $fabricVersion){return}
    $meta=Invoke-RestMethod -Uri 'https://meta.fabricmc.net/v2/versions/installer' -Headers @{'User-Agent'='HVMC-Bootstrapper'} -TimeoutSec 30;$installer=$meta|Where-Object{$_.stable -eq $true -and $_.exe}|Select-Object -First 1
    if(-not $installer){throw 'Could not find a stable Fabric Windows installer.'};$installerPath=Join-Path $Root 'fabric-installer.exe';if(-not(Download-File ([string]$installer.exe) $installerPath)){throw 'Could not download Fabric installer.'}
    $p=Start-Process $installerPath -ArgumentList 'client','-dir',('"'+$MinecraftDir+'"'),'-mcversion',$McVersion,'-loader',$FabricLoader -Wait -PassThru
    if($p.ExitCode -ne 0 -or -not(Test-Path $fabricVersion)){throw 'Fabric installation failed.'}
}
function Get-PoolConfig {
    $cfg=Read-JsonFile $PoolConfigPath
    if(-not $cfg){$cfg=[pscustomobject]@{enabled=$false;url='https://accounts.hvmc.nl';autoSelect=$true;requireOAuth=$true;leaseSeconds=3600};Save-JsonFile $cfg $PoolConfigPath}
    return $cfg
}
function Ensure-OAuthProfile {
    $cfg=Get-PoolConfig;if(-not $cfg.enabled -or -not $cfg.requireOAuth){return $null}
    if(Test-Path $OAuthProfilePath){return Read-JsonFile $OAuthProfilePath}
    $oauthScript=Join-Path $PSScriptRoot 'HVMCOAuth.ps1';if(-not(Test-Path $oauthScript)){throw 'HVMCOAuth.ps1 is missing.'}
    if(-not(Test-Path $OAuthConfigPath)){$example=Join-Path $PSScriptRoot 'oauth-config.example.json';if(-not(Test-Path $example)){throw 'oauth-config.example.json is missing.'};Copy-Item $example $OAuthConfigPath -Force;throw "OAuth setup required. Edit $OAuthConfigPath with your Microsoft Application (client) ID and start again."}
    $result=& powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File $oauthScript 2>&1
    if($LASTEXITCODE -ne 0){throw (($result|Out-String).Trim())}
    return ($result|Out-String|ConvertFrom-Json)
}
function Acquire-PoolLease($Profile) {
    $cfg=Get-PoolConfig;if(-not $cfg.enabled -or [string]::IsNullOrWhiteSpace([string]$cfg.url)){return $null}
    try{$body=@{clientId="$env:COMPUTERNAME-$env:USERNAME";accountId=if($Profile){[string]$Profile.accountId}else{''};product='HVMC';minecraftVersion=$McVersion}|ConvertTo-Json;return Invoke-RestMethod -Uri ([string]$cfg.url).TrimEnd('/')+'/v1/lease/acquire' -Method Post -ContentType 'application/json' -Body $body -TimeoutSec 10}catch{Write-Log "Account pool unavailable: $($_.Exception.Message)";return $null}
}

Write-Log 'Bootstrapper started.'
try {
    try{$remotePack=Get-GitHubFile 'HV.mrpack';$state=Read-JsonFile $StatePath;if(-not(Test-Path $PackPath)-or -not $state -or [string]$state.packSha -ne [string]$remotePack.sha){if(-not(Download-File $remotePack.download_url $PackPath)){throw 'Could not download HV.mrpack.'};Save-JsonFile ([pscustomobject]@{packSha=[string]$remotePack.sha}) $StatePath};$index=Sync-Mrpack $PackPath;Write-Log "Pack synchronized: $($index.name) $($index.versionId)"}catch{Write-Log "Pack synchronization failed: $($_.Exception.Message)"}
    try{Sync-CustomTree}catch{Write-Log "Custom content sync failed: $($_.Exception.Message)"}
    $launcher=Ensure-OfficialLauncher
    Ensure-Fabric
    $profile=Ensure-OAuthProfile
    $lease=Acquire-PoolLease $profile
    if($lease -and $lease.leaseId){Save-JsonFile ([pscustomobject]@{leaseId=[string]$lease.leaseId;accountName=[string]$lease.accountName;acquired=(Get-Date).ToUniversalTime().ToString('o')}) (Join-Path $Root 'lease.json');Write-Log "Account pool lease acquired: $($lease.accountName)"}
    Write-Log 'Starting official Minecraft Launcher.'
    Start-Process $launcher
    exit 0
}catch{Write-Log "Bootstrapper failed: $($_.Exception.Message)";exit 1}
finally{Write-Log 'Bootstrapper finished.'}
