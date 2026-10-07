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
    private sealed class ResilientHttpHandler : DelegatingHandler
    {
        private readonly string _logPath;

        public ResilientHttpHandler(string logPath)
        {
            _logPath = logPath;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Exception? lastException = null;
            var method = request.Method.Method;
            var url = request.RequestUri?.ToString() ?? "<onbekende URL>";

            for (var attempt = 1; attempt <= 4; attempt++)
            {
                try
                {
                    WriteLog($"HTTP download poging {attempt}/4: {method} {url}");
                    var response = await base.SendAsync(request, cancellationToken);
                    WriteLog($"HTTP response poging {attempt}/4: {(int)response.StatusCode} {response.ReasonPhrase} — {method} {url}");

                    if ((int)response.StatusCode >= 500 && attempt < 4)
                    {
                        WriteLog($"HTTP serverfout {(int)response.StatusCode}; opnieuw proberen over {attempt} sec.");
                        response.Dispose();
                        await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
                        continue;
                    }

                    return response;
                }
                catch (HttpRequestException ex) when (attempt < 4)
                {
                    lastException = ex;
                    WriteLog($"HTTP exception poging {attempt}/4 — {method} {url}: {ex}");
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
                }
                catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested && attempt < 4)
                {
                    lastException = ex;
                    WriteLog($"HTTP timeout poging {attempt}/4 — {method} {url}: {ex}");
                    await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
                }
            }

            WriteLog($"HTTP download definitief mislukt — {method} {url}: {lastException}");
            throw lastException ?? new HttpRequestException("Minecraft-download mislukt.");
        }

        private void WriteLog(string message)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!);
                File.AppendAllText(
                    _logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Logging must never break Minecraft downloading.
            }
        }
    }

    private const string PoolApi = "https://accounts.hvmc.nl";
    private const string MinecraftVersion = "26.2";
    private const string FabricVersion = "0.19.3";
    private static readonly string LauncherVersion = App.AppVersion;
    private const int MaximumRamMb = 4096;
    private const int PcHeartbeatSeconds = 5;
    private const int LeaseHeartbeatSeconds = 5;

    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bendemen", "HVMC");
    private readonly string _launcherLogPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bendemen", "HVMC", "launcher.log");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private readonly HttpClient _minecraftHttp = CreateMinecraftHttpClient();
    private string? _clientId;
    private string? _deviceToken;
    private string? _leaseId;
    private CancellationTokenSource? _leaseHeartbeatCts;
    private CancellationTokenSource? _pcHeartbeatCts;
    private bool _contentUpdateFailed;
    private bool _deviceBlocked;
    private bool _playInProgress;
    private bool _launcherUpdateScheduled;
    private Process? _minecraftProcess;

    public MainWindow()
    {
        InitializeComponent();

        // The release workflow uses major.minor versions (for example 3.57).
        // App.AppVersion is the assembly version (for example 3.57.0), so show
        // the actual release number without the implicit .0 build component.
        if (Version.TryParse(App.AppVersion, out var releaseVersion))
            VersionText.Text = $"Versie: v{releaseVersion.Major}.{releaseVersion.Minor}";
        else
            VersionText.Text = $"Versie: v{App.AppVersion}";

        Directory.CreateDirectory(_root);
        WriteLauncherLog("Launcher gestart.");
        Loaded += MainWindow_Loaded;
        Closed += (_, _) =>
        {
            _leaseHeartbeatCts?.Cancel();
            _pcHeartbeatCts?.Cancel();
            _http.Dispose();
            _minecraftHttp.Dispose();
        };
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        // Never make the first visible launcher frame wait for network traffic.
        // The launcher can safely open immediately; PlayButton performs the
        // authoritative authorization check again before Minecraft starts.
        _clientId = GetStableClientId();
        _deviceToken = GetDeviceToken();

        ExitButton.IsEnabled = true;
        HideLoading();

        if (string.IsNullOrWhiteSpace(_deviceToken))
        {
            AuthorizationPanel.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = false;
            SetStatus("Deze pc moet eenmalig worden geautoriseerd.");
        }
        else
        {
            AuthorizationPanel.Visibility = Visibility.Collapsed;
            PlayButton.IsEnabled = true;
            SetStatus("Klaar om te spelen.");
        }

        WriteLauncherLog($"Launcher versie {LauncherVersion}, Minecraft {MinecraftVersion}, Fabric {FabricVersion}.");

        // Network checks continue after the UI is usable. Launcher update
        // checks are deliberately delayed and cached so a normal startup does
        // not wait on GitHub at all.
        _ = InitializeStartupChecksAsync();
    }

    private async Task InitializeStartupChecksAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_deviceToken))
            {
                var authorized = await EnsurePcAuthorizedAsync();
                if (!authorized)
                {
                    PlayButton.IsEnabled = false;
                    return;
                }

                await SendPcHeartbeatAsync();
                StartPcHeartbeat();
                SetStatus("Klaar om te spelen.");
                PlayButton.IsEnabled = true;
            }

            // Do not make launcher startup depend on GitHub. Check for a new
            // launcher only after the window is responsive and only once per
            // cache period. The Play button has its own authoritative checks.
            if (ShouldCheckLauncherUpdate())
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (_playInProgress || !ShouldCheckLauncherUpdate())
                    return;

                var updated = await CheckForLauncherUpdateAsync();
                if (!updated)
                    SaveLauncherUpdateCheck();
            }
        }
        catch (DeviceBlockedException)
        {
            await HandleDeviceBlockedAsync();
        }
        catch (Exception ex)
        {
            // Startup maintenance must never turn a working launcher into a
            // startup error. The Play flow performs the required checks again.
            WriteLauncherLog($"Achtergrond-startupcontrole mislukt: {ex.Message}");
            if (AuthorizationPanel.Visibility != Visibility.Visible)
            {
                SetStatus("Klaar om te spelen.");
                PlayButton.IsEnabled = true;
            }
        }
    }

    private bool ShouldCheckLauncherUpdate()
    {
        try
        {
            var path = Path.Combine(_root, "launcher-update-check.txt");
            if (!File.Exists(path))
                return true;

            var text = File.ReadAllText(path).Trim();
            if (!DateTimeOffset.TryParse(text, out var lastCheck))
                return true;

            return DateTimeOffset.UtcNow - lastCheck >= TimeSpan.FromHours(6);
        }
        catch
        {
            return true;
        }
    }

    private void SaveLauncherUpdateCheck()
    {
        try
        {
            Directory.CreateDirectory(_root);
            File.WriteAllText(
                Path.Combine(_root, "launcher-update-check.txt"),
                DateTimeOffset.UtcNow.ToString("O"));
        }
        catch
        {
            // A failed cache write must never affect launcher startup.
        }
    }

    private async Task<bool> EnsurePcAuthorizedAsync()
    {
        if (string.IsNullOrWhiteSpace(_clientId)) return false;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{PoolApi}/v1/launcher/pc/status?clientId={Uri.EscapeDataString(_clientId)}");
        if (!string.IsNullOrWhiteSpace(_deviceToken)) request.Headers.Add("x-hvmc-device-token", _deviceToken);
        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();
        if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(_deviceToken)) return true;
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) throw new DeviceBlockedException();
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
        if (_playInProgress || _launcherUpdateScheduled)
            return;

        _playInProgress = true;
        PlayButton.IsEnabled = false;
        ExitButton.IsEnabled = false;
        try
        {
            // Never allow a second launch while the previous Minecraft process
            // is still attached to the launcher. Process.HasExited only reflects
            // the associated process, so we explicitly close/dispose the handle
            // before creating a fresh CmlLib process.
            await EnsurePreviousMinecraftProcessStoppedAsync();

            if (!await EnsurePcAuthorizedAsync()) { ExitButton.IsEnabled = true; return; }
            var minecraftPath = new MinecraftPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"));
            _contentUpdateFailed = false;
            SetStatus("HVMC content synchroniseren...");
            WriteLauncherLog("HVMC content-updater starten.");
            await RunUpdaterAsync();
            WriteLauncherLog("HVMC content-updater voltooid.");
            SetStatus("Minecraft voorbereiden...");
            WriteLauncherLog("Minecraft-bestanden controleren/downloaden.");
            var minecraftLauncher = new MinecraftLauncher(
                MinecraftLauncherParameters.CreateDefault(minecraftPath, _minecraftHttp));
            await InstallMinecraftWithRetryAsync(minecraftLauncher);
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
            _minecraftProcess = process;
            var launchLogPath = Path.Combine(_root, "minecraft-launch.log");
            Directory.CreateDirectory(_root);
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.RedirectStandardOutput = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
            process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
            process.EnableRaisingEvents = true;

            using var launchLog = new StreamWriter(launchLogPath, append: false, Encoding.UTF8);
            process.OutputDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    try { launchLog.WriteLine(args.Data); launchLog.Flush(); } catch { }
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (args.Data is not null)
                    try { launchLog.WriteLine(args.Data); launchLog.Flush(); } catch { }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            HideLoading();

            // Minecraft is now actually running. Keep the play button visibly
            // disabled so users cannot mistake an active session for another
            // launch action.
            PlayButton.Content = "MINECRAFT DRAAIT";
            PlayButton.IsEnabled = false;
            SetStatus("Minecraft draait.");
            await SendLeaseHeartbeatAsync(_clientId, _deviceToken, _leaseId);
            StartLeaseHeartbeat(_clientId, _deviceToken, _leaseId);
            await process.WaitForExitAsync();
            var exitCode = process.ExitCode;

            await WaitForMinecraftShutdownAsync(process);

            if (exitCode != 0)
                throw new InvalidOperationException(
                    $"Minecraft is direct afgesloten (exitcode {exitCode}). Bekijk het logbestand: {launchLogPath}");

            process.Close();
            _minecraftProcess = null;
        }
        catch (DeviceBlockedException)
        {
            await HandleDeviceBlockedAsync();
        }
        catch (Exception ex)
        {
            WriteLauncherLog($"Fout tijdens Minecraft-start: {ex}");
            if (!_deviceBlocked)
            {
                SetStatus("Minecraft is gestopt of kon niet starten.");
                ShowError("Minecraft starten mislukt", ex);
            }
        }
        finally
        {
            HideLoading();
            // Minecraft has returned to the launcher (normally or after a crash).
            // Always stop the heartbeat, release the account and dispose the
            // Process object before allowing another launch.
            _leaseHeartbeatCts?.Cancel();
            await ReleaseLeaseSafeAsync();
            await EnsurePreviousMinecraftProcessStoppedAsync();
            _playInProgress = false;
            PlayButton.Content = "SPELEN";
            PlayButton.IsEnabled = !AuthorizationPanel.IsVisible;
            ExitButton.IsEnabled = true;
            // Do not overwrite an error status with "Klaar om te spelen".
            if (AuthorizationPanel.Visibility != Visibility.Visible && !_deviceBlocked
                && !StatusText.Text.StartsWith("Minecraft is gestopt", StringComparison.OrdinalIgnoreCase))
                SetStatus("Klaar om te spelen.");
        }
    }

    private async Task EnsurePreviousMinecraftProcessStoppedAsync()
    {
        var process = _minecraftProcess;
        if (process is null)
            return;

        try
        {
            if (!process.HasExited)
            {
                SetStatus("Vorige Minecraft-sessie wordt afgesloten...");
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
        catch (TimeoutException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
            }
            catch { }

            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
        }
        catch (InvalidOperationException)
        {
            // The Process object is no longer associated with a live process.
        }
        catch { }
        finally
        {
            try { process.Close(); } catch { }
            if (ReferenceEquals(_minecraftProcess, process))
                _minecraftProcess = null;
        }
    }

    private static async Task WaitForMinecraftShutdownAsync(Process process)
    {
        // HasExited/WaitForExit only tracks the associated process, not its
        // descendants. Give the Java process tree a small grace period to settle
        // before the launcher becomes launchable again.
        for (var i = 0; i < 20; i++)
        {
            try
            {
                if (process.HasExited)
                    return;
            }
            catch (InvalidOperationException)
            {
                return;
            }

            await Task.Delay(250);
        }
    }

    private async Task InstallMinecraftWithRetryAsync(MinecraftLauncher launcher)
    {
        Exception? lastException = null;
        var logPath = Path.Combine(_root, "minecraft-download.log");

        WriteMinecraftDownloadLog($"=== Minecraft download/install gestart: {MinecraftVersion} ===");

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                SetLoadingText(attempt == 1
                    ? "Minecraft-bestanden controleren..."
                    : $"Minecraft-download opnieuw proberen ({attempt}/3)...");
                SetStatus(attempt == 1
                    ? "Minecraft voorbereiden..."
                    : $"Minecraft-download opnieuw proberen ({attempt}/3)...");

                WriteMinecraftDownloadLog($"InstallAsync poging {attempt}/3 gestart.");
                await launcher.InstallAsync(MinecraftVersion);
                WriteMinecraftDownloadLog($"InstallAsync poging {attempt}/3 succesvol afgerond.");
                WriteLauncherLog("Minecraft-bestanden zijn succesvol gecontroleerd/gedownload.");
                return;
            }
            catch (Exception ex)
            {
                lastException = ex;
                WriteMinecraftDownloadLog($"InstallAsync poging {attempt}/3 mislukt.");
                WriteMinecraftDownloadLog($"Exception: {ex}");
                WriteMinecraftDownloadLog($"Base exception: {ex.GetBaseException()}");

                if (attempt < 3)
                {
                    WriteMinecraftDownloadLog($"Nieuwe volledige installatiepoging over {attempt * 2} seconden.");
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2));
                }
            }
        }

        WriteMinecraftDownloadLog("=== Minecraft download/install definitief mislukt ===");
        WriteLauncherLog($"Minecraft-download definitief mislukt. Zie {logPath}");

        throw new InvalidOperationException(
            $"Minecraft kon niet worden gedownload. Details staan in: {logPath}",
            lastException);
    }

    private static HttpClient CreateMinecraftHttpClient()
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Bendemen",
            "HVMC",
            "minecraft-download.log");

        var handler = new ResilientHttpHandler(logPath)
        {
            InnerHandler = new HttpClientHandler()
        };

        return new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(180)
        };
    }

    private void WriteLauncherLog(string message)
    {
        try
        {
            Directory.CreateDirectory(_root);
            File.AppendAllText(
                _launcherLogPath,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break the launcher.
        }
    }

    private void WriteMinecraftDownloadLog(string message)
    {
        try
        {
            Directory.CreateDirectory(_root);
            File.AppendAllText(
                Path.Combine(_root, "minecraft-download.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never break Minecraft downloading.
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
                catch (DeviceBlockedException)
                {
                    await HandleDeviceBlockedAsync();
                    break;
                }
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
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || IsBlockedResponse(json))
            throw new DeviceBlockedException();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(GetError(json));
    }

    private static bool IsBlockedResponse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        if (json.Contains("geblokkeerd", StringComparison.OrdinalIgnoreCase)
            || json.Contains("blocked", StringComparison.OrdinalIgnoreCase)
            || json.Contains("device_blocked", StringComparison.OrdinalIgnoreCase)
            || json.Contains("deviceBlocked", StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            using var document = JsonDocument.Parse(json);
            return ContainsBlockedValue(document.RootElement);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool ContainsBlockedValue(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.Name.Equals("blocked", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("deviceBlocked", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("device_blocked", StringComparison.OrdinalIgnoreCase)
                    || property.Name.Equals("isBlocked", StringComparison.OrdinalIgnoreCase))
                {
                    if (property.Value.ValueKind == JsonValueKind.True)
                        return true;
                    if (property.Value.ValueKind == JsonValueKind.String
                        && property.Value.GetString()?.Equals("true", StringComparison.OrdinalIgnoreCase) == true)
                        return true;
                }

                if (ContainsBlockedValue(property.Value)) return true;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                if (ContainsBlockedValue(item)) return true;
        }

        return false;
    }

    private async Task HandleDeviceBlockedAsync()
    {
        if (_deviceBlocked) return;
        _deviceBlocked = true;
        _leaseHeartbeatCts?.Cancel();
        _pcHeartbeatCts?.Cancel();

        try
        {
            var process = _minecraftProcess;
            if (process is not null && !process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); }
                catch { try { process.Kill(); } catch { } }
                try { await process.WaitForExitAsync(); } catch { }
            }
        }
        catch { }
        finally
        {
            _minecraftProcess = null;
            await Dispatcher.InvokeAsync(() =>
            {
                System.Windows.MessageBox.Show(
                    this,
                    "Je bent geblokkeerd, Neem contact op met info@bendemen.nl voor meer informatie.",
                    "HVMC",
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                Close();
            });
        }
    }

    private void ShowLoading(string message)
    {
        Dispatcher.Invoke(() =>
        {
            LoadingText.Text = message;
            LoadingOverlay.Visibility = Visibility.Visible;
            PlayButton.IsEnabled = false;
        });
    }

    private void SetLoadingText(string message)
    {
        Dispatcher.Invoke(() => LoadingText.Text = message);
    }

    private void HideLoading()
    {
        Dispatcher.Invoke(() => LoadingOverlay.Visibility = Visibility.Collapsed);
    }

    private async Task<bool> CheckForLauncherUpdateAsync()
    {
        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe)) return false;

        if (!Version.TryParse(LauncherVersion, out var currentVersion))
            return false;

        // Do NOT use api.github.com here. This code runs on every installed
        // launcher and unauthenticated GitHub REST API calls are rate-limited.
        // A 403 from the API must never make the launcher update dialog fail.
        //
        // version.txt is the release source of truth in this repository. The
        // release workflow creates the matching vX.Y tag/release and publishes
        // HVMC.exe under the predictable GitHub release-download URL.
        Version? remoteVersion = null;
        try
        {
            using var versionResponse = await _http.GetAsync(
                "https://raw.githubusercontent.com/Bendemen-Studios/HVMC/main/version.txt");
            if (!versionResponse.IsSuccessStatusCode)
            {
                WriteLauncherLog($"Launcher releasecontrole overgeslagen: version.txt gaf HTTP {(int)versionResponse.StatusCode}.");
                return false;
            }

            var remoteText = (await versionResponse.Content.ReadAsStringAsync()).Trim();
            if (!Version.TryParse(remoteText, out var parsedRemoteVersion))
            {
                WriteLauncherLog($"Launcher releasecontrole overgeslagen: ongeldige remote versie '{remoteText}'.");
                return false;
            }

            remoteVersion = parsedRemoteVersion;
        }
        catch (Exception ex)
        {
            // An unavailable release check must never block an otherwise
            // working launcher. The normal content updater has its own check.
            WriteLauncherLog($"Launcher releasecontrole kon niet worden uitgevoerd: {ex.Message}");
            return false;
        }

        if (remoteVersion <= currentVersion)
            return false;

        var tag = $"v{remoteVersion}";
        var assetUrl = $"https://github.com/Bendemen-Studios/HVMC/releases/download/{tag}/HVMC.exe";

        SetLoadingText($"Nieuwe HVMC-versie {tag} gevonden. Update wordt gedownload...");
        SetStatus($"HVMC {tag} wordt automatisch bijgewerkt...");

        var temp = Path.Combine(_root, $"HVMCLauncher-update-{Guid.NewGuid():N}.exe");
        try
        {
            using (var dl = await _http.GetAsync(assetUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                // The release workflow can bump version.txt before GitHub has
                // finished publishing the matching release. Treat that short
                // window as "update not ready yet" instead of showing an error.
                if (dl.StatusCode == System.Net.HttpStatusCode.NotFound)
                {
                    WriteLauncherLog($"HVMC {tag} staat nog niet als releasebestand klaar. Update wordt bij de volgende start opnieuw gecontroleerd.");
                    return false;
                }

                if (!dl.IsSuccessStatusCode)
                {
                    WriteLauncherLog($"HVMC release-download gaf HTTP {(int)dl.StatusCode} {dl.ReasonPhrase}.");
                    return false;
                }

                await using var source = await dl.Content.ReadAsStreamAsync();
                await using var target = File.Create(temp);
                await source.CopyToAsync(target);
            }

            var downloaded = new FileInfo(temp);
            if (downloaded.Length < 1_000_000)
                throw new InvalidOperationException("De gedownloade HVMC-launcher lijkt ongeldig of te klein.");

            SetStatus($"HVMC {tag} is gedownload. Launcher wordt stil bijgewerkt...");
            if (_playInProgress)
            {
                try { File.Delete(temp); } catch { }
                WriteLauncherLog("Launcher-update overgeslagen omdat Minecraft al wordt gestart.");
                return false;
            }

            // From this point the current process must only perform the
            // launcher replacement/restart. It must never continue into the
            // Minecraft launch flow.
            _launcherUpdateScheduled = true;
            PlayButton.IsEnabled = false;
            ExitButton.IsEnabled = false;
            SetStatus($"HVMC {tag} wordt opnieuw gestart...");
            ScheduleSilentLauncherReplacement(temp, currentExe);
            return true;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            WriteLauncherLog($"Launcher-update van {tag} mislukt: {ex.Message}");
            throw new InvalidOperationException($"De nieuwe HVMC-launcher kon niet worden gedownload: {ex.Message}", ex);
        }
    }

    private static void ScheduleSilentLauncherReplacement(string downloadedExe, string currentExe)
    {
        var pid = Environment.ProcessId;
        var root = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;
        var scriptPath = Path.Combine(root, $"HVMC-launcher-replace-{Guid.NewGuid():N}.ps1");
        var backupPath = currentExe + ".update-backup";

        static string Ps(string value) =>
            "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

        // Use a real .ps1 file so Windows paths with spaces cannot break
        // PowerShell command-line quoting during a self-update.
        var script = string.Join(Environment.NewLine, new[]
        {
            "$ErrorActionPreference = 'Stop'",
            "$replaced = $false",
            "try {",
            "  Start-Sleep -Milliseconds 1000",
            "  $deadline = (Get-Date).AddSeconds(30)",
            "  while ((Get-Date) -lt $deadline -and (Get-Process -Id $LauncherPid -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 250 }",
            "  if (Get-Process -Id $LauncherPid -ErrorAction SilentlyContinue) { throw 'De oude HVMC-launcher is na 30 seconden nog actief.' }",
            "  $deadline = (Get-Date).AddSeconds(30)",
            "  while ((Get-Date) -lt $deadline -and -not $replaced) {",
            "    try {",
            "      if (Test-Path -LiteralPath $BackupPath) { Remove-Item -LiteralPath $BackupPath -Force -ErrorAction Stop }",
            "      if (Test-Path -LiteralPath $TargetPath) { Move-Item -LiteralPath $TargetPath -Destination $BackupPath -Force -ErrorAction Stop }",
            "      Move-Item -LiteralPath $SourcePath -Destination $TargetPath -Force -ErrorAction Stop",
            "      if (-not (Test-Path -LiteralPath $TargetPath)) { throw 'Nieuwe HVMC.exe is na vervangen niet gevonden.' }",
            "      $replaced = $true",
            "    } catch {",
            "      try { if (-not (Test-Path -LiteralPath $TargetPath) -and (Test-Path -LiteralPath $BackupPath)) { Move-Item -LiteralPath $BackupPath -Destination $TargetPath -Force -ErrorAction SilentlyContinue } } catch {}",
            "      Start-Sleep -Milliseconds 500",
            "    }",
            "  }",
            "  if (-not $replaced) { throw 'HVMC.exe kon niet binnen 30 seconden worden vervangen.' }",
            "  Remove-Item -LiteralPath $BackupPath -Force -ErrorAction SilentlyContinue",
            "  Start-Process -FilePath $TargetPath",
            "} catch {",
            "  try { if (-not (Test-Path -LiteralPath $TargetPath) -and (Test-Path -LiteralPath $BackupPath)) { Move-Item -LiteralPath $BackupPath -Destination $TargetPath -Force -ErrorAction SilentlyContinue } } catch {}",
            "  try { if (Test-Path -LiteralPath $TargetPath) { Start-Process -FilePath $TargetPath } } catch {}",
            "} finally {",
            "  try { if (Test-Path -LiteralPath $SourcePath) { Remove-Item -LiteralPath $SourcePath -Force -ErrorAction SilentlyContinue } } catch {}",
            "  Start-Sleep -Milliseconds 300",
            "  try { Remove-Item -LiteralPath $ScriptPath -Force -ErrorAction SilentlyContinue } catch {}",
            "}"
        });

        File.WriteAllText(scriptPath,
            "$LauncherPid = " + pid + Environment.NewLine +
            "$SourcePath = " + Ps(downloadedExe) + Environment.NewLine +
            "$TargetPath = " + Ps(currentExe) + Environment.NewLine +
            "$BackupPath = " + Ps(backupPath) + Environment.NewLine +
            "$ScriptPath = " + Ps(scriptPath) + Environment.NewLine + script,
            new UTF8Encoding(false));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            ArgumentList =
            {
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy", "Bypass",
                "-File", scriptPath
            }
        });

        // The helper owns the replacement from this point. The old launcher
        // must exit before HVMC.exe can be renamed/replaced.
        Environment.Exit(0);
    }
    private async Task RunUpdaterAsync(bool forceRedownload = false)
    {
        ShowLoading(forceRedownload
            ? "HVMC-bestanden opnieuw downloaden..."
            : "HVMC-bestanden controleren en bijwerken...");
        var updater = Path.Combine(_root, "HVMCUpdater.ps1");

        // Always prefer the current updater from GitHub. The updater is also
        // embedded in the launcher as an offline fallback, but an older
        // installed launcher must not keep executing a broken/stale updater
        // forever. This is intentionally done before PowerShell is started.
        var remoteUpdaterUrl = "https://raw.githubusercontent.com/Bendemen-Studios/HVMC/main/HVMCUpdater.ps1";
        var updaterRefreshed = false;
        try
        {
            using var response = await _http.GetAsync(remoteUpdaterUrl);
            response.EnsureSuccessStatusCode();
            var remoteUpdater = await response.Content.ReadAsStringAsync();

            if (remoteUpdater.Contains("param([switch]$ForceRedownload", StringComparison.Ordinal) &&
                remoteUpdater.Contains("HVMC updater afgerond.", StringComparison.Ordinal) &&
                !remoteUpdater.Contains("Update-LauncherIfNeeded", StringComparison.Ordinal))
            {
                await File.WriteAllTextAsync(updater, remoteUpdater, new UTF8Encoding(false));
                updaterRefreshed = true;
                WriteLauncherLog("Actuele HVMCUpdater.ps1 vanaf GitHub opgehaald.");
            }
            else
            {
                WriteLauncherLog("GitHub updater werd geweigerd omdat de inhoud niet herkenbaar geldig is.");
            }
        }
        catch (Exception ex)
        {
            WriteLauncherLog($"Actuele HVMCUpdater.ps1 kon niet worden opgehaald; ingebouwde updater wordt gebruikt: {ex.Message}");
        }

        if (!updaterRefreshed)
        {
            var assembly = System.Reflection.Assembly.GetExecutingAssembly();
            var resourceName = assembly.GetManifestResourceNames().FirstOrDefault(x => x.EndsWith("HVMCUpdater.ps1", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(resourceName)) throw new InvalidOperationException("De ingebouwde HVMC updater ontbreekt in deze launcher-build.");
            await using (var resource = assembly.GetManifestResourceStream(resourceName) ?? throw new InvalidOperationException("De ingebouwde HVMC updater kon niet worden geopend."))
            await using (var target = File.Create(updater))
            {
                await resource.CopyToAsync(target);
            }
            WriteLauncherLog("Ingebouwde HVMCUpdater.ps1 gebruikt.");
        }
        var startInfo = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            ArgumentList =
            {
                "-NoProfile",
                "-NonInteractive",
                "-ExecutionPolicy", "Bypass",
                "-File", updater
            }
        };
        if (forceRedownload)
            startInfo.ArgumentList.Add("-ForceRedownload");

        using var p = Process.Start(startInfo)
            ?? throw new InvalidOperationException("HVMC updater kon niet worden gestart.");
        var stdoutTask = p.StandardOutput.ReadToEndAsync();
        var stderrTask = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (p.ExitCode == 10)
        {
            // The updater has downloaded a newer launcher and scheduled the
            // replacement process. This launcher must exit now so the updater
            // can replace HVMC.exe and start the new version.
            SetStatus("Nieuwe launcher gedownload. Launcher wordt bijgewerkt...");
            Environment.Exit(0);
            return;
        }

        if (p.ExitCode == 2)
        {
            // Explicit exception: the updater itself could not reach GitHub
            // before a sync started. Existing local content may be used.
            SetStatus("Updater is offline. Bestaande versie wordt gebruikt.");
            return;
        }

        if (p.ExitCode != 0)
        {
            _contentUpdateFailed = true;
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
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && IsBlockedResponse(json))
            throw new DeviceBlockedException();
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
                catch (DeviceBlockedException)
                {
                    await HandleDeviceBlockedAsync();
                    break;
                }
                catch { }
            }
        }, token);
    }

    private async Task ReleaseLeaseSafeAsync()
    {
        if (string.IsNullOrWhiteSpace(_leaseId) || string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_deviceToken)) return;

        var leaseId = _leaseId;
        var clientId = _clientId;
        var deviceToken = _deviceToken;

        // Clear the local lease immediately so multiple cleanup paths cannot
        // accidentally release the same lease more than once.
        _leaseId = null;
        _leaseHeartbeatCts?.Cancel();

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{PoolApi}/v1/launcher/lease/release");
                request.Headers.Add("x-hvmc-client-id", clientId);
                request.Headers.Add("x-hvmc-device-token", deviceToken);
                request.Content = new StringContent(
                    JsonSerializer.Serialize(new { clientId, leaseId }),
                    Encoding.UTF8,
                    "application/json");

                using var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode)
                    return;

                var body = await response.Content.ReadAsStringAsync();
                if (attempt == 3)
                    SetStatus($"Account vrijgeven mislukt (HTTP {(int)response.StatusCode}).");
            }
            catch
            {
                if (attempt == 3)
                    SetStatus("Account kon niet automatisch worden vrijgegeven.");
            }

            if (attempt < 3)
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt));
        }
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

    private async Task RetryContentDownloadAsync()
    {
        PlayButton.IsEnabled = false;
        ExitButton.IsEnabled = false;
        _contentUpdateFailed = false;
        try
        {
            SetStatus("HVMC content opnieuw downloaden...");
            await RunUpdaterAsync(forceRedownload: true);
            SetStatus("HVMC content is bijgewerkt. Klaar om te spelen.");
            PlayButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _contentUpdateFailed = true;
            SetStatus("Opnieuw downloaden mislukt.");
            ShowError("Opnieuw downloaden mislukt", ex);
        }
        finally
        {
            ExitButton.IsEnabled = true;
            if (!PlayButton.IsEnabled && !_contentUpdateFailed)
                PlayButton.IsEnabled = true;
        }
    }

    private void ShowError(string title, Exception ex)
    {
        var message = GetFriendlyError(ex);

        if (_contentUpdateFailed)
        {
            var retry = System.Windows.MessageBox.Show(
                this,
                "HVMC kon de benodigde bestanden niet bijwerken.\n\nWil je het opnieuw proberen?",
                title,
                MessageBoxButton.YesNo,
                MessageBoxImage.Error);

            if (retry == MessageBoxResult.Yes)
            {
                _ = RetryContentDownloadAsync();
            }

            return;
        }

        System.Windows.MessageBox.Show(
            this,
            message,
            title,
            MessageBoxButton.OK,
            MessageBoxImage.Error);
    }

    private sealed class DeviceBlockedException : Exception { }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private sealed record PcRegistrationResponse(string DeviceToken);
    private sealed record LeaseResponse(string LeaseId, string Username, string MinecraftAccessToken, string Uuid, string? Xuid, string AccountName);
}
