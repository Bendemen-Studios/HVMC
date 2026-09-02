$ErrorActionPreference = 'Stop'

$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$ConfigPath = Join-Path $Root 'oauth.json'
$RefreshTokenPath = Join-Path $Root 'oauth-refresh-token.txt'
$IdTokenPath = Join-Path $Root 'oauth-id-token.txt'
$ProfilePath = Join-Path $Root 'oauth-profile.json'
$DefaultClientId = '7fcdeaa7-ba20-4883-96b0-0b68cff24bb9'
$DefaultAuthority = 'https://login.microsoftonline.com/common'
$DefaultScopes = @('openid','profile','offline_access','https://graph.microsoft.com/User.Read')

New-Item -ItemType Directory -Force -Path $Root | Out-Null

function Read-Json([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null }
}
function Save-Json($Value,[string]$Path) { $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding UTF8 }
function Save-Secure([string]$Value,[string]$Path) { ConvertTo-SecureString $Value -AsPlainText -Force | ConvertFrom-SecureString | Set-Content -LiteralPath $Path -Encoding UTF8 }
function Read-Secure([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try {
        $secure = Get-Content -LiteralPath $Path -Raw | ConvertTo-SecureString
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
    } catch { return $null }
}
function Decode-JwtPayload([string]$Jwt) {
    try {
        $part = $Jwt.Split('.')[1].Replace('-','+').Replace('_','/')
        while ($part.Length % 4) { $part += '=' }
        [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($part)) | ConvertFrom-Json
    } catch { $null }
}

$config = Read-Json $ConfigPath
if (-not $config) {
    $config = [pscustomobject]@{
        clientId = $DefaultClientId
        authority = $DefaultAuthority
        scopes = $DefaultScopes
    }
    Save-Json $config $ConfigPath
}

$clientId = [string]$config.clientId
if ([string]::IsNullOrWhiteSpace($clientId) -or $clientId -like 'PASTE-*') { throw "OAuth clientId is not configured in $ConfigPath." }
$authority = [string]$config.authority
if ([string]::IsNullOrWhiteSpace($authority)) { $authority = $DefaultAuthority }
$scopeValues = @($config.scopes)
if ($scopeValues.Count -eq 0) { $scopeValues = $DefaultScopes }
$scope = ($scopeValues -join ' ')

function Request-Token([string]$RefreshToken) {
    Invoke-RestMethod -Uri "$authority/oauth2/v2.0/token" -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{
        client_id = $clientId
        grant_type = 'refresh_token'
        refresh_token = $RefreshToken
        scope = $scope
    } -TimeoutSec 30
}
function Device-Login {
    $device = Invoke-RestMethod -Uri "$authority/oauth2/v2.0/devicecode" -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{
        client_id = $clientId
        scope = $scope
    } -TimeoutSec 30
    Write-Host ''
    Write-Host 'HVMC Microsoft login required.'
    Write-Host $device.message
    Write-Host ''
    $interval = [Math]::Max([int]$device.interval,5)
    $deadline = (Get-Date).AddSeconds([int]$device.expires_in)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $interval
        try {
            return Invoke-RestMethod -Uri "$authority/oauth2/v2.0/token" -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{
                client_id=$clientId
                grant_type='urn:ietf:params:oauth:grant-type:device_code'
                device_code=[string]$device.device_code
            } -TimeoutSec 30
        } catch {
            $text = $_.ErrorDetails.Message
            if ($text -match 'authorization_pending') { continue }
            if ($text -match 'slow_down') { $interval += 5; continue }
            throw
        }
    }
    throw 'Microsoft device login expired.'
}

$token = $null
$refresh = Read-Secure $RefreshTokenPath
if ($refresh) { try { $token = Request-Token $refresh } catch { $token = $null } }
if (-not $token) { $token = Device-Login }
if (-not $token.access_token) { throw 'Microsoft OAuth did not return an access token.' }
if ($token.refresh_token) { Save-Secure ([string]$token.refresh_token) $RefreshTokenPath }
if ($token.id_token) { Save-Secure ([string]$token.id_token) $IdTokenPath }

$profile = $null
try {
    $profile = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/me?$select=id,displayName,userPrincipalName,mail' -Headers @{ Authorization = "Bearer $($token.access_token)" } -TimeoutSec 30
} catch {
    $profile = Decode-JwtPayload ([string]$token.id_token)
}
if (-not $profile) { throw 'Could not determine the signed-in Microsoft account.' }
Save-Json $profile $ProfilePath

[pscustomobject]@{
    authenticated = $true
    accountId = [string]$profile.id
    displayName = [string]$profile.displayName
    userPrincipalName = [string]$profile.userPrincipalName
    profilePath = $ProfilePath
} | ConvertTo-Json -Depth 5
