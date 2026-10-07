using CmlLib.Core;
using CmlLib.Core.Auth;
using CmlLib.Core.ProcessBuilder;
using System.Diagnostics;
using System.Text;

namespace HVMCLauncher.CrossPlatform.Services;

public sealed class MinecraftService
{
    private const string MinecraftVersion = "26.2";
    private const int MaximumRamMb = 4096;
    private readonly HttpClient _http;
    private readonly LauncherLogger _log;

    public MinecraftService(HttpClient http, LauncherLogger log)
    {
        _http = http;
        _log = log;
    }

    public async Task<Process> PrepareAndBuildAsync(
        LeaseResponse lease,
        string fabricProfile,
        int width,
        int height,
        CancellationToken ct = default)
    {
        HvmcPaths.Ensure();

        var path = new MinecraftPath(HvmcPaths.MinecraftRoot);
        var launcher = new MinecraftLauncher(
            MinecraftLauncherParameters.CreateDefault(path, _http));

        Exception? last = null;
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                WriteDownloadLog($"=== Minecraft installatiepoging {attempt}/3 ===");
                _log.Info($"Minecraft {MinecraftVersion} installatiepoging {attempt}/3.");
                await launcher.InstallAsync(MinecraftVersion, ct);
                WriteDownloadLog($"InstallAsync poging {attempt}/3 succesvol afgerond.");
                last = null;
                break;
            }
            catch (Exception ex)
            {
                last = ex;
                WriteDownloadLog($"InstallAsync poging {attempt}/3 mislukt: {ex}");
                _log.Error($"Minecraft installatiepoging {attempt}/3 mislukt: {ex}");

                if (attempt < 3)
                    await Task.Delay(TimeSpan.FromSeconds(attempt * 2), ct);
            }
        }

        if (last is not null)
            throw new InvalidOperationException(
                $"Minecraft kon niet worden gedownload. Details staan in: {HvmcPaths.MinecraftDownloadLog}",
                last);

        var profilePath = Path.Combine(
            path.BasePath,
            "versions",
            fabricProfile,
            $"{fabricProfile}.json");

        if (!File.Exists(profilePath))
            throw new InvalidOperationException(
                $"Gebundelde Fabric ontbreekt: {profilePath}");

        var session = new MSession
        {
            Username = lease.Username,
            AccessToken = lease.MinecraftAccessToken,
            UUID = lease.Uuid,
            Xuid = lease.Xuid ?? string.Empty
        };

        var process = await launcher.InstallAndBuildProcessAsync(
            fabricProfile,
            new MLaunchOption
            {
                Session = session,
                MaximumRamMb = MaximumRamMb,
                GameLauncherName = "HVMC School Launcher",
                FullScreen = true,
                ScreenWidth = width,
                ScreenHeight = height
            },
            ct);

        ConfigureProcess(process);
        return process;
    }

    private void WriteDownloadLog(string message)
    {
        try
        {
            Directory.CreateDirectory(HvmcPaths.Root);
            File.AppendAllText(
                HvmcPaths.MinecraftDownloadLog,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    private static void ConfigureProcess(Process process)
    {
        process.StartInfo.UseShellExecute = false;
        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.StartInfo.StandardOutputEncoding = Encoding.UTF8;
        process.StartInfo.StandardErrorEncoding = Encoding.UTF8;
        process.EnableRaisingEvents = true;
    }
}