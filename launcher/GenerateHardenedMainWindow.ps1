param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination
)

$ErrorActionPreference = 'Stop'
$text = [IO.File]::ReadAllText($Source)
$pattern = '(?s)    private async Task RunUpdaterAsync\(\)\s*\{.*?\r?\n    \}\r?\n\r?\n    private async Task<LeaseResponse>'
$replacement = @'
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

$updated = [regex]::Replace($text, $pattern, $replacement, 1)
if ($updated -eq $text) { throw 'RunUpdaterAsync block was not found; refusing to generate an unmodified launcher.' }

$parent = Split-Path -Parent $Destination
New-Item -ItemType Directory -Force -Path $parent | Out-Null
[IO.File]::WriteAllText($Destination, $updated, [Text.UTF8Encoding]::new($false))
