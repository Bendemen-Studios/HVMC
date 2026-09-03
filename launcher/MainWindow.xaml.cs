using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Windows;
using Forms = System.Windows.Forms;
using WpfMessageBox = System.Windows.MessageBox;

namespace HVMCLauncher;

public partial class MainWindow : Window
{
    private const string PoolApi = "https://accounts.hvmc.nl";
    private const string LatestReleaseApi = "https://api.github.com/repos/Bendemen-Studios/HVMC/releases/latest";
    private const string MinecraftVersion = "1.21.11";
    private const string FabricVersion = "0.18.1";
    private const string LauncherVersion = "1.3.0";
    private const int MaximumRamMb = 4096;
    private const int PcHeartbeatSeconds = 30;

    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bendemen", "HVMC");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private string? _clientId;
    private string? _deviceToken;
    private string? _leaseId;
    private CancellationTokenSource? _leaseHeartbeatCts;
    private CancellationTokenSource? _pcHeartbeatCts;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_root);
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _leaseHeartbeatCts?.Cancel();
            _pcHeartbeatCts?.Cancel();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PlayButton.IsEnabled = false;
        ExitButton.IsEnabled = true;
        try
        {
            _clientId = GetStableClientId();
            _deviceToken = GetDeviceToken();
            SetStatus("HVMC School Launcher controleren...");
            if (await CheckForLauncherUpdateAsync()) return;
            if (!await EnsurePcAuthorizedAsync()) return;
            await SendPcHeartbeatAsync();
            StartPcHeartbeat();
            SetStatus("Klaar om te spelen.");
            PlayButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SetStatus("Controle mislukt.");
            WpfMessageBox.Show(this, ex.Message, "HVMC School Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitButton.IsEnabled = true;
        }
    }

    private async Task<bool> EnsurePcAuthorizedAsync()
    {
        if (string.IsNullOrWhiteSpace(_clientId)) return false;
        using var response = await _http.GetAsync($"{PoolApi}/v1/launcher/pc/status?clientId={Uri.EscapeDataString(_clientId)}");
        var json = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(_deviceToken)) return true;
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && json.Contains("geblokkeerd", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Deze pc is door een beheerder geblokkeerd.");
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound && response.StatusCode != System.Net.HttpStatusCode.Unauthorized && response.StatusCode != System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException(GetError(json));

        AuthorizationPanel.Visibility = Visibility.Visible;
        AuthorizationCodeBox.Focus();
        SetStatus("Deze pc moet eenmalig worden geautoriseerd.");
        return false;
    }

    private async void AuthorizeButton_Click(object sender, RoutedEventArgs e)
    {
        AuthorizeButton.IsEnabled = false;
        try
        {
            var code = AuthorizationCodeBox.Text.Trim().ToUpperInvariant();
            if (code.Length != 10) throw new InvalidOperationException("Vul de 10-karakter HVMC pc-autorisatiecode in.");
            _clientId ??= GetStableClientId();
            var name = Environment.MachineName;
            using var content = new StringContent(JsonSerializer.Serialize(new { clientId = _clientId, code, name }), Encoding.UTF8, "application/json");
            using var response = await _http.PostAsync($"{PoolApi}/v1/launcher/pc/register", content);
            var json = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode) throw new InvalidOperationException(GetError(json));
            var result = JsonSerializer.Deserialize<PcRegistrationResponse>(json, JsonOptions) ?? throw new InvalidOperationException("Ongeldige pc-autorisatie-response.");
            if (string.IsNullOrWhiteSpace(result.DeviceToken)) throw new InvalidOperationException("De server gaf geen pc-token terug.");
            _deviceToken = result.DeviceToken;
            SaveDeviceToken(result.DeviceToken);
            AuthorizationPanel.Visibility = Visibility.Collapsed;
            await SendPcHeartbeatAsync();
            StartPcHeartbeat();
            SetStatus("Pc geautoriseerd. Klaar om te spelen.");
            PlayButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            SetStatus("Pc-autorisatie mislukt.");
            WpfMessageBox.Show(this, ex.Message, "HVMC School Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { AuthorizeButton.IsEnabled = true; }
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        PlayButton.IsEnabled = false;
        ExitButton.IsEnabled = false;
        try
        {
            if (!await EnsurePcAuthorizedAsync()) { ExitButton.IsEnabled = true; return; }

            var minecraftPath = new MinecraftPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"));
            var minecraftLauncher = new MinecraftLauncher(minecraftPath);

            SetStatus("Minecraft voorbereiden...");
            await minecraftLauncher.InstallAsync(MinecraftVersion);

            SetStatus("HVMC content synchroniseren...");
            await RunUpdaterAsync();

            _clientId ??= GetStableClientId();
            _deviceToken ??= GetDeviceToken();
            if (string.IsNullOrWhiteSpace(_deviceToken)) throw new InvalidOperationException("Deze pc is niet geautoriseerd.");

            SetStatus("Vrij Minecraft-account zoeken...");
            var lease = await AcquireLeaseAsync(_clientId, _deviceToken);
            _leaseId = lease.LeaseId;
            SetStatus($"{lease.AccountName} geselecteerd.");

            var session = new MSession { Username = lease.Username, AccessToken = lease.MinecraftAccessToken, UUID = lease.Uuid, Xuid = lease.Xuid ?? string.Empty };
            var display = Forms.Screen.PrimaryScreen?.Bounds;
            var width = display?.Width ?? 1920;
            var height = display?.Height ?? 1080;
            var fabricProfile = $"fabric-loader-{FabricVersion}-{MinecraftVersion}";

            SetStatus($"Fabric voorbereiden en Minecraft starten op {width}x{height}...");
            var process = await minecraftLauncher.InstallAndBuildProcessAsync(fabricProfile, new MLaunchOption
            {
                Session = session,
                MaximumRamMb = MaximumRamMb,
                GameLauncherName = "HVMC School Launcher",
                FullScreen = true,
                ScreenWidth = width,
                ScreenHeight = height
            });
            process.Start();
            SetStatus("Minecraft draait.");
            StartLeaseHeartbeat(_clientId, _deviceToken, _leaseId);
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Starten mislukt.");
            WpfMessageBox.Show(this, ex.Message, "HVMC School Launcher", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _leaseHeartbeatCts?.Cancel();
            await ReleaseLeaseSafeAsync();
            PlayButton.IsEnabled = true;
            ExitButton.IsEnabled = true;
            if (AuthorizationPanel.Visibility != Visibility.Visible) SetStatus("Klaar om te spelen.");
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();

    private void StartPcHeartbeat()
    {
        _pcHeartbeatCts?.Cancel();
        _pcHeartbeatCts = new CancellationTokenSource();
        var token = _pcHeartbeatCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(PcHeartbeatSeconds), token);
                    if (token.IsCancellationRequested) break;
                    await SendPcHeartbeatAsync(token);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, token);
    }

    private async Task SendPcHeartbeatAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_deviceToken)) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{PoolApi}/v1/launcher/pc/heartbeat");
        request.Headers.Add("x-hvmc-client-id", _clientId);
        request.Headers.Add("x-hvmc-device-token", _deviceToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { clientId = _clientId, osVersion = Environment.OSVersion.VersionString, launcherVersion = LauncherVersion }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(GetError(json));
    }

    private async Task<bool> CheckForLauncherUpdateAsync()
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe)) return false;

        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.ParseAdd("HVMC-School-Launcher");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await _http.SendAsync(request);
        if (!response.IsSuccessStatusCode) return false;

        var json = await response.Content.ReadAsStringAsync();
        var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);
        var tag = release?.TagName?.Trim();
        if (string.IsNullOrWhiteSpace(tag)) return false;

        var remoteText = tag.TrimStart('v', 'V');
        if (Version.TryParse(remoteText, out var remoteVersion) && Version.TryParse(LauncherVersion, out var currentVersion))
        {
            SetStatus($"Launcher controleren: huidig {currentVersion}, beschikbaar {remoteVersion}...");
            if (remoteVersion <= currentVersion) return false;
        }

        var asset = release?.Assets?.FirstOrDefault(x => string.Equals(x.Name, "HVMCLauncher.exe", StringComparison.OrdinalIgnoreCase));
        if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl)) return false;

        SetStatus($"Nieuwe launcher {tag} gevonden. Downloaden...");
        var temp = Path.Combine(_root, $"HVMCLauncher-update-{Guid.NewGuid():N}.exe");
        using (var dl = await _http.GetAsync(asset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            dl.EnsureSuccessStatusCode();
            await using var source = await dl.Content.ReadAsStreamAsync();
            await using var target = File.Create(temp);
            await source.CopyToAsync(target);
        }

        if (asset.Size > 0 && new FileInfo(temp).Length != asset.Size)
        {
            File.Delete(temp);
            throw new InvalidOperationException("De gedownloade launcher heeft een onjuiste bestandsgrootte.");
        }

        var currentHash = await Sha256Async(currentExe);
        var newHash = await Sha256Async(temp);
        if (CryptographicOperations.FixedTimeEquals(currentHash, newHash))
        {
            File.Delete(temp);
            return false;
        }

        SetStatus($"HVMC School Launcher {tag} installeren...");
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
        static string Ps(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
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
        if (p.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(stderr) ? stdout : stderr);
    }

    private async Task<LeaseResponse> AcquireLeaseAsync(string clientId, string deviceToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{PoolApi}/v1/launcher/lease/acquire");
        request.Headers.Add("x-hvmc-client-id", clientId);
        request.Headers.Add("x-hvmc-device-token", deviceToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { clientId }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(GetError(json));
        return JsonSerializer.Deserialize<LeaseResponse>(json, JsonOptions) ?? throw new InvalidOperationException("Ongeldige pool-response.");
    }

    private void StartLeaseHeartbeat(string clientId, string deviceToken, string leaseId)
    {
        _leaseHeartbeatCts?.Cancel();
        _leaseHeartbeatCts = new CancellationTokenSource();
        var token = _leaseHeartbeatCts.Token;
        _ = Task.Run(async () =>
        {
            while (!token.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(5), token);
                    if (token.IsCancellationRequested) break;
                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{PoolApi}/v1/launcher/lease/heartbeat");
                    request.Headers.Add("x-hvmc-client-id", clientId);
                    request.Headers.Add("x-hvmc-device-token", deviceToken);
                    request.Content = new StringContent(JsonSerializer.Serialize(new { clientId, leaseId }), Encoding.UTF8, "application/json");
                    await _http.SendAsync(request, token);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, token);
    }

    private async Task ReleaseLeaseSafeAsync()
    {
        if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_deviceToken) || string.IsNullOrWhiteSpace(_leaseId)) return;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{PoolApi}/v1/launcher/lease/release");
            request.Headers.Add("x-hvmc-client-id", _clientId);
            request.Headers.Add("x-hvmc-device-token", _deviceToken);
            request.Content = new StringContent(JsonSerializer.Serialize(new { clientId = _clientId, leaseId = _leaseId }), Encoding.UTF8, "application/json");
            await _http.SendAsync(request);
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

    private string? GetDeviceToken()
    {
        var path = Path.Combine(_root, "device-token.txt");
        if (!File.Exists(path)) return null;
        var token = File.ReadAllText(path).Trim();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private void SaveDeviceToken(string token) => File.WriteAllText(Path.Combine(_root, "device-token.txt"), token);
    private void SetStatus(string message) => StatusText.Text = message;
    private static string GetError(string json)
    {
        try { return JsonSerializer.Deserialize<ApiError>(json, JsonOptions)?.Error ?? json; }
        catch { return string.IsNullOrWhiteSpace(json) ? "Onbekende fout." : json; }
    }

    private sealed record LeaseResponse(string LeaseId,string AccountId,string AccountName,string? MicrosoftUsername,string Username,string Uuid,string MinecraftAccessToken,int ExpiresIn,string ExpiresAt,string? Xuid);
    private sealed record PcRegistrationResponse(string DeviceToken);
    private sealed record ApiError(string Error);
    private sealed record GitHubRelease(string? TagName, List<GitHubAsset>? Assets);
    private sealed record GitHubAsset(string Name, string BrowserDownloadUrl, long Size);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
