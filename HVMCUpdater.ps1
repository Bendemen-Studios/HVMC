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

foreach ($dir in @($Root,$MinecraftDir,$MinecraftDir+'\mods',$MinecraftDir+'\config',$MinecraftDir+'\resourcepacks',$MinecraftDir+'\shaderpacks',$MinecraftDir+'\datapacks',$MinecraftDir+'\kubejs')) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

function Log([string]$Message) {
    Write-Host "[HVMC] $Message"
    try { Add-Content -LiteralPath $LogPath -Value (("{0:u} {1}" -f (Get-Date),$Message)) -Encoding UTF8 } catch {}
}

function Get-GitHubHeaders {
    @{ 'User-Agent' = 'HVMC-School-Launcher'; 'Accept' = 'application/vnd.github+json' }
}

function Get-RemoteFiles {
    $headers = Get-GitHubHeaders
    try {
        $refUri = "https://api.github.com/repos/$Repo/git/ref/heads/$Branch"
        $ref = Invoke-RestMethod -Uri $refUri -Headers $headers -TimeoutSec 30
        $treeSha = [string]$ref.object.sha
        if ([string]::IsNullOrWhiteSpace($treeSha)) { throw 'GitHub branch SHA ontbreekt.' }

        $treeUri = "https://api.github.com/repos/$Repo/git/trees/$treeSha`?recursive=1"
        $tree = Invoke-RestMethod -Uri $treeUri -Headers $headers -TimeoutSec 30
    }
    catch {
        throw "GitHub content index ophalen mislukt: $($_.Exception.Message)"
    }

    if (-not $tree.tree) { return @() }

    @($tree.tree | Where-Object {
        $_.type -eq 'blob' -and $_.path -like 'content/*'
    } | ForEach-Object {
        [pscustomobject]@{
            path = [string]$_.path
            sha = [string]$_.sha
            download = "https://raw.githubusercontent.com/$Repo/$Branch/$($_.path)"
        }
    })
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
        Invoke-WebRequest -Uri $Url -OutFile $tmp -Headers (Get-GitHubHeaders) -UseBasicParsing -TimeoutSec 180
        if (-not (Test-Path -LiteralPath $tmp)) { throw 'Download did not create a file.' }
        Move-Item -LiteralPath $tmp -Destination $Destination -Force
    }
    catch {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        throw "Download failed for ${Destination}: $($_.Exception.Message)"
    }
}

function ReadJson([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null }
}

function SaveJson($Value,[string]$Path) {
    $Value | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
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

function Find-Java {
    $runtimeRoot = Join-Path $MinecraftDir 'runtime'
    if (Test-Path -LiteralPath $runtimeRoot) {
        $runtimeJava = Get-ChildItem -LiteralPath $runtimeRoot -Filter 'java.exe' -File -Recurse -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($runtimeJava) { return $runtimeJava.FullName }
    }

    $javaCommand = Get-Command java.exe -ErrorAction SilentlyContinue
    if ($javaCommand) { return $javaCommand.Source }

    $common = @(
        (Join-Path ${env:ProgramFiles} 'Java\*\bin\java.exe'),
        (Join-Path ${env:ProgramFiles} 'Eclipse Adoptium\*\bin\java.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Java\*\bin\java.exe')
    )
    foreach ($pattern in $common) {
        $candidate = Get-ChildItem -Path $pattern -File -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($candidate) { return $candidate.FullName }
    }
    return $null
}

function Ensure-Fabric {
    $target = Join-Path $MinecraftDir "versions\fabric-loader-$FabricLoader-$McVersion\fabric-loader-$FabricLoader-$McVersion.json"
    if (Test-Path -LiteralPath $target) {
        Log "Fabric $FabricLoader voor Minecraft $McVersion gevonden."
        return
    }

    Log "Fabric $FabricLoader voor Minecraft $McVersion installeren..."
    try {
        $meta = Invoke-RestMethod -Uri 'https://meta.fabricmc.net/v2/versions/installer' -Headers @{ 'User-Agent'='HVMC-School-Launcher'; 'Accept'='application/json' } -TimeoutSec 30
        $installer = @($meta | Where-Object { $_.stable -eq $true -and $_.url }) | Select-Object -First 1
        if (-not $installer) { throw 'Geen stabiele Fabric installer gevonden.' }

        $javaPath = Find-Java
        if (-not $javaPath) {
            throw 'Java runtime niet gevonden. Minecraft moet eerst eenmaal door de launcher worden voorbereid.'
        }

        $installerVersion = [string]$installer.version
        $installerPath = Join-Path $Root "fabric-installer-$installerVersion.jar"
        Download ([string]$installer.url) $installerPath
        Log "Fabric installer $installerVersion gedownload als Universal JAR."

        $args = @('-jar', $installerPath, 'client', '-dir', $MinecraftDir, '-mcversion', $McVersion, '-loader', $FabricLoader, '-noprofile')
        $proc = Start-Process -FilePath $javaPath -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
        if ($proc.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $target)) {
            throw "Fabric CLI-installatie mislukt (exitcode $($proc.ExitCode))."
        }
        Log 'Fabric CLI-installatie voltooid.'
    }
    catch {
        throw "Fabric installeren mislukt: $($_.Exception.Message)"
    }
}

try {
    Log 'HVMC School Launcher updater gestart.'
    $versionResponse = Invoke-WebRequest -Uri "https://raw.githubusercontent.com/$Repo/$Branch/version.txt" -Headers @{ 'User-Agent'='HVMC-School-Launcher' } -UseBasicParsing -TimeoutSec 20
    $remoteVersion = ([string]$versionResponse.Content).Trim()
    if ([string]::IsNullOrWhiteSpace($remoteVersion)) { throw 'version.txt is leeg.' }
    Log "Beschikbare HVMC versie: $remoteVersion"

    $remoteFiles = @(Get-RemoteFiles)
    Log "Beheerde contentbestanden: $($remoteFiles.Count)"

    $oldManifest = ReadJson $ManifestPath
    $oldEntries = @{}
    if ($oldManifest -and $oldManifest.files) {
        foreach ($entry in @($oldManifest.files)) {
            $oldEntries[[string]$entry.path] = [string]$entry.sha
        }
    }

    $newManifest = @{}
    foreach ($file in $remoteFiles) {
        $relative = Safe ([string]$file.path).Substring(8)
        $destination = Join-Path $MinecraftDir $relative
        if (-not (Test-GitBlobSha $destination ([string]$file.sha))) {
            Download ([string]$file.download) $destination
            Log "Updated: $relative"
        }
        else {
            Log "Unchanged: $relative"
        }
        $newManifest[$relative] = [string]$file.sha
    }

    foreach ($oldPath in @($oldEntries.Keys)) {
        if (-not $newManifest.ContainsKey($oldPath)) {
            $obsolete = Join-Path $MinecraftDir (Safe $oldPath)
            if (Test-Path -LiteralPath $obsolete) {
                try {
                    Remove-Item -LiteralPath $obsolete -Force
                    Log "Removed obsolete: $oldPath"
                } catch {
                    Log "Could not remove obsolete ${oldPath}: $($_.Exception.Message)"
                }
            }
        }
    }

    $manifestFiles = foreach ($key in ($newManifest.Keys | Sort-Object)) {
        [pscustomobject]@{ path = $key; sha = $newManifest[$key] }
    }

    SaveJson ([pscustomobject]@{
        version = $remoteVersion
        files = @($manifestFiles)
        updated = (Get-Date).ToUniversalTime().ToString('o')
    }) $ManifestPath

    SaveJson ([pscustomobject]@{
        installedVersion = $remoteVersion
        updated = (Get-Date).ToUniversalTime().ToString('o')
    }) $StatePath

    Ensure-Fabric
    Log 'HVMC School Launcher update/installatie voltooid.'
    exit 0
}
catch {
    Log "Updater mislukt: $($_.Exception.Message)"
    exit 1
}
finally {
    Log 'HVMC updater afgerond.'
}