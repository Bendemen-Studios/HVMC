using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace HVMCLauncher.CrossPlatform.Services;

public sealed class LauncherUpdateService
{
    private const string Repo = "Bendemen-Studios/HVMC";
    private readonly HttpClient _http;
    private readonly LauncherLogger _log;

    public LauncherUpdateService(HttpClient http, LauncherLogger log)
    {
        _http = http; _log = log;
    }

    public async Task<bool> CheckAndScheduleAsync(CancellationToken ct = default)
    {
        var currentPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentPath) || !File.Exists(currentPath))
            return false;

        if (!Version.TryParse(App.AppVersion, out var current))
            return false;

        Version remote;
        try
        {
            using var response = await _http.GetAsync($"https://raw.githubusercontent.com/{Repo}/main/version.txt", ct);
            if (!response.IsSuccessStatusCode) return false;
            var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (!Version.TryParse(text, out remote)) return false;
        }
        catch (Exception ex)
        {
            _log.Info($"Launcher updatecontrole overgeslagen: {ex.Message}");
            return false;
        }

        if (remote <= current) return false;

        var tag = $"v{remote}";
        var asset = GetAssetName();
        var url = $"https://github.com/{Repo}/releases/download/{tag}/{asset}";
        var temp = Path.Combine(HvmcPaths.Root, $"HVMC-update-{Guid.NewGuid():N}");

        try
        {
            Directory.CreateDirectory(HvmcPaths.Root);
            using var response = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
            {
                _log.Info($"Launcher {tag} is nog niet beschikbaar voor {RuntimeInformation.RuntimeIdentifier}.");
                return false;
            }

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(temp))
                await source.CopyToAsync(target, ct);

            var size = new FileInfo(temp).Length;
            if (size < 1_000_000)
                throw new InvalidOperationException("Nieuwe launcher is te klein.");

            await ScheduleReplacementAsync(temp, currentPath);
            return true;
        }
        catch (Exception ex)
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            _log.Error($"Launcher-update {tag} mislukt: {ex}");
            return false;
        }
    }

    private static string GetAssetName()
    {
        if (OperatingSystem.IsWindows()) return "HVMC.exe";

        var arch = RuntimeInformation.ProcessArchitecture;
        if (OperatingSystem.IsMacOS())
            return arch == Architecture.Arm64 ? "HVMC-macos-arm64" : "HVMC-macos-x64";

        return arch == Architecture.Arm64 ? "HVMC-linux-arm64" : "HVMC-linux-x64";
    }

    private static async Task ScheduleReplacementAsync(string source, string target)
    {
        var pid = Environment.ProcessId;
        if (OperatingSystem.IsWindows())
        {
            var script = $"$pid={pid};$src='{Escape(source)}';$dst='{Escape(target)}';Start-Sleep -Milliseconds 800;while(Get-Process -Id $pid -ErrorAction SilentlyContinue){{Start-Sleep -Milliseconds 200}};Move-Item -LiteralPath $src -Destination $dst -Force;Start-Process -FilePath $dst;";
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-WindowStyle", "Hidden", "-Command", script }
            });
        }
        else
        {
            var scriptPath = Path.Combine(Path.GetDirectoryName(source)!, $"update-{Guid.NewGuid():N}.sh");
            var script = $"#!/bin/sh
PID={pid}
SRC='{EscapeShell(source)}'
DST='{EscapeShell(target)}'
sleep 1
while kill -0 "$PID" 2>/dev/null; do sleep 0.2; done
mv -f "$SRC" "$DST"
chmod +x "$DST"
rm -f "$0"
nohup "$DST" >/dev/null 2>&1 &
";
            await File.WriteAllTextAsync(scriptPath, script, new UTF8Encoding(false));
            try { File.SetUnixFileMode(scriptPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); } catch { }

            Process.Start(new ProcessStartInfo
            {
                FileName = "/bin/sh",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList = { scriptPath }
            });
        }

        await Task.Delay(250);
        Environment.Exit(0);
    }

    private static string Escape(string value) => value.Replace("'", "''", StringComparison.Ordinal);
    private static string EscapeShell(string value) => value.Replace("'", "'\\''", StringComparison.Ordinal);
}
