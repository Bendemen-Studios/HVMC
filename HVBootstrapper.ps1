$ErrorActionPreference = 'Stop'

$Repo = 'Bendemen-Studios/HVMC'
$Branch = 'main'
$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$Instance = Join-Path $Root 'instance'
$Mods = Join-Path $Instance 'mods'
$Config = Join-Path $Instance 'config'
$ContentRoot = Join-Path $Root 'content'
$StatePath = Join-Path $Root 'state.json'
$LogPath = Join-Path $Root 'bootstrapper.log'
$PackPath = Join-Path $Root 'HV.mrpack'
$ShortcutPath = Join-Path $Root 'HV.lnk'
$ManagedPath = Join-Path $Root 'managed-files.json'

foreach ($dir in @($Root,$Instance,$Mods,$Config,(Join-Path $Instance 'resourcepacks'),(Join-Path $Instance 'shaderpacks'),(Join-Path $Instance 'datapacks'),(Join-Path $Instance 'kubejs'))) {
    New-Item -ItemType Directory -Force -Path $dir | Out-Null
}

function Write-Log([string]$Message) {
    try { Add-Content -LiteralPath $LogPath -Value ("{0:u} {1}" -f (Get-Date), $Message) -Encoding UTF8 } catch {}
}

function Get-GitHubFile([string]$Name) {
    $api = "https://api.github.com/repos/$Repo/contents/$Name?ref=$Branch"
    Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent' = 'HVMC-Bootstrapper' } -Method Get -TimeoutSec 20
}

function Download-File([string]$Url,[string]$Destination,[string]$ExpectedSha512='',[string]$ExpectedSha1='') {
    $parent = Split-Path -Parent $Destination
    New-Item -ItemType Directory -Force -Path $parent | Out-Null
    $tmp = "$Destination.download"
    try {
        Invoke-WebRequest -Uri $Url -OutFile $tmp -Headers @{ 'User-Agent' = 'HVMC-Bootstrapper' } -UseBasicParsing -TimeoutSec 180
        if (-not (Test-Path -LiteralPath $tmp)) { throw 'Download did not create a file.' }
        if ($ExpectedSha512) {
            $actual = (Get-FileHash -LiteralPath $tmp -Algorithm SHA512).Hash.ToLowerInvariant()
            if ($actual -ne $ExpectedSha512.ToLowerInvariant()) { throw 'SHA-512 verification failed.' }
        } elseif ($ExpectedSha1) {
            $actual = (Get-FileHash -LiteralPath $tmp -Algorithm SHA1).Hash.ToLowerInvariant()
            if ($actual -ne $ExpectedSha1.ToLowerInvariant()) { throw 'SHA-1 verification failed.' }
        }
        Move-Item -LiteralPath $tmp -Destination $Destination -Force
        return $true
    } catch {
        Remove-Item -LiteralPath $tmp -Force -ErrorAction SilentlyContinue
        Write-Log "Download failed: $Url :: $($_.Exception.Message)"
        return $false
    }
}

function Read-JsonFile([string]$Path) {
    if (Test-Path -LiteralPath $Path) { try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch {} }
    return $null
}

function Save-JsonFile($Object,[string]$Path) {
    $Object | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Get-RelativePath([string]$Base,[string]$Full) {
    $baseUri = [Uri]((Resolve-Path -LiteralPath $Base).Path.TrimEnd('\') + '\')
    $fullUri = [Uri]((Resolve-Path -LiteralPath $Full).Path)
    [Uri]::UnescapeDataString($baseUri.MakeRelativeUri($fullUri).ToString()).Replace('/','\')
}

function Sync-FileEntry($Entry) {
    $relative = ([string]$Entry.path).Replace('/','\')
    if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('..')) { throw "Unsafe pack path: $relative" }
    $destination = Join-Path $Instance $relative
    $sha512 = ''
    $sha1 = ''
    if ($Entry.hashes) {
        if ($Entry.hashes.sha512) { $sha512 = [string]$Entry.hashes.sha512 }
        if ($Entry.hashes.sha1) { $sha1 = [string]$Entry.hashes.sha1 }
    }
    $valid = $false
    if (Test-Path -LiteralPath $destination) {
        try {
            if ($sha512) { $valid = ((Get-FileHash -LiteralPath $destination -Algorithm SHA512).Hash -eq $sha512) }
            elseif ($sha1) { $valid = ((Get-FileHash -LiteralPath $destination -Algorithm SHA1).Hash -eq $sha1) }
        } catch {}
    }
    if ($valid) { return 'unchanged' }
    if (-not $Entry.downloads -or $Entry.downloads.Count -eq 0) { throw "No download URL for $relative" }
    foreach ($url in @($Entry.downloads)) {
        if (Download-File ([string]$url) $destination $sha512 $sha1) { return 'updated' }
    }
    throw "Unable to download $relative"
}

function Remove-ManagedFiles([string[]]$OldFiles,[string[]]$NewFiles) {
    $newSet = @{}
    foreach ($f in $NewFiles) { $newSet[$f.ToLowerInvariant()] = $true }
    foreach ($f in $OldFiles) {
        if (-not $newSet.ContainsKey($f.ToLowerInvariant())) {
            $path = Join-Path $Instance ($f.Replace('/','\'))
            if (Test-Path -LiteralPath $path) {
                try { Remove-Item -LiteralPath $path -Force; Write-Log "Removed obsolete managed file: $f" } catch { Write-Log "Could not remove $f: $($_.Exception.Message)" }
            }
        }
    }
}

function Extract-And-SyncPack([string]$Mrpack) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = [IO.Compression.ZipFile]::OpenRead($Mrpack)
    $temp = Join-Path $Root ('pack-' + [Guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Force -Path $temp | Out-Null
    try {
        $indexEntry = $zip.GetEntry('modrinth.index.json')
        if (-not $indexEntry) { throw 'HV.mrpack does not contain modrinth.index.json.' }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($indexEntry,(Join-Path $temp 'modrinth.index.json'),$true)
        $index = Read-JsonFile (Join-Path $temp 'modrinth.index.json')
        if (-not $index) { throw 'Could not read modrinth.index.json.' }

        $newManaged = New-Object System.Collections.Generic.List[string]
        foreach ($entry in @($index.files)) {
            $result = Sync-FileEntry $entry
            $relative = ([string]$entry.path).Replace('/','\')
            $newManaged.Add($relative)
            Write-Log "$result: $relative"
        }

        # Apply mrpack overrides. These are the right place for pack-owned configs,
        # scripts, resource packs, etc. They are copied only when their contents change.
        foreach ($overrideRoot in @('overrides','client-overrides')) {
            foreach ($entry in $zip.Entries) {
                $prefix = $overrideRoot + '/'
                if (-not $entry.FullName.StartsWith($prefix) -or $entry.FullName.EndsWith('/')) { continue }
                $relative = $entry.FullName.Substring($prefix.Length).Replace('/','\')
                if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('..')) { throw "Unsafe override path: $relative" }
                $destination = Join-Path $Instance $relative
                $parent = Split-Path -Parent $destination
                New-Item -ItemType Directory -Force -Path $parent | Out-Null
                $stream = $entry.Open()
                $tmp = "$destination.override"
                $out = [IO.File]::Open($tmp,[IO.FileMode]::Create,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try { $stream.CopyTo($out) } finally { $out.Dispose(); $stream.Dispose() }
                $same = $false
                if (Test-Path -LiteralPath $destination) {
                    try { $same = ((Get-FileHash $destination -Algorithm SHA512).Hash -eq (Get-FileHash $tmp -Algorithm SHA512).Hash) } catch {}
                }
                if ($same) { Remove-Item $tmp -Force } else { Move-Item $tmp $destination -Force }
                $newManaged.Add($relative)
            }
        }

        $old = Read-JsonFile $ManagedPath
        $oldFiles = if ($old -and $old.files) { @($old.files) } else { @() }
        Remove-ManagedFiles $oldFiles @($newManaged)
        Save-JsonFile ([pscustomobject]@{ version=1; files=@($newManaged | Sort-Object -Unique); packVersion=[string]$index.versionId; synced=(Get-Date).ToUniversalTime().ToString('o') }) $ManagedPath
        return $index
    } finally {
        $zip.Dispose()
        Remove-Item -LiteralPath $temp -Recurse -Force -ErrorAction SilentlyContinue
    }
}

function Sync-CustomRepositoryContent {
    # Optional operator-managed files live in content/. Recommended layout:
    # content/mods, content/config, content/resourcepacks, content/shaderpacks, etc.
    $api = "https://api.github.com/repos/$Repo/contents/content?ref=$Branch"
    try {
        $items = Invoke-RestMethod -Uri $api -Headers @{ 'User-Agent'='HVMC-Bootstrapper' } -TimeoutSec 20
    } catch { return }
    if (-not $items) { return }
    foreach ($item in @($items)) {
        if ($item.type -ne 'file') { continue }
        $relative = ([string]$item.path).Substring('content/'.Length).Replace('/','\')
        if ([IO.Path]::IsPathRooted($relative) -or $relative.Contains('..')) { continue }
        $dest = Join-Path $Instance $relative
        if (-not (Test-Path -LiteralPath $dest) -or (Get-FileHash $dest -Algorithm SHA256).Hash -ne (Invoke-RestMethod -Uri $item.download_url -Method Get -Headers @{ 'User-Agent'='HVMC-Bootstrapper' } -TimeoutSec 20 | Out-String)) {
            # Content API listing is intentionally only for discovering small custom files.
            Download-File $item.download_url $dest
        }
    }
}

Write-Log 'Bootstrapper started.'
$packReady = $false
try {
    $remotePack = Get-GitHubFile 'HV.mrpack'
    $state = Read-JsonFile $StatePath
    if (-not (Test-Path -LiteralPath $PackPath) -or -not $state -or [string]$state.packSha -ne [string]$remotePack.sha) {
        if (Download-File $remotePack.download_url $PackPath) {
            Save-JsonFile ([pscustomobject]@{packSha=[string]$remotePack.sha}) $StatePath
        } else { throw 'Could not download HV.mrpack.' }
    }
    $index = Extract-And-SyncPack $PackPath
    $packReady = $true
    Write-Log "Pack synchronized: $($index.name) $($index.versionId)"
} catch { Write-Log "Pack synchronization failed: $($_.Exception.Message)" }

try { Sync-CustomRepositoryContent } catch { Write-Log "Custom content sync failed: $($_.Exception.Message)" }

# Keep the legacy shortcut as a fallback for authentication/launch until a
# launcher-owned Microsoft session is available. No visible bootstrapper window.
if (Test-Path -LiteralPath $ShortcutPath) {
    try {
        $shell = New-Object -ComObject WScript.Shell
        $shell.Run('"' + $ShortcutPath + '"',0,$false)
        Write-Log 'HV.lnk launched after synchronization.'
        exit 0
    } catch { Write-Log "Shortcut launch failed: $($_.Exception.Message)" }
}

try {
    $remoteShortcut = Get-GitHubFile 'HV.lnk'
    if (Download-File $remoteShortcut.download_url $ShortcutPath) {
        $shell = New-Object -ComObject WScript.Shell
        $shell.Run('"' + $ShortcutPath + '"',0,$false)
        Write-Log 'HV.lnk downloaded and launched.'
        exit 0
    }
} catch { Write-Log "Fallback launch failed: $($_.Exception.Message)" }

Write-Log 'Synchronization completed but no launcher shortcut is available.'
exit 1
