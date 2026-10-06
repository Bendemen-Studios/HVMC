using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace HVMCLauncher.CrossPlatform.Services;

public sealed class DeviceBlockedException : Exception
{
    public DeviceBlockedException() : base("Deze pc is geblokkeerd.") { }
}

public sealed record PcRegistrationResponse(string DeviceToken);
public sealed record LeaseResponse(string LeaseId, string Username, string MinecraftAccessToken, string Uuid, string? Xuid, string AccountName);

public sealed class AccountPoolClient : IDisposable
{
    private const string Api = "https://accounts.hvmc.nl";
    private readonly HttpClient _http;
    private readonly LauncherLogger _log;
    private CancellationTokenSource? _pcHeartbeatCts;
    private CancellationTokenSource? _leaseHeartbeatCts;

    public string ClientId { get; }
    public string? DeviceToken { get; private set; }
    public string? LeaseId { get; private set; }

    public AccountPoolClient(HttpClient http, LauncherLogger log)
    {
        _http = http; _log = log; HvmcPaths.Ensure();
        ClientId = LoadClientId();
        DeviceToken = LoadDeviceToken();
    }

    public async Task<bool> EnsureAuthorizedAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{Api}/v1/launcher/pc/status?clientId={Uri.EscapeDataString(ClientId)}");
        AddDeviceHeaders(request);
        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(DeviceToken)) return true;
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden) throw new DeviceBlockedException();
        if (response.StatusCode is not System.Net.HttpStatusCode.NotFound and not System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException(GetError(json));
        return false;
    }

    public async Task AuthorizeAsync(string code, CancellationToken ct = default)
    {
        code = code.Trim().ToUpperInvariant();
        if (code.Length != 10) throw new InvalidOperationException("Vul de 10-karakter HVMC pc-autorisatiecode in.");
        using var content = JsonContent.Create(new { clientId = ClientId, code, name = Environment.MachineName });
        using var response = await _http.PostAsync($"{Api}/v1/launcher/pc/register", content, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(GetError(json));
        var result = JsonSerializer.Deserialize<PcRegistrationResponse>(json, JsonOptions)
            ?? throw new InvalidOperationException("Ongeldige pc-autorisatie-response.");
        if (!IsValidHeaderValue(result.DeviceToken)) throw new InvalidOperationException("De accountserver gaf een ongeldige pc-token terug.");
        DeviceToken = result.DeviceToken.Trim();
        File.WriteAllText(HvmcPaths.DeviceToken, DeviceToken);
        await SendPcHeartbeatAsync(ct);
    }

    public void StartPcHeartbeat()
    {
        _pcHeartbeatCts?.Cancel(); _pcHeartbeatCts = new CancellationTokenSource();
        _ = PcHeartbeatLoopAsync(_pcHeartbeatCts.Token);
    }

    private async Task PcHeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); await SendPcHeartbeatAsync(ct); }
            catch (OperationCanceledException) { break; }
            catch (DeviceBlockedException) { break; }
            catch (Exception ex) { _log.Error($"PC heartbeat mislukt: {ex.Message}"); }
        }
    }

    private async Task SendPcHeartbeatAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(DeviceToken)) return;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/v1/launcher/pc/heartbeat");
        request.Headers.Add("x-hvmc-client-id", ClientId);
        request.Headers.Add("x-hvmc-device-token", DeviceToken);
        request.Content = JsonContent.Create(new { clientId = ClientId, osVersion = Environment.OSVersion.VersionString, launcherVersion = App.AppVersion });
        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden || IsBlocked(json)) throw new DeviceBlockedException();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(GetError(json));
    }

    public async Task<LeaseResponse> AcquireLeaseAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(DeviceToken)) throw new InvalidOperationException("Deze pc is niet geautoriseerd.");
        Exception? last = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/v1/launcher/lease/acquire");
                AddDeviceHeaders(request); request.Content = JsonContent.Create(new { clientId = ClientId });
                using var response = await _http.SendAsync(request, ct);
                var body = await response.Content.ReadAsStringAsync(ct);
                if (!response.IsSuccessStatusCode) throw new InvalidOperationException(GetError(body));
                var lease = JsonSerializer.Deserialize<LeaseResponse>(body, JsonOptions)
                    ?? throw new InvalidOperationException("De accountserver gaf geen accountgegevens terug.");
                if (string.IsNullOrWhiteSpace(lease.LeaseId) || string.IsNullOrWhiteSpace(lease.Username) || string.IsNullOrWhiteSpace(lease.MinecraftAccessToken))
                    throw new InvalidOperationException("De accountserver gaf onvolledige accountgegevens terug.");
                LeaseId = lease.LeaseId; return lease;
            }
            catch (Exception ex) { last = ex; if (attempt < 2) await Task.Delay(1200, ct); }
        }
        throw new InvalidOperationException(last?.Message ?? "Onbekende fout bij het ophalen van een Minecraft-account.", last);
    }

    public void StartLeaseHeartbeat()
    {
        if (string.IsNullOrWhiteSpace(LeaseId) || string.IsNullOrWhiteSpace(DeviceToken)) return;
        _leaseHeartbeatCts?.Cancel(); _leaseHeartbeatCts = new CancellationTokenSource();
        _ = LeaseHeartbeatLoopAsync(LeaseId, _leaseHeartbeatCts.Token);
    }

    private async Task LeaseHeartbeatLoopAsync(string leaseId, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await Task.Delay(TimeSpan.FromSeconds(5), ct); await SendLeaseHeartbeatAsync(leaseId, ct); }
            catch (OperationCanceledException) { break; }
            catch (DeviceBlockedException) { break; }
            catch (Exception ex) { _log.Error($"Lease heartbeat mislukt: {ex.Message}"); }
        }
    }

    private async Task SendLeaseHeartbeatAsync(string leaseId, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/v1/launcher/lease/heartbeat");
        AddDeviceHeaders(request); request.Content = JsonContent.Create(new { clientId = ClientId, leaseId });
        using var response = await _http.SendAsync(request, ct);
        var json = await response.Content.ReadAsStringAsync(ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Forbidden && IsBlocked(json)) throw new DeviceBlockedException();
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(GetError(json));
    }

    public async Task ReleaseLeaseAsync()
    {
        var lease = LeaseId; if (string.IsNullOrWhiteSpace(lease) || string.IsNullOrWhiteSpace(DeviceToken)) return;
        LeaseId = null; _leaseHeartbeatCts?.Cancel();
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, $"{Api}/v1/launcher/lease/release");
                AddDeviceHeaders(request); request.Content = JsonContent.Create(new { clientId = ClientId, leaseId = lease });
                using var response = await _http.SendAsync(request);
                if (response.IsSuccessStatusCode) return;
            }
            catch { }
            if (attempt < 3) await Task.Delay(500 * attempt);
        }
    }

    private void AddDeviceHeaders(HttpRequestMessage request)
    {
        request.Headers.Add("x-hvmc-client-id", ClientId);
        if (!string.IsNullOrWhiteSpace(DeviceToken)) request.Headers.Add("x-hvmc-device-token", DeviceToken);
    }

    private static string LoadClientId()
    {
        try
        {
            var value = File.Exists(HvmcPaths.ClientId) ? File.ReadAllText(HvmcPaths.ClientId).Trim() : "";
            if (value.Length == 64 && value.All(Uri.IsHexDigit)) return value.ToLowerInvariant();
        }
        catch { }
        var id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Environment.MachineName.Trim()))).ToLowerInvariant();
        try { File.WriteAllText(HvmcPaths.ClientId, id); } catch { }
        return id;
    }

    private static string? LoadDeviceToken()
    {
        foreach (var path in new[] { HvmcPaths.DeviceToken, Path.Combine(HvmcPaths.Root, "device-token.txt"), Path.Combine(HvmcPaths.Root, "device-token.dat") })
        {
            try { if (File.Exists(path)) { var value = File.ReadAllText(path).Trim(); if (value.Length >= 20 && IsValidHeaderValue(value)) return value; } } catch { }
        }
        return null;
    }

    private static bool IsValidHeaderValue(string? value) => !string.IsNullOrEmpty(value) && value.IndexOfAny(['\r','\n','\0']) < 0;
    private static bool IsBlocked(string json) => json.Contains("blocked", StringComparison.OrdinalIgnoreCase) || json.Contains("geblokkeerd", StringComparison.OrdinalIgnoreCase) || json.Contains("device_blocked", StringComparison.OrdinalIgnoreCase);

    private static string GetError(string json)
    {
        try { using var doc=JsonDocument.Parse(json); if(doc.RootElement.TryGetProperty("error",out var e)) return e.GetString()??"Onbekende serverfout."; if(doc.RootElement.TryGetProperty("message",out var m)) return m.GetString()??"Onbekende serverfout."; } catch { }
        return string.IsNullOrWhiteSpace(json) ? "Onbekende serverfout." : json;
    }

    public void Dispose() { _pcHeartbeatCts?.Cancel(); _leaseHeartbeatCts?.Cancel(); }
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
}