$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$MinecraftDir = Join-Path $env:APPDATA '.minecraft'
$ManifestPath = Join-Path $Root 'content-manifest.json'
$StatePath = Join-Path $Root 'state.json'
$LogPath = Join-Path $Root 'bootstrapper.log'
$McVersion = '1.21.11'
$FabricLoader = '0.19.2'

foreach ($dir in @($Root,$MinecraftDir,$MinecraftDir+'\mods',$MinecraftDir+'\config',$MinecraftDir+'\resourcepacks',$MinecraftDir+'\shaderpacks',$MinecraftDir+'\datapacks',$MinecraftDir+'\kubejs')) { New-Item -ItemType Directory -Force -Path $dir | Out-Null }
