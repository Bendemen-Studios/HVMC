using CmlLib.Core;
using CmlLib.Core.Auth.Microsoft;
using CmlLib.Core.ProcessBuilder;
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
    private readonly string _mappingPath;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private string? _leaseId;
    private string? _clientId;

    public MainWindow()
    {
        InitializeComponent();
        _mappingPath = Path.Combine(_root, "pool-accounts.json");
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

            SetStatus("Vrij Minecraft-account zoeken...");
            _clientId = GetStableClientId();
            var lease = await AcquireLeaseAsync(_clientId);
            _leaseId = lease.LeaseId;

            SetStatus($"Account {lease.AccountName} reserveren...");
            var identifier = FindLocalAccountIdentifier(lease.Slot, lease.MicrosoftUsername);
            if (identifier is null)
                throw new InvalidOperationException($"Pool-account '{lease.AccountName}' is op deze pc nog niet gekoppeld aan een lokale Microsoft-login. Laat een beheerder deze poolaccount eenmalig op deze pc aanmelden.");

            SetStatus("Microsoft-sessie voorbereiden...");
            var session = await AuthenticateSilentlyAsync(identifier);

            SetStatus("Minecraft voorbereiden...");
            var minecraftPath = new MinecraftPath(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) is { Length: > 0 } app
                ? Path.Combine(app, ".minecraft")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData", "Roaming", ".minecraft"));
            var launcher = new MinecraftLauncher(minecraftPath);
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
            StartLeaseHeartbeat(_clientId, _leaseId);
            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Er is een fout opgetreden.");
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

        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(updater);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("HVMC updater kon niet worden gestart.");
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
        return JsonSerializer.Deserialize<LeaseResponse>(json, JsonOptions) ?? throw new InvalidOperationException("Ongeldige lease-response.");
    }

    private async Task<JELoginHandler> BuildLoginHandlerAsync()
    {
        var loggerFactory = LoggerFactory.Create(config => config.ClearProviders());
        var logger = loggerFactory.CreateLogger("HVMC");
        return new JELoginHandlerBuilder().WithLogger(logger).Build();
    }

    private async Task<CmlLib.Core.Auth.MSession> AuthenticateSilentlyAsync(string identifier)
    {
        var loggerFactory = LoggerFactory.Create(config => config.ClearProviders());
        var logger = loggerFactory.CreateLogger("HVMC");
        var msal = await MsalClientHelper.BuildApplicationWithCache(MicrosoftClientId);
        var handler = new JELoginHandlerBuilder().WithLogger(logger).Build();
        var account = handler.AccountManager.GetAccounts().FirstOrDefault(a => string.Equals(a.Identifier, identifier, StringComparison.Ordinal));
        if (account is null)
            throw new InvalidOperationException("De lokale HVMC-accountcache bevat dit pool-account niet.");

        var authenticator = handler.CreateAuthenticator(account, default);
        authenticator.AddMsalOAuth(msal, oauth => oauth.Silent());
        authenticator.AddXboxAuthForJE(xbox => xbox.Basic());
        authenticator.AddJEAuthenticator();
        return await authenticator.ExecuteForLauncherAsync();
    }

    private string? FindLocalAccountIdentifier(string slot, string? username)
    {
        if (File.Exists(_mappingPath))
        {
            try
            {
                var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(_mappingPath));
                if (map is not null && map.TryGetValue(slot, out var id) && !string.IsNullOrWhiteSpace(id))
                    return id;
            }
            catch { }
        }

        return null;
    }

    private string GetStableClientId()
    {
        var idPath = Path.Combine(_root, "client-id.txt");
        if (File.Exists(idPath))
        {
            var existing = File.ReadAllText(idPath).Trim();
            if (Guid.TryParse(existing, out _)) return existing;
        }
        var created = Guid.NewGuid().ToString();
        File.WriteAllText(idPath, created);
        return created;
    }

    private async void StartLeaseHeartbeat(string clientId, string leaseId)
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
