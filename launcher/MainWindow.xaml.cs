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
using System.Windows.Controls;
using System.Windows.Media;
using Forms = System.Windows.Forms;

namespace HVMCLauncher;

public partial class MainWindow : Window
{
    private const string PoolApi = "https://accounts.hvmc.nl";
    private const string LatestReleaseApi = "https://api.github.com/repos/Bendemen-Studios/HVMC/releases/latest";
    private const string MinecraftVersion = "26.2";
    private const string FabricVersion = "0.19.3";
    private static readonly string LauncherVersion = App.AppVersion;
    private const int MaximumRamMb = 4096;
    private const int PcHeartbeatSeconds = 30;
    private const int LeaseHeartbeatSeconds = 30;

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
        Closed += (_, _) => { _leaseHeartbeatCts?.Cancel(); _pcHeartbeatCts?.Cancel(); };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        PlayButton.IsEnabled = false;
        ExitButton.IsEnabled = true;
        try
        {
            _clientId = GetStableClientId();
            _deviceToken = GetDeviceToken();
            SetStatus("HVMC School Launcher voorbereiden...");
            if (!await EnsurePcAuthorizedAsync()) return;
            await SendPcHeartbeatAsync();
            StartPcHeartbeat();
            SetStatus("Klaar om te spelen.");
            PlayButton.IsEnabled = true;
        }
        catch (Exception ex) { SetStatus("Controle mislukt."); ShowError("Controle mislukt", ex); ExitButton.IsEnabled = true; }
    }

    private async Task<bool> EnsurePcAuthorizedAsync()
    {
        if (string.IsNullOrWhiteSpace(_clientId)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PoolApi}/v1/launcher/pc/status?clientId={Uri.EscapeDataString(_clientId)}");
        if (!string.IsNullOrWhiteSpace(_deviceToken)) request.Headers.Add("x-hvmc-device-token", _deviceToken);
        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(_deviceToken)) return true;
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && json.Contains("geblokkeerd", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Deze pc is door een beheerder geblokkeerd.");
        if (response.StatusCode != System.Net.HttpStatusCode.NotFound && response.StatusCode != System.Net.HttpStatusCode.Unauthorized && response.StatusCode != System.Net.HttpStatusCode.Forbidden) throw new InvalidOperationException(GetError(json));
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
            var deviceToken = result.DeviceToken?.Trim();
            if (!IsValidHeaderValue(deviceToken)) throw new InvalidOperationException("De accountserver gaf een ongeldige pc-token terug.");
            _deviceToken = deviceToken;
            SaveDeviceToken(deviceToken!);
            AuthorizationPanel.Visibility = Visibility.Collapsed;
            await SendPcHeartbeatAsync();
            StartPcHeartbeat();
            SetStatus("Pc geautoriseerd. Klaar om te spelen.");
            PlayButton.IsEnabled = true;
        }
        catch (Exception ex) { SetStatus("Pc-autorisatie mislukt."); ShowError("Pc-autorisatie mislukt", ex); }
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
            SetStatus("HVMC content synchroniseren...");
            await RunUpdaterAsync();
            var minecraftLauncher = new MinecraftLauncher(minecraftPath);
            SetStatus("Minecraft voorbereiden...");
            await minecraftLauncher.InstallAsync(MinecraftVersion);
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
            var fabricProfilePath = Path.Combine(minecraftPath.BasePath, "versions", fabricProfile, $"{fabricProfile}.json");
            if (!File.Exists(fabricProfilePath)) throw new InvalidOperationException($"Gebundelde Fabric ontbreekt: {fabricProfilePath}");
            SetStatus($"Fabric {FabricVersion} controleren...");
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
            await SendLeaseHeartbeatAsync(_clientId, _deviceToken, _leaseId);
            StartLeaseHeartbeat(_clientId, _deviceToken, _leaseId);
            await process.WaitForExitAsync();
        }
        catch (Exception ex) { SetStatus("Starten mislukt."); ShowError("Starten mislukt", ex); }
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
                catch
                {
                    try { Dispatcher.Invoke(() => SetStatus("Verbinding met HVMC-server tijdelijk verloren; opnieuw proberen...")); } catch { }
                }
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
        if (asset.Size > 0 && new FileInfo(temp).Length != asset.Size) { File.Delete(temp); throw new InvalidOperationException("De gedownloade launcher heeft een onjuiste bestandsgrootte."); }
        var currentHash = await Sha256Async(currentExe);
        var newHash = await Sha256Async(temp);
        if (CryptographicOperations.FixedTimeEquals(currentHash, newHash)) { File.Delete(temp); return false; }
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
        Process.Start(new ProcessStartInfo { FileName = "powershell.exe", Arguments = $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -Command \"{script.Replace("\"", "\\\"")}\"", UseShellExecute = false, CreateNoWindow = true });
        Environment.Exit(0);
    }

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
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (p.ExitCode == 2)
        {
            // Explicit exception: GitHub could not be reached before a sync
            // started. Existing local content may be used.
            SetStatus("GitHub is offline. Bestaande HVMC-content wordt gebruikt.");
            return;
        }

        if (p.ExitCode != 0)
        {
            var details = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr + (string.IsNullOrWhiteSpace(stdout) ? string.Empty : $"{Environment.NewLine}{Environment.NewLine}{stdout}");
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(details)
                    ? $"HVMC content-update is mislukt (foutcode {p.ExitCode}). Minecraft wordt niet gestart."
                    : details.Trim());
        }
    }

    private async Task<LeaseResponse> AcquireLeaseAsync(string clientId, string deviceToken)
    {
        const int maxAttempts = 2;
        string? lastError = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{PoolApi}/v1/launcher/lease/acquire");
                request.Headers.Add("x-hvmc-client-id", clientId);
                request.Headers.Add("x-hvmc-device-token", deviceToken);
                request.Headers.Accept.ParseAdd("application/json");
                request.Content = new StringContent(JsonSerializer.Serialize(new { clientId }), Encoding.UTF8, "application/json");
                using var response = await _http.SendAsync(request);
                var body = await response.Content.ReadAsStringAsync();
                var contentType = response.Content.Headers.ContentType?.MediaType ?? "";
                if (string.IsNullOrWhiteSpace(body)) lastError = $"De accountserver gaf een lege response terug (HTTP {(int)response.StatusCode} {response.StatusCode}).";
                else if (!response.IsSuccessStatusCode) lastError = GetError(body);
                else
                {
                    try
                    {
                        var lease = JsonSerializer.Deserialize<LeaseResponse>(body, JsonOptions);
                        if (lease is null) throw new InvalidOperationException("De accountserver gaf geen accountgegevens terug.");
                        if (string.IsNullOrWhiteSpace(lease.LeaseId) || string.IsNullOrWhiteSpace(lease.Username) || string.IsNullOrWhiteSpace(lease.MinecraftAccessToken)) throw new InvalidOperationException("De accountserver gaf onvolledige accountgegevens terug.");
                        return lease;
                    }
                    catch (JsonException)
                    {
                        var preview = body.Length > 500 ? body[..500] + "..." : body;
                        throw new InvalidOperationException($"Ongeldige JSON van de accountserver (HTTP {(int)response.StatusCode}, {contentType}). Response: {preview}");
                    }
                }
            }
            catch (HttpRequestException ex) { lastError = $"Accountserver niet bereikbaar: {ex.Message}"; }
            catch (TaskCanceledException ex) { lastError = $"Time-out bij de accountserver: {ex.Message}"; }
            catch (InvalidOperationException ex) { lastError = ex.Message; }
            if (attempt < maxAttempts) { SetStatus("Accountserver reageerde niet goed. Opnieuw proberen..."); await Task.Delay(1200); }
        }
        throw new InvalidOperationException(lastError ?? "Onbekende fout bij het ophalen van een Minecraft-account.");
    }

    private async Task SendLeaseHeartbeatAsync(string clientId, string deviceToken, string leaseId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{PoolApi}/v1/launcher/lease/heartbeat");
        request.Headers.Add("x-hvmc-client-id", clientId);
        request.Headers.Add("x-hvmc-device-token", deviceToken);
        request.Content = new StringContent(JsonSerializer.Serialize(new { clientId, leaseId }), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(GetError(json));
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
                    await Task.Delay(TimeSpan.FromSeconds(LeaseHeartbeatSeconds), token);
                    if (token.IsCancellationRequested) break;
                    await SendLeaseHeartbeatAsync(clientId, deviceToken, leaseId, token);
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }, token);
    }

    private async Task ReleaseLeaseSafeAsync()
    {
        if (string.IsNullOrWhiteSpace(_leaseId) || string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_deviceToken)) return;
        var leaseId = _leaseId;
        _leaseId = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{PoolApi}/v1/launcher/lease/release");
            request.Headers.Add("x-hvmc-client-id", _clientId);
            request.Headers.Add("x-hvmc-device-token", _deviceToken);
            request.Content = new StringContent(JsonSerializer.Serialize(new { clientId = _clientId, leaseId }), Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request);
            if (!response.IsSuccessStatusCode) _ = await response.Content.ReadAsStringAsync();
        }
        catch { }
    }

    private string GetStableClientId()
    {
        var path = Path.Combine(_root, "client-id.txt");
        try
        {
            if (File.Exists(path))
            {
                var saved = File.ReadAllText(path).Trim();
                if (saved.Length == 64 && saved.All(Uri.IsHexDigit)) return saved.ToLowerInvariant();
            }
        }
        catch { }

        var value = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName.Trim()))).ToLowerInvariant();
        try { File.WriteAllText(path, value); } catch { }
        return value;
    }

    private string? GetDeviceToken()
    {
        var candidates = new[]
        {
            Path.Combine(_root, "device.token"),
            Path.Combine(_root, "device-token.txt"),
            Path.Combine(_root, "device-token.dat")
        };

        foreach (var path in candidates)
        {
            try
            {
                if (!File.Exists(path)) continue;
                var token = File.ReadAllText(path).Trim();
                if (token.Length < 20 || !IsValidHeaderValue(token)) continue;
                if (!string.Equals(path, candidates[0], StringComparison.OrdinalIgnoreCase))
                {
                    try { File.WriteAllText(candidates[0], token); } catch { }
                }
                return token;
            }
            catch { }
        }
        return null;
    }

    private void SaveDeviceToken(string token)
    {
        token = token.Trim();
        if (!IsValidHeaderValue(token)) throw new InvalidOperationException("De pc-token bevat ongeldige tekens.");
        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "device.token"), token);
    }

    private static bool IsValidHeaderValue(string? value)
        => !string.IsNullOrEmpty(value) && value.IndexOfAny(new[] { '\r', '\n', '\0' }) < 0;

    private static string GetError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error)) return error.GetString() ?? "Onbekende serverfout.";
            if (doc.RootElement.TryGetProperty("message", out var message)) return message.GetString() ?? "Onbekende serverfout.";
        }
        catch { }
        return string.IsNullOrWhiteSpace(json) ? "Onbekende serverfout." : json;
    }

    private void SetStatus(string text) => Dispatcher.Invoke(() => StatusText.Text = text);

    private static string GetFriendlyError(Exception ex)
    {
        if (ex is HttpRequestException) return "De accountserver is niet bereikbaar. Controleer je internetverbinding en probeer het opnieuw.";
        if (ex is TaskCanceledException) return "De verbinding met de accountserver duurde te lang. Probeer het opnieuw.";
        if (ex is JsonException) return "De accountserver stuurde een ongeldig antwoord. Probeer het opnieuw.";
        if (ex is UnauthorizedAccessException) return "HVMC heeft geen toegang tot de benodigde bestanden. Start de launcher opnieuw of controleer de bestandsrechten.";
        if (ex is IOException) return "HVMC kon een bestand niet lezen of schrijven. Controleer of de launcher toegang heeft tot de map.";
        if (ex is InvalidOperationException) return ex.Message;
        return string.IsNullOrWhiteSpace(ex.Message) ? "Er is een onverwachte fout opgetreden." : ex.Message;
    }

    private void ShowError(string title, Exception ex)
    {
        var dialog = new Window
        {
            Title = title,
            Owner = this,
            Width = 760,
            Height = 520,
            MinWidth = 520,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.CanResize,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(247, 244, 236))
        };

        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 36, 30)),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(heading, 0);
        grid.Children.Add(heading);

        var details = new System.Windows.Controls.TextBlock
        {
            Text = GetFriendlyError(ex),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Top,
            FontSize = 15,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(39, 36, 30))
        };
        Grid.SetRow(details, 1);
        grid.Children.Add(details);

        var close = new System.Windows.Controls.Button
        {
            Content = "OK",
            Width = 110,
            Height = 40,
            Margin = new Thickness(0, 14, 0, 0),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            IsDefault = true
        };
        close.Click += (_, _) => dialog.Close();
        Grid.SetRow(close, 2);
        grid.Children.Add(close);

        dialog.Content = grid;
        dialog.ShowDialog();
    }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record PcRegistrationResponse(string DeviceToken);
    private sealed record LeaseResponse(string LeaseId, string Username, string MinecraftAccessToken, string Uuid, string? Xuid, string AccountName);
    private sealed record GitHubRelease(string? TagName, List<GitHubAsset>? Assets);
    private sealed record GitHubAsset(string? Name, long Size, string? BrowserDownloadUrl);
}
