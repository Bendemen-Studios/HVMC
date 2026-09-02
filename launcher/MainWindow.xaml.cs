using CmlLib.Core;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Forms = System.Windows.Forms;

namespace HVMCLauncher;

public partial class MainWindow : Window
{
    private const string PoolApi = "https://accounts.hvmc.nl";
    private const string LatestReleaseApi = "https://api.github.com/repos/Bendemen-Studios/HVMC/releases/latest";
    private const string MinecraftVersion = "1.21.11";
    private const string FabricVersion = "0.18.1";
    private const int MaximumRamMb = 4096;

    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bendemen", "HVMC");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private string? _leaseId;
    private string? _clientId;
    private CancellationTokenSource? _heartbeatCts;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_root);
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PlayButton.IsEnabled = false;
        ExitButton.IsEnabled = true;
        try
        {
            SetStatus("HVMC controleren...");
            var restarted = await CheckForLauncherUpdateAsync();
            if (restarted) return;
            SetStatus("Klaar om te spelen.");
            PlayButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            // A launcher update is optional; an existing launcher should still be usable.
            SetStatus("Klaar om te spelen.");
            PlayButton.IsEnabled = true;
            Debug.WriteLine($"Launcher update check failed: {ex}");
        }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        PlayButton.IsEnabled = false;
        ExitButton.IsEnabled = false;
        try
        {
            SetStatus("HVMC controleren...");
            await RunUpdaterAsync();

            _clientId = GetStableClientId();
            SetStatus("Vrij Minecraft-account zoeken...");
            var lease = await AcquireLeaseAsync(_clientId);
            _leaseId = lease.LeaseId;
            SetStatus($"{lease.AccountName} geselecteerd.");

            var minecraftPath = new MinecraftPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"));
            var minecraftLauncher = new MinecraftLauncher(minecraftPath);
            var session = new MSession(lease.Username, lease.MinecraftAccessToken, lease.Uuid)
            {
                Xuid = lease.Xuid ?? string.Empty
            };

            SetStatus("Minecraft controleren...");
            await minecraftLauncher.InstallAsync(MinecraftVersion);

            var display = Forms.Screen.PrimaryScreen?.Bounds;
            var width = display?.Width ?? 1920;
            var height = display?.Height ?? 1080;
            var fabricProfile = $"fabric-loader-{FabricVersion}-{MinecraftVersion}";

            SetStatus($"Minecraft starten op {width}x{height}...");
            var process = await minecraftLauncher.InstallAndBuildProcessAsync(fabricProfile, new MLaunchOption
            {
                Session = session,
                MaximumRamMb = MaximumRamMb,
                GameLauncherName = "HVMC",
                FullScreen = true,
                ScreenWidth = width,
                ScreenHeight = height
            });

            process.Start();
            SetStatus("Minecraft draait.");
            StartLeaseHeartbeat(_clientId, _leaseId);
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Starten mislukt.");
            MessageBox.Show(this, ex.Message, "HVMC", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _heartbeatCts?.Cancel();
            await ReleaseLeaseSafeAsync();
            PlayButton.IsEnabled = true;
            ExitButton.IsEnabled = true;
            SetStatus("Klaar om te spelen.");
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();

    private async Task<bool> CheckForLauncherUpdateAsync()
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe)) return false;

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("HVMC-Launcher");
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return false;
        var json = await response.Content.ReadAsStringAsync();
        var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);
        var asset = release?.Assets?.FirstOrDefault(x => string.Equals(x.Name, "HVMCLauncher.exe", StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl)) return false;

        SetStatus("Launcher-versie controleren...");
        var temp = Path.Combine(_root, $"HVMCLauncher-update-{Guid.NewGuid():N}.exe");
        using (var dl = await _http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            dl.EnsureSuccessStatusCode();
            await using var source = await dl.Content.ReadAsStreamAsync();
            await using var target = File.Create(temp);
            await source.CopyToAsync(target);
        }

        var currentHash = await Sha256Async(currentExe);
        var newHash = await Sha256Async(temp);
        if (CryptographicOperations.FixedTimeEquals(currentHash, newHash))
        {
            File.Delete(temp);
            return false;
        }

        SetStatus("Nieuwe launcher gevonden. Updaten...");
        ScheduleSelfReplacement(currentExe, temp);
        return true;
    }

    private static async Task<byte[]> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return await SHA256.HashDataAsync(stream);
    }

    private static void ScheduleSelfReplacement(string currentExe, string updateExe)
    {
        var pid = Environment.ProcessId;
        static string Ps(string value) => "'" + value.Replace("'", "''") + "'";
        var script = $"$pid={pid};$src={Ps(updateExe)};$dst={Ps(currentExe)};Start-Sleep -Milliseconds 800;while(Get-Process -Id $pid -ErrorAction SilentlyContinue){{Start-Sleep -Milliseconds 200}};Move-Item -LiteralPath $src -Destination $dst -Force;Start-Process -FilePath $dst";
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        Environment.Exit(0);
    }

    private async Task RunUpdaterAsync()
    {
        var updater = Path.Combine(_root, "HVMCUpdater.ps1");
        using var response = await _http.GetAsync("https://raw.githubusercontent.com/Bendemen-Studios/HVMC/main/HVMCUpdater.ps1");
        response.EnsureSuccessStatusCode();
        await File.WriteAllBytesAsync(updater, await response.Content.ReadAsByteArrayAsync());

        using var p = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList = { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", updater }
        }) ?? throw new InvalidOperationException("HVMC updater kon niet worden gestart.");

        var stdout = await p.StandardOutput.ReadToEndAsync();
        var stderr = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        if (p.ExitCode != 0)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
    }

    private async Task<LeaseResponse> AcquireLeaseAsync(string clientId)
    {
        using var content = new StringContent(JsonSerializer.Serialize(new { clientId }), Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync($"{PoolApi}/v1/launcher/lease/acquire", content);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException(GetError(json));
        return JsonSerializer.Deserialize<LeaseResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("Ongeldige pool-response.");
    }

    private void StartLeaseHeartbeat(string clientId, string leaseId)
    {
        _heartbeatCts?.Cancel();
        _heartbeatCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_heartbeatCts.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), _heartbeatCts.Token);
                    if (_heartbeatCts.IsCancellationRequested) break;
                    using var content = new StringContent(JsonSerializer.Serialize(new { clientId, leaseId }), Encoding.UTF8, "application/json");
                    await _http.PostAsync($"{PoolApi}/v1/launcher/lease/heartbeat", content, _heartbeatCts.Token);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, _heartbeatCts.Token);
    }

    private async Task ReleaseLeaseSafeAsync()
    {
        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_leaseId)) return;
        try
        {
            using var content = new StringContent(JsonSerializer.Serialize(new { clientId = _clientId, leaseId = _leaseId }), Encoding.UTF8, "application/json");
            await _http.PostAsync($"{PoolApi}/v1/launcher/lease/release", content);
        }
        catch { }
        finally { _leaseId = null; }
    }

    private string GetStableClientId()
    {
        var path = Path.Combine(_root, "client-id.txt");
        if (File.Exists(path))
        {
            var existing = File.ReadAllText(path).Trim();
            if (Guid.TryParse(existing, out _)) return existing;
        }
        var created = Guid.NewGuid().ToString();
        File.WriteAllText(path, created);
        return created;
    }

    private void SetStatus(string message) => StatusText.Text = message;

    private static string GetError(string json)
    {
        try { return JsonSerializer.Deserialize<ApiError>(json, JsonOptions)?.Error ?? json; }
        catch { return string.IsNullOrWhiteSpace(json) ? "Onbekende fout." : json; }
    }

    private sealed record LeaseResponse(
        string LeaseId,
        string AccountId,
        string AccountName,
        string? MicrosoftUsername,
        string Username,
        string Uuid,
        string MinecraftAccessToken,
        int ExpiresIn,
        string ExpiresAt,
        string? Xuid
    );
    private sealed record ApiError(string Error);
    private sealed record GitHubRelease(List<GitHubAsset>? Assets);
    private sealed record GitHubAsset(string Name, string BrowserDownloadUrl);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
