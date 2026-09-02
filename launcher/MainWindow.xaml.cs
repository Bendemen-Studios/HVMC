using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.Auth.Microsoft.Sessions;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;

namespace HVMCLauncher;

public partial class MainWindow : Window
{
    private const string PoolApi = "https://accounts.hvmc.nl";
    private const string MicrosoftClientId = "7fcdeaa7-ba20-4883-96b0-0b68cff24bb9";
    private const string MinecraftVersion = "1.21.11";
    private const string FabricVersion = "0.18.1";
    private const int MaximumRamMb = 4096;

    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Bendemen", "HVMC");
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private string? _leaseId;
    private string? _clientId;

    public MainWindow()
    {
        InitializeComponent();
        Directory.CreateDirectory(_root);
    }

    private async void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        PlayButton.IsEnabled = false;
        ExitButton.IsEnabled = false;
        try
        {
            SetStatus("Updates controleren...");
            await RunUpdaterAsync();

            _clientId = GetStableClientId();
            SetStatus("Vrij Minecraft-account zoeken...");
            var lease = await AcquireLeaseAsync(_clientId);
            _leaseId = lease.LeaseId;

            SetStatus($"{lease.AccountName} wordt klaargemaakt...");
            var handler = BuildLoginHandler();
            var account = FindLocalAccount(handler, lease.MicrosoftUsername);
            if (account is null)
            {
                throw new InvalidOperationException(
                    $"Het pool-account '{lease.AccountName}' is nog niet lokaal aangemeld op deze pc. " +
                    "Laat een beheerder dit Microsoft-account één keer aanmelden op deze pc.");
            }

            SetStatus("Microsoft-sessie voorbereiden...");
            var session = await AuthenticateSilentlyAsync(handler, account);

            var minecraftPath = new MinecraftPath(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"));
            var launcher = new MinecraftLauncher(minecraftPath);
            SetStatus("Minecraft voorbereiden...");
            await launcher.InstallAsync(MinecraftVersion);

            var fabricProfile = $"fabric-loader-{FabricVersion}-{MinecraftVersion}";
            SetStatus("Minecraft starten...");
            var process = await launcher.InstallAndBuildProcessAsync(fabricProfile, new MLaunchOption
            {
                Session = session,
                MaximumRamMb = MaximumRamMb
            });

            process.Start();
            SetStatus("Minecraft draait.");
            _ = StartLeaseHeartbeatAsync(_clientId, _leaseId);
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Starten mislukt.");
            MessageBox.Show(this, ex.Message, "HVMC", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            await ReleaseLeaseSafeAsync();
            PlayButton.IsEnabled = true;
            ExitButton.IsEnabled = true;
            SetStatus("Klaar om te spelen.");
        }
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e) => Close();

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
            ?? throw new InvalidOperationException("Ongeldige lease-response.");
    }

    private JELoginHandler BuildLoginHandler()
    {
        var loggerFactory = LoggerFactory.Create(config => config.ClearProviders());
        return new JELoginHandlerBuilder()
            .WithLogger(loggerFactory.CreateLogger("HVMC"))
            .Build();
    }

    private static JEGameAccount? FindLocalAccount(JELoginHandler handler, string? username)
    {
        if (string.IsNullOrWhiteSpace(username)) return null;
        var wanted = username.Trim();
        foreach (var item in handler.AccountManager.GetAccounts())
        {
            if (item is JEGameAccount je &&
                string.Equals(je.Profile?.Username, wanted, StringComparison.OrdinalIgnoreCase))
                return je;
        }
        return null;
    }

    private static async Task<MSession> AuthenticateSilentlyAsync(JELoginHandler handler, JEGameAccount account)
    {
        var loggerFactory = LoggerFactory.Create(config => config.ClearProviders());
        var msal = await MsalClientHelper.BuildApplicationWithCache(MicrosoftClientId);
        var authenticator = handler.CreateAuthenticator(account, default);
        authenticator.AddMsalOAuth(msal, oauth => oauth.Silent());
        authenticator.AddXboxAuthForJE(xbox => xbox.Basic());
        authenticator.AddJEAuthenticator();
        return await authenticator.ExecuteForLauncherAsync();
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

    private async Task StartLeaseHeartbeatAsync(string clientId, string leaseId)
    {
        try
        {
            while (_leaseId == leaseId)
            {
                await Task.Delay(TimeSpan.FromMinutes(5));
                if (_leaseId != leaseId) break;
                using var content = new StringContent(JsonSerializer.Serialize(new { clientId, leaseId }), Encoding.UTF8, "application/json");
                await _http.PostAsync($"{PoolApi}/v1/launcher/lease/heartbeat", content);
            }
        }
        catch { }
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

    private void SetStatus(string message) => StatusText.Text = message;

    private static string GetError(string json)
    {
        try { return JsonSerializer.Deserialize<ApiError>(json, JsonOptions)?.Error ?? json; }
        catch { return json; }
    }

    private sealed record LeaseResponse(string LeaseId, string Slot, string AccountName, string? MicrosoftUsername, string ExpiresAt);
    private sealed record ApiError(string Error);
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}
