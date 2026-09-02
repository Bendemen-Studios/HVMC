$ErrorActionPreference = 'Stop'

$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$ConfigPath = Join-Path $Root 'oauth.json'
$TokenPath = Join-Path $Root 'oauth-token.txt'
$ProfilePath = Join-Path $Root 'oauth-profile.json'

New-Item -ItemType Directory -Force -Path $Root | Out-Null

function Read-Json([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    try { return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json } catch { return $null }
}

function Save-Json($Value,[string]$Path) {
    $Value | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $Path -Encoding UTF8
}

function Decode-JwtPayload([string]$Jwt) {
    try {
        $part = $Jwt.Split('.')[1].Replace('-','+').Replace('_','/')
        while ($part.Length % 4) { $part += '=' }
        $bytes = [Convert]::FromBase64String($part)
        return [Text.Encoding]::UTF8.GetString($bytes) | ConvertFrom-Json
    } catch { return $null }
}

$config = Read-Json $ConfigPath
if (-not $config) {
    $example = Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'oauth-config.example.json'
    if (Test-Path $example) { Copy-Item $example $ConfigPath -Force }
    throw "OAuth is not configured. Edit $ConfigPath and set clientId to your Microsoft App Registration client ID."
}

$clientId = [string]$config.clientId
if ([string]::IsNullOrWhiteSpace($clientId) -or $clientId -like 'PASTE-*') {
    throw "OAuth clientId is not configured in $ConfigPath."
}

$authority = [string]$config.authority
if ([string]::IsNullOrWhiteSpace($authority)) { $authority = 'https://login.microsoftonline.com/common' }
$scope = (@($config.scopes) -join ' ')
if ([string]::IsNullOrWhiteSpace($scope)) { $scope = 'openid profile offline_access https://graph.microsoft.com/User.Read' }

function Save-TokenSecure([string]$RefreshToken) {
    # DPAPI: readable only by the current Windows user on this PC.
    ConvertTo-SecureString $RefreshToken -AsPlainText -Force |
        ConvertFrom-SecureString |
        Set-Content -LiteralPath $TokenPath -Encoding UTF8
}

function Read-TokenSecure {
    if (-not (Test-Path -LiteralPath $TokenPath)) { return $null }
    try {
        $secure = Get-Content -LiteralPath $TokenPath -Raw | ConvertTo-SecureString
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR(
            [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        )
    } catch { return $null }
}

function Request-Token([string]$RefreshToken) {
    $uri = "$authority/oauth2/v2.0/token"
    $body = @{
        client_id = $clientId
        grant_type = 'refresh_token'
        refresh_token = $RefreshToken
        scope = $scope
    }
    return Invoke-RestMethod -Uri $uri -Method Post -ContentType 'application/x-www-form-urlencoded' -Body $body -TimeoutSec 30
}

function Device-Login {
    $deviceUri = "$authority/oauth2/v2.0/devicecode"
    $tokenUri = "$authority/oauth2/v2.0/token"

    $device = Invoke-RestMethod -Uri $deviceUri -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{
        client_id = $clientId
        scope = $scope
    } -TimeoutSec 30

    Write-Host ''
    Write-Host 'HVMC Microsoft login required.'
    Write-Host $device.message
    Write-Host ''

    $interval = [Math]::Max([int]$device.interval, 5)
    $deadline = (Get-Date).AddSeconds([int]$device.expires_in)

    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds $interval
        try {
            return Invoke-RestMethod -Uri $tokenUri -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{
                client_id = $clientId
                grant_type = 'urn:ietf:params:oauth:grant-type:device_code'
                device_code = [string]$device.device_code
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
$refreshToken = Read-TokenSecure
if ($refreshToken) {
    try { $token = Request-Token $refreshToken } catch { $token = $null }
}
if (-not $token) {
    $token = Device-Login
}
if (-not $token.access_token) { throw 'Microsoft OAuth did not return an access token.' }
if ($token.refresh_token) { Save-TokenSecure ([string]$token.refresh_token) }

$profile = $null
try {
    $profile = Invoke-RestMethod -Uri 'https://graph.microsoft.com/v1.0/me?$select=id,displayName,userPrincipalName,mail' -Headers @{ Authorization = "Bearer $($token.access_token)" } -TimeoutSec 30
} catch {
    # Fall back to the ID token for local display only. The VPS must not trust this unverified payload.
    $profile = Decode-JwtPayload ([string]$token.id_token)
}

if (-not $profile) { throw 'Could not determine the signed-in Microsoft account.' }
Save-Json $profile $ProfilePath

[pscustomobject]@{
    authenticated = $true
    accountId = [string]$profile.id
    displayName = [string]$profile.displayName
    userPrincipalName = [string]$profile.userPrincipalName
    tokenPath = $TokenPath
    profilePath = $ProfilePath
} | ConvertTo-Json -Depth 5
