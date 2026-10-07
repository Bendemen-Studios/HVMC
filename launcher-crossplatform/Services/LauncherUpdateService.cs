using System.Diagnostics;
using System.IO.Compression;
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
        _http = http;
        _log = log;
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
            using var response = await _http.GetAsync(
                $"https://raw.githubusercontent.com/{Repo}/main/version.txt", ct);

            if (!response.IsSuccessStatusCode)
            {
                _log.Info($"Launcher releasecontrole overgeslagen: version.txt gaf HTTP {(int)response.StatusCode}.");
                return false;
            }

            var text = (await response.Content.ReadAsStringAsync(ct)).Trim();
            if (!Version.TryParse(text, out remote))
            {
                _log.Info($"Launcher releasecontrole overgeslagen: ongeldige remote versie '{text}'.");
                return false;
            }
        }
        catch (Exception ex)
        {
            _log.Info($"Launcher updatecontrole overgeslagen: {ex.Message}");
            return false;
        }

        if (remote <= current)
            return false;

        var tag = $"v{remote}";
        var asset = GetAssetName();
        var url = $"https://github.com/{Repo}/releases/download/{tag}/{asset}";
        var temp = Path.Combine(
            HvmcPaths.Root,
            $"HVMC-update-{Guid.NewGuid():N}{GetArchiveExtension()}");

        try
        {
            Directory.CreateDirectory(HvmcPaths.Root);

            using var response = await _http.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                ct);

            // version.txt can become visible a little before the release asset.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                _log.Info($"HVMC {tag} bestaat al in version.txt, maar het releasebestand is nog niet klaar.");
                return false;
            }

            if (!response.IsSuccessStatusCode)
            {
                _log.Info($"HVMC {tag} release-download gaf HTTP {(int)response.StatusCode}.");
                return false;
            }

            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(temp))
            {
                await source.CopyToAsync(target, ct);
                await target.FlushAsync(ct);
            }

            var size = new FileInfo(temp).Length;
            if (size < 1_000_000)
                throw new InvalidOperationException("Nieuwe launcher is te klein of ongeldig.");

            if (OperatingSystem.IsWindows())
            {
                await ScheduleReplacementAsync(temp, currentPath);
                return true;
            }

            var extracted = Path.Combine(
                HvmcPaths.Root,
                $"HVMC-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(extracted);

            ExtractArchive(temp, extracted);

            var replacement = FindReplacement(extracted)
                ?? throw new InvalidOperationException(
                    "Het updatepakket bevat geen geschikte HVMC-launcher.");

            await ScheduleReplacementAsync(replacement, currentPath, extracted);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
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
        if (OperatingSystem.IsWindows())
            return "HVMC.exe";

        var arch = RuntimeInformation.ProcessArchitecture;

        if (OperatingSystem.IsMacOS())
            return arch == Architecture.Arm64
                ? "HVMC-macos-arm64.zip"
                : "HVMC-macos-x64.zip";

        return arch == Architecture.Arm64
            ? "HVMC-linux-arm64.tar.gz"
            : "HVMC-linux-x64.tar.gz";
    }

    private static string GetArchiveExtension()
    {
        if (OperatingSystem.IsWindows()) return ".exe";
        return OperatingSystem.IsMacOS() ? ".zip" : ".tar.gz";
    }

    private static void ExtractArchive(string archive, string destination)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archive, destination, true);
            return;
        }

        if (archive.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            var psi = new ProcessStartInfo
            {
                FileName = "tar",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            psi.ArgumentList.Add("-xzf");
            psi.ArgumentList.Add(archive);
            psi.ArgumentList.Add("-C");
            psi.ArgumentList.Add(destination);

            using var process = Process.Start(psi)
                ?? throw new InvalidOperationException("tar kon niet worden gestart.");

            process.WaitForExit();
            var error = process.StandardError.ReadToEnd();

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    $"Uitpakken van launcher-update mislukt: {error}");

            return;
        }

        throw new InvalidOperationException(
            $"Onbekend launcher-updateformaat: {archive}");
    }

    private static string? FindReplacement(string extracted)
    {
        if (OperatingSystem.IsMacOS())
        {
            return Directory.EnumerateFiles(extracted, "HVMC", SearchOption.AllDirectories)
                .FirstOrDefault(path =>
                    path.Contains(
                        $"{Path.DirectorySeparatorChar}Contents{Path.DirectorySeparatorChar}MacOS{Path.DirectorySeparatorChar}HVMC",
                        StringComparison.Ordinal))
                ?? Directory.EnumerateFiles(
                    extracted, "HVMC", SearchOption.AllDirectories).FirstOrDefault();
        }

        return Directory.EnumerateFiles(
            extracted, "HVMC", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static async Task ScheduleReplacementAsync(
        string source,
        string target,
        string? cleanupDirectory = null)
    {
        var pid = Environment.ProcessId;

        if (OperatingSystem.IsWindows())
        {
            var backup = target + ".update-backup";
            var scriptPath = Path.Combine(
                Path.GetDirectoryName(target) ?? AppContext.BaseDirectory,
                $"HVMC-update-{Guid.NewGuid():N}.ps1");

            var script = string.Join(Environment.NewLine, new[]
            {
                "$ErrorActionPreference = 'Stop'",
                "$replaced = $false",
                $"$LauncherPid = {pid}",
                $"$SourcePath = {Ps(source)}",
                $"$TargetPath = {Ps(target)}",
                $"$BackupPath = {Ps(backup)}",
                $"$ScriptPath = {Ps(scriptPath)}",
                "try {",
                "  Start-Sleep -Milliseconds 1000",
                "  $deadline = (Get-Date).AddSeconds(30)",
                "  while ((Get-Date) -lt $deadline -and (Get-Process -Id $LauncherPid -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 250 }",
                "  if (Get-Process -Id $LauncherPid -ErrorAction SilentlyContinue) { throw 'De oude HVMC-launcher is na 30 seconden nog actief.' }",
                "  if (Test-Path -LiteralPath $BackupPath) { Remove-Item -LiteralPath $BackupPath -Force -ErrorAction Stop }",
                "  if (Test-Path -LiteralPath $TargetPath) { Move-Item -LiteralPath $TargetPath -Destination $BackupPath -Force -ErrorAction Stop }",
                "  Move-Item -LiteralPath $SourcePath -Destination $TargetPath -Force -ErrorAction Stop",
                "  if (-not (Test-Path -LiteralPath $TargetPath)) { throw 'Nieuwe HVMC-launcher is na vervangen niet gevonden.' }",
                "  $replaced = $true",
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

            await File.WriteAllTextAsync(
                scriptPath,
                script,
                new UTF8Encoding(false));

            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                ArgumentList =
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-ExecutionPolicy", "Bypass",
                    "-File", scriptPath
                }
            });
        }
        else
        {
            var scriptPath = Path.Combine(
                Path.GetDirectoryName(source) ?? HvmcPaths.Root,
                $"update-{Guid.NewGuid():N}.sh");

            var cleanup = string.IsNullOrWhiteSpace(cleanupDirectory)
                ? string.Empty
                : $"rm -rf -- {EscapeShell(cleanupDirectory)}";

            var script = string.Join(Environment.NewLine, new[]
            {
                "#!/bin/sh",
                "set -u",
                $"PID={pid}",
                $"SRC='{EscapeShell(source)}'",
                $"DST='{EscapeShell(target)}'",
                "sleep 1",
                "deadline=$(( $(date +%s) + 30 ))",
                @"while kill -0 ""$PID"" 2>/dev/null && [ $(date +%s) -lt $deadline ]; do sleep 0.2; done",
                @"if kill -0 ""$PID"" 2>/dev/null; then exit 1; fi",
                @"if ! mv -f -- ""$SRC"" ""$DST""; then exit 1; fi",
                @"chmod +x -- ""$DST""",
                cleanup,
                @"rm -f -- ""$0""",
                @"nohup ""$DST"" >/dev/null 2>&1 &"
            }) + Environment.NewLine;

            await File.WriteAllTextAsync(
                scriptPath,
                script,
                new UTF8Encoding(false));

            try
            {
                File.SetUnixFileMode(
                    scriptPath,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);
            }
            catch { }

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

    private static string Ps(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static string EscapeShell(string value) =>
        "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}