$ErrorActionPreference = 'Stop'

$Root = Join-Path $env:LOCALAPPDATA 'Bendemen\HVMC'
$ConfigPath = Join-Path $Root 'oauth.json'
$RefreshTokenPath = Join-Path $Root 'oauth-refresh-token.txt'
$AccessTokenPath = Join-Path $Root 'oauth-access-token.txt'
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
function Base64Url([byte[]]$Bytes) {
    [Convert]::ToBase64String($Bytes).TrimEnd('=').Replace('+','-').Replace('/','_')
}
function New-CodeVerifier {
    $bytes = New-Object byte[] 64
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    Base64Url $bytes
}
function Get-CodeChallenge([string]$Verifier) {
    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($Verifier))
    Base64Url $hash
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
    $config = [pscustomobject]@{ clientId = $DefaultClientId; authority = $DefaultAuthority; scopes = $DefaultScopes }
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
        client_id=$clientId; grant_type='refresh_token'; refresh_token=$RefreshToken; scope=$scope
    } -TimeoutSec 30
}

function Browser-OAuthLogin {
    $verifier = New-CodeVerifier
    $challenge = Get-CodeChallenge $verifier
    $stateBytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Fill($stateBytes)
    $state = Base64Url $stateBytes

    $tcp = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
    $tcp.Start()
    $port = ([Net.IPEndPoint]$tcp.LocalEndpoint).Port
    $tcp.Stop()

    $redirect = "http://localhost:$port/"
    $listener = [Net.HttpListener]::new()
    $listener.Prefixes.Add($redirect)
    $listener.Start()

    try {
        $query = @{
            client_id=$clientId
            response_type='code'
            redirect_uri=$redirect
            response_mode='query'
            scope=$scope
            state=$state
            code_challenge=$challenge
            code_challenge_method='S256'
            prompt='select_account'
        }.GetEnumerator() | ForEach-Object {
            "$([Uri]::EscapeDataString([string]$_.Key))=$([Uri]::EscapeDataString([string]$_.Value))"
        }
        $authorizeUrl = "$authority/oauth2/v2.0/authorize?" + ($query -join '&')
        Start-Process $authorizeUrl | Out-Null

        $context = $listener.GetContext()
        $params = @{}
        foreach ($pair in $context.Request.Url.Query.TrimStart('?').Split('&')) {
            if (-not $pair) { continue }
            $parts = $pair.Split('=',2)
            $key = [Uri]::UnescapeDataString($parts[0])
            $value = if ($parts.Count -gt 1) { [Uri]::UnescapeDataString($parts[1]) } else { '' }
            $params[$key] = $value
        }

        $ok = $params['code'] -and $params['state'] -eq $state
        $title = if ($ok) { 'HVMC account gekoppeld' } else { 'HVMC login mislukt' }
        $msg = if ($ok) { 'Je kunt dit venster sluiten.' } else { 'De aanmelding kon niet worden voltooid.' }
        $html = "<html><head><meta charset='utf-8'></head><body style='font-family:Segoe UI;text-align:center;padding:60px'><h2>$title</h2><p>$msg</p></body></html>"
        $bytes = [Text.Encoding]::UTF8.GetBytes($html)
        $context.Response.ContentType = 'text/html; charset=utf-8'
        $context.Response.ContentLength64 = $bytes.Length
        $context.Response.OutputStream.Write($bytes,0,$bytes.Length)
        $context.Response.OutputStream.Close()
        $context.Response.Close()

        if (-not $ok) { throw 'Microsoft OAuth authorization failed or state validation failed.' }

        Invoke-RestMethod -Uri "$authority/oauth2/v2.0/token" -Method Post -ContentType 'application/x-www-form-urlencoded' -Body @{
            client_id=$clientId
            grant_type='authorization_code'
            code=[string]$params['code']
            redirect_uri=$redirect
            code_verifier=$verifier
            scope=$scope
        } -TimeoutSec 30
    } finally {
        if ($listener.IsListening) { $listener.Stop() }
        $listener.Close()
    }
}

$token = $null
$refresh = Read-Secure $RefreshTokenPath
if ($refresh) { try { $token = Request-Token $refresh } catch { $token = $null } }
if (-not $token) { $token = Browser-OAuthLogin }
if (-not $token.access_token) { throw 'Microsoft OAuth did not return an access token.' }
Save-Secure ([string]$token.access_token) $AccessTokenPath
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

[pscustomobject]@{ authenticated=$true; accountId=[string]$profile.id; displayName=[string]$profile.displayName; userPrincipalName=[string]$profile.userPrincipalName; profilePath=$ProfilePath } | ConvertTo-Json -Depth 5
