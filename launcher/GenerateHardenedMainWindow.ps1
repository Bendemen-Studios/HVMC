param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination
)

$ErrorActionPreference = 'Stop'
$text = [IO.File]::ReadAllText($Source)

$patternUpdater = '(?s)    private async Task RunUpdaterAsync\(\)\s*\{.*?\r?\n    \}\r?\n\r?\n    private async Task<LeaseResponse>'
$replacementUpdater = @'
    private async Task RunUpdaterAsync()
    {
        var updater = Path.Combine(_root, "HVMCUpdater.ps1");
        var assembly = System.Reflection.Assembly.GetExecutingAssembly();
        var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith("HVMCUpdater.ps1", StringComparison.OrdinalIgnoreCase));
        if (string.IsNullOrWhiteSpace(resourceName)) throw new InvalidOperationException("De ingebouwde HVMC updater ontbreekt in deze launcher-build.");
        await using (var resource = assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("De ingebouwde HVMC updater kon niet worden geopend."))
        await using (var target = File.Create(updater))
        {
            await resource.CopyToAsync(target);
        }

        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", updater }
        }) ?? throw new InvalidOperationException("HVMC updater kon niet worden gestart.");
        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
    }

    private async Task<LeaseResponse>
'@

$patternIdentity = '(?s)    private static string GetStableClientId\(\)\s*\{.*?\r?\n    \}\r?\n\r?\n    private string\? GetDeviceToken\(\)\s*\{.*?\r?\n    \}\r?\n\r?\n    private void SaveDeviceToken\(string token\)\s*=>.*?;'
$replacementIdentity = @'
    private static string GetStableClientId()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bendemen", "HVMC", "client-id.txt");
        try
        {
            if (File.Exists(path))
            {
                var existing = File.ReadAllText(path).Trim();
                if (Guid.TryParse(existing, out _)) return existing;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var created = Guid.NewGuid().ToString("D");
            File.WriteAllText(path, created);
            return created;
        }
        catch
        {
            return Guid.NewGuid().ToString("D");
        }
    }

    private string? GetDeviceToken()
    {
        var path = Path.Combine(_root, "device.token");
        var legacyPath = Path.Combine(_root, "device-token.txt");
        try
        {
            if (File.Exists(path))
            {
                var protectedBytes = File.ReadAllBytes(path);
                var clearBytes = System.Security.Cryptography.ProtectedData.Unprotect(
                    protectedBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
                var token = Encoding.UTF8.GetString(clearBytes).Trim();
                return string.IsNullOrWhiteSpace(token) ? null : token;
            }
        }
        catch { }

        try
        {
            if (File.Exists(legacyPath))
            {
                var legacyToken = File.ReadAllText(legacyPath).Trim();
                if (!string.IsNullOrWhiteSpace(legacyToken))
                {
                    SaveDeviceToken(legacyToken);
                    try { File.Delete(legacyPath); } catch { }
                    return legacyToken;
                }
            }
        }
        catch { }

        return null;
    }

    private void SaveDeviceToken(string token)
    {
        var path = Path.Combine(_root, "device.token");
        Directory.CreateDirectory(_root);
        var clearBytes = Encoding.UTF8.GetBytes(token.Trim());
        var protectedBytes = System.Security.Cryptography.ProtectedData.Protect(
            clearBytes, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        File.WriteAllBytes(path, protectedBytes);
    }
'@

$updated = [regex]::Replace($text, $patternUpdater, $replacementUpdater, 1)
$updated = [regex]::Replace($updated, $patternIdentity, $replacementIdentity, 1)
$updated = $updated.Replace('private const string LauncherVersion = "2.4.0";', 'private const string LauncherVersion = "2.5.0";', [StringComparison]::Ordinal)

if ($updated -eq $text) { throw 'Expected launcher blocks were not found; refusing to generate an unmodified launcher.' }
if ($updated -notmatch 'private const string LauncherVersion = "2\.5\.0";') { throw 'Launcher version 2.5.0 was not applied.' }

$remainingLegacyMethods = @(
    'private static string GetStableClientId',
    'private string? GetDeviceToken',
    'private void SaveDeviceToken'
)
foreach ($method in $remainingLegacyMethods) {
    if (($updated | Select-String -SimpleMatch $method -Quiet)) { throw "Legacy method remained after hardening: $method" }
}

$parent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
[IO.File]::WriteAllText($Destination, $updated, [Text.UTF8Encoding]::new($false))
