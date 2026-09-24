using System.IO;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using WpfMessageBox = System.Windows.MessageBox;

namespace HVMCLauncher;

public partial class App : System.Windows.Application
{
    private const string AppName = "HVMC School Launcher";
    private const string Publisher = "Bendemen Studios";

    public static string AppVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private const string LatestReleaseApi =
        "https://api.github.com/repos/Bendemen-Studios/HVMC/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(5)
    };

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bendemen", "HVMC");

    private static readonly string InstallDir = Path.Combine(Root, "App");
    private static readonly string InstalledExe = Path.Combine(InstallDir, "HVMCLauncher.exe");

    private async void Application_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            if (e.Args.Any(a => string.Equals(a, "--uninstall", StringComparison.OrdinalIgnoreCase)))
            {
                Uninstall();
                Shutdown();
                return;
            }

            Directory.CreateDirectory(Root);

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
                throw new InvalidOperationException("HVMC kan het huidige uitvoerbare bestand niet vinden.");

            var currentPath = Path.GetFullPath(currentExe);
            var installedPath = Path.GetFullPath(InstalledExe);

            if (!string.Equals(currentPath, installedPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(InstallDir);
                File.Copy(currentPath, InstalledExe, true);
                RegisterWindowsApp();
                CreateShortcuts();

                Process.Start(new ProcessStartInfo
                {
                    FileName = InstalledExe,
                    WorkingDirectory = InstallDir,
                    UseShellExecute = true
                });

                Shutdown();
                return;
            }

            if (await CheckForLauncherUpdateAsync())
                return;

            RegisterWindowsApp();
            CreateShortcuts();

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            WpfMessageBox.Show(ex.Message, AppName, MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static async Task<bool> CheckForLauncherUpdateAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("HVMC-School-Launcher");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);

            if (!response.IsSuccessStatusCode)
                return false;

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);

            if (release is null ||
                release.Draft ||
                release.Prerelease ||
                string.IsNullOrWhiteSpace(release.TagName))
                return false;

            var remoteText = release.TagName.Trim().TrimStart('v', 'V');

            if (!Version.TryParse(remoteText, out var remoteVersion) ||
                !Version.TryParse(AppVersion, out var currentVersion) ||
                remoteVersion <= currentVersion)
                return false;

            var asset = release.Assets?
                .FirstOrDefault(x => string.Equals(
                    x.Name,
                    "HVMCLauncher.exe",
                    StringComparison.OrdinalIgnoreCase));

            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                return false;

            var tempPath = Path.Combine(
                Root,
                $"HVMCLauncher-{remoteVersion}-{Guid.NewGuid():N}.tmp");

            try
            {
                using var downloadRequest = new HttpRequestMessage(
                    HttpMethod.Get,
                    asset.BrowserDownloadUrl);

                downloadRequest.Headers.UserAgent.ParseAdd("HVMC-School-Launcher");

                using var downloadResponse = await Http.SendAsync(
                    downloadRequest,
                    HttpCompletionOption.ResponseHeadersRead);

                downloadResponse.EnsureSuccessStatusCode();

                await using (var source = await downloadResponse.Content.ReadAsStreamAsync())
                await using (var target = File.Create(tempPath))
                {
                    await source.CopyToAsync(target);
                }

                var downloadedSize = new FileInfo(tempPath).Length;
                if (asset.Size > 0 && downloadedSize != asset.Size)
                    throw new InvalidOperationException(
                        "De nieuwe launcher heeft een onjuiste bestandsgrootte.");

                if (!string.IsNullOrWhiteSpace(asset.Digest))
                {
                    var expectedDigest = asset.Digest.Trim();
                    if (expectedDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                        expectedDigest = expectedDigest["sha256:".Length..];

                    var actualDigest = await Sha256HexAsync(tempPath);
                    if (!string.Equals(
                        actualDigest,
                        expectedDigest,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidOperationException(
                            "De nieuwe launcher kon niet worden geverifieerd.");
                    }
                }

                var currentHash = await Sha256HexAsync(InstalledExe);
                var newHash = await Sha256HexAsync(tempPath);

                if (string.Equals(
                    currentHash,
                    newHash,
                    StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(tempPath);
                    return false;
                }

                ScheduleSelfReplacement(InstalledExe, tempPath);
                return true;
            }
            catch
            {
                try { File.Delete(tempPath); } catch { }
                throw;
            }
        }
        catch (Exception ex) when (
            ex is HttpRequestException ||
            ex is TaskCanceledException ||
            ex is InvalidOperationException ||
            ex is JsonException)
        {
            return false;
        }
    }

    private static async Task<string> Sha256HexAsync(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void ScheduleSelfReplacement(string destinationExe, string updateExe)
    {
        var pid = Environment.ProcessId;
        var source = Ps(updateExe);
        var destination = Ps(destinationExe);
        var workingDirectory = Ps(InstallDir);

        var script =
            "$pid=" + pid + ";" +
            "$src=" + source + ";" +
            "$dst=" + destination + ";" +
            "Start-Sleep -Milliseconds 700;" +
            "while(Get-Process -Id $pid -ErrorAction SilentlyContinue){" +
                "Start-Sleep -Milliseconds 200" +
            "};" +
            "Move-Item -LiteralPath $src -Destination $dst -Force;" +
            "Start-Process -FilePath $dst -WorkingDirectory " +
                workingDirectory + ";";

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
                "-Command", script
            }
        });

        Environment.Exit(0);
    }

    private static void RegisterWindowsApp()
    {
        try
        {
            using var uninstall = Registry.CurrentUser.CreateSubKey(
                @"Software\\Microsoft\\Windows\\CurrentVersion\\Uninstall\\HVMC", true);

            if (uninstall is null) return;

            uninstall.SetValue("DisplayName", AppName);
            uninstall.SetValue("DisplayVersion", AppVersion);
            uninstall.SetValue("Publisher", Publisher);
            uninstall.SetValue("InstallLocation", InstallDir);
            uninstall.SetValue("DisplayIcon", InstalledExe);
            uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
            uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            uninstall.SetValue(
                "UninstallString",
                $"\"{InstalledExe}\" --uninstall");
        }
        catch { }
    }

    private static void Uninstall()
    {
        try
        {
            var startMenuShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "HVMC School Launcher",
                "HVMC School Launcher.lnk");

            var desktopShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "HVMC School Launcher.lnk");

            File.Delete(startMenuShortcut);
            File.Delete(desktopShortcut);
            Directory.Delete(
                Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                    "Programs", "HVMC School Launcher"),
                true);
        }
        catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"SoftwareMicrosoftWindowsCurrentVersionUninstallHVMC",
                false);
        }
        catch { }

        try
        {
            if (string.IsNullOrWhiteSpace(Environment.ProcessPath)) return;

            var pid = Environment.ProcessId;
            var script =
                "$pid=" + pid +
                ";$path=" + Ps(InstallDir) +
                ";Start-Sleep -Milliseconds 700;" +
                "while(Get-Process -Id $pid -ErrorAction SilentlyContinue){" +
                    "Start-Sleep -Milliseconds 200" +
                "};" +
                "if(Test-Path -LiteralPath $path){" +
                    "Remove-Item -LiteralPath $path -Recurse -Force" +
                "}";

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
                    "-Command", script
                }
            });
        }
        catch { }
    }

    private static void CreateShortcuts()
    {
        try
        {
            var startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "HVMC School Launcher");

            Directory.CreateDirectory(startMenuDir);

            WriteShortcut(Path.Combine(
                startMenuDir,
                "HVMC School Launcher.lnk"));

            WriteShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "HVMC School Launcher.lnk"));
        }
        catch { }
    }

    private static void WriteShortcut(string shortcutPath)
    {
        var target = Ps(InstalledExe);
        var workingDir = Ps(InstallDir);
        var link = Ps(shortcutPath);
        var description = Ps("HVMC School Launcher - Hero's Vault MC");

        var script =
            "$shell=New-Object -ComObject WScript.Shell;" +
            $"$shortcut=$shell.CreateShortcut('{link}');" +
            $"$shortcut.TargetPath='{target}';" +
            $"$shortcut.WorkingDirectory='{workingDir}';" +
            $"$shortcut.Description='{description}';" +
            $"$shortcut.IconLocation='{target},0';" +
            "$shortcut.Save();";

        using var process = Process.Start(new ProcessStartInfo
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
                "-Command", script
            }
        });

        process?.WaitForExit(5000);
    }

    private static string Ps(string value) =>
        "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private sealed record GitHubRelease(
        string? TagName,
        bool Draft,
        bool Prerelease,
        List<GitHubAsset>? Assets);

    private sealed record GitHubAsset(
        string? Name,
        long Size,
        string? BrowserDownloadUrl,
        string? Digest);
}
