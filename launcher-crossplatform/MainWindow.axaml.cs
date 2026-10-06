using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Interactivity;

namespace HVMCLauncher.CrossPlatform;

public partial class MainWindow : Window
{
    private const string PoolApi = "https://accounts.hvmc.nl";
    private const int PcHeartbeatSeconds = 5;

    private readonly string _root;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(45) };
    private string? _clientId;
    private string? _deviceToken;
    private CancellationTokenSource? _heartbeatCts;

    public MainWindow()
    {
        InitializeComponent();

        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Bendemen", "HVMC");
        Directory.CreateDirectory(_root);

        VersionText.Text = $"Versie: v{App.AppVersion}";
        _clientId = GetStableClientId();
        _deviceToken = GetDeviceToken();

        Opened += async (_, _) => await InitializeAsync();
        Closed += (_, _) =>
        {
            _heartbeatCts?.Cancel();
            _http.Dispose();
        };
    }

    private async Task InitializeAsync()
    {
        if (string.IsNullOrWhiteSpace(_deviceToken))
        {
            AuthorizationPanel.IsVisible = true;
            PlayButton.IsEnabled = false;
            StatusText.Text = "Deze pc moet eenmalig worden geautoriseerd.";
            return;
        }

        try
        {
            if (await EnsurePcAuthorizedAsync())
            {
                AuthorizationPanel.IsVisible = false;
                PlayButton.IsEnabled = true;
                StatusText.Text = "Klaar om te spelen.";
                StartPcHeartbeat();
            }
        }
        catch (Exception ex)
        {
            PlayButton.IsEnabled = false;
            StatusText.Text = $"Accountserver niet bereikbaar: {ex.Message}";
        }
    }

    private async void AuthorizeButton_Click(object? sender, RoutedEventArgs e)
    {
        AuthorizeButton.IsEnabled = false;
        try
        {
            var code = AuthorizationCodeBox.Text?.Trim().ToUpperInvariant() ?? string.Empty;
            if (code.Length != 10)
                throw new InvalidOperationException("Vul de 10-karakter HVMC pc-autorisatiecode in.");

            _clientId ??= GetStableClientId();

            using var content = new StringContent(
                JsonSerializer.Serialize(new { clientId = _clientId, code, name = Environment.MachineName }),
                Encoding.UTF8, "application/json");

            using var response = await _http.PostAsync($"{PoolApi}/v1/launcher/pc/register", content);
            var json = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(GetError(json));

            using var document = JsonDocument.Parse(json);
            if (!document.RootElement.TryGetProperty("deviceToken", out var tokenElement))
                throw new InvalidOperationException("De server gaf geen pc-token terug.");

            var token = tokenElement.GetString()?.Trim();
            if (string.IsNullOrWhiteSpace(token))
                throw new InvalidOperationException("De server gaf een lege pc-token terug.");

            SaveDeviceToken(token);
            _deviceToken = token;
            AuthorizationPanel.IsVisible = false;
            PlayButton.IsEnabled = true;
            StatusText.Text = "Pc geautoriseerd. Klaar om te spelen.";
            StartPcHeartbeat();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Pc-autorisatie mislukt: {ex.Message}";
        }
        finally
        {
            AuthorizeButton.IsEnabled = true;
        }
    }

    private async void PlayButton_Click(object? sender, RoutedEventArgs e)
    {
        PlayButton.IsEnabled = false;
        try
        {
            if (!await EnsurePcAuthorizedAsync())
                return;

            StatusText.Text = "Cross-platform basis is klaar. Minecraft-launch migratie volgt in de volgende fase.";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Starten mislukt: {ex.Message}";
        }
        finally
        {
            PlayButton.IsEnabled = true;
        }
    }

    private void ExitButton_Click(object? sender, RoutedEventArgs e) => Close();

    private async Task<bool> EnsurePcAuthorizedAsync()
    {
        if (string.IsNullOrWhiteSpace(_clientId))
            return false;

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{PoolApi}/v1/launcher/pc/status?clientId={Uri.EscapeDataString(_clientId)}");

        if (!string.IsNullOrWhiteSpace(_deviceToken))
            request.Headers.Add("x-hvmc-device-token", _deviceToken);

        using var response = await _http.SendAsync(request);
        var json = await response.Content.ReadAsStringAsync();

        if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(_deviceToken))
            return true;

        if ((int)response.StatusCode == 403)
            throw new InvalidOperationException("Deze pc is geblokkeerd.");

        if ((int)response.StatusCode is not 404 and not 401)
            throw new InvalidOperationException(GetError(json));

        AuthorizationPanel.IsVisible = true;
        PlayButton.IsEnabled = false;
        StatusText.Text = "Deze pc moet eenmalig worden geautoriseerd.";
        return false;
    }

    private void StartPcHeartbeat()
    {
        if (_heartbeatCts is not null)
            return;

        _heartbeatCts = new CancellationTokenSource();
        _ = Task.Run(async () =>
        {
            while (!_heartbeatCts.IsCancellationRequested)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(_clientId) || string.IsNullOrWhiteSpace(_deviceToken))
                        break;

                    using var request = new HttpRequestMessage(HttpMethod.Post, $"{PoolApi}/v1/launcher/pc/heartbeat");
                    request.Headers.Add("x-hvmc-client-id", _clientId);
                    request.Headers.Add("x-hvmc-device-token", _deviceToken);
                    request.Content = new StringContent(
                        JsonSerializer.Serialize(new { clientId = _clientId }),
                        Encoding.UTF8, "application/json");

                    using var response = await _http.SendAsync(request, _heartbeatCts.Token);
                    if ((int)response.StatusCode == 403)
                        break;
                }
                catch when (!_heartbeatCts.IsCancellationRequested) { }

                await Task.Delay(TimeSpan.FromSeconds(PcHeartbeatSeconds), _heartbeatCts.Token);
            }
        });
    }

    private string GetStableClientId()
    {
        var path = Path.Combine(_root, "client-id.txt");
        try
        {
            if (File.Exists(path))
            {
                var saved = File.ReadAllText(path).Trim();
                if (saved.Length == 64 && saved.All(Uri.IsHexDigit))
                    return saved.ToLowerInvariant();
            }
        }
        catch { }

        var value = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName.Trim())))
            .ToLowerInvariant();

        try { File.WriteAllText(path, value); } catch { }
        return value;
    }

    private string? GetDeviceToken()
    {
        var path = Path.Combine(_root, "device.token");
        try
        {
            if (!File.Exists(path))
                return null;

            var token = File.ReadAllText(path).Trim();
            return token.Length >= 20 ? token : null;
        }
        catch { return null; }
    }

    private void SaveDeviceToken(string token)
    {
        if (token.Any(c => c is '\r' or '\n' or '\0'))
            throw new InvalidOperationException("De pc-token bevat ongeldige tekens.");

        Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "device.token"), token.Trim());
    }

    private static string GetError(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("error", out var error))
                return error.GetString() ?? "Onbekende serverfout.";
            if (doc.RootElement.TryGetProperty("message", out var message))
                return message.GetString() ?? "Onbekende serverfout.";
        }
        catch { }

        return string.IsNullOrWhiteSpace(json) ? "Onbekende serverfout." : json;
    }
}
