using System.IO;
using Microsoft.Win32;
using System.Diagnostics;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
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

            LauncherUpdateResult updateResult;
            while (true)
            {
                try
                {
                    updateResult = await CheckForLauncherUpdateAsync();
                    break;
                }
                catch (Exception updateEx)
                {
                    var retry = ShowErrorDialog(
                        "Launcher-update mislukt",
                        updateEx,
                        "OPNIEUW DOWNLOADEN");

                    if (!retry)
                    {
                        Shutdown(1);
                        return;
                    }
                }
            }

            if (updateResult == LauncherUpdateResult.Updated)
                return;

            RegisterWindowsApp();
            CreateShortcuts();

            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            ShowErrorDialog("HVMC starten mislukt", ex, null);
            Shutdown(1);
        }
    }

    private static bool ShowErrorDialog(string title, Exception ex, string? retryText)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 760,
            Height = 520,
            MinWidth = 520,
            MinHeight = 360,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ResizeMode = ResizeMode.CanResize,
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(247, 244, 236))
        };

        var grid = new Grid { Margin = new Thickness(22) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var heading = new System.Windows.Controls.TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(39, 36, 30)),
            Margin = new Thickness(0, 0, 0, 12)
        };
        Grid.SetRow(heading, 0);
        grid.Children.Add(heading);

        var details = new System.Windows.Controls.TextBlock
        {
            Text = ex.Message,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 15,
            Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(39, 36, 30))
        };

        var scrollViewer = new System.Windows.Controls.ScrollViewer
        {
            Content = details,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            CanContentScroll = true,
            MaxHeight = 360
        };

        var detailsBorder = new System.Windows.Controls.Border
        {
            BorderBrush = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(214, 205, 185)),
            BorderThickness = new Thickness(1),
            Background = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(255, 253, 248)),
            Padding = new Thickness(12),
            Child = scrollViewer
        };
        Grid.SetRow(detailsBorder, 1);
        grid.Children.Add(detailsBorder);

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right
        };

        var close = new System.Windows.Controls.Button
        {
            Content = "AFSLUITEN",
            Width = 110,
            Height = 40,
            Margin = new Thickness(0, 14, 0, 0),
            IsDefault = true
        };
        close.Click += (_, _) => dialog.Close();
        buttonPanel.Children.Add(close);

        if (!string.IsNullOrWhiteSpace(retryText))
        {
            var retry = new System.Windows.Controls.Button
            {
                Content = retryText,
                Width = 190,
                Height = 40,
                Margin = new Thickness(0, 14, 10, 0),
                FontWeight = FontWeights.SemiBold
            };
            retry.Click += (_, _) =>
            {
                dialog.DialogResult = true;
                dialog.Close();
            };
            buttonPanel.Children.Insert(0, retry);
        }

        Grid.SetRow(buttonPanel, 2);
        grid.Children.Add(buttonPanel);

        dialog.Content = grid;
        return dialog.ShowDialog() == true;
    }

    private static async Task<LauncherUpdateResult> CheckForLauncherUpdateAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
            request.Headers.UserAgent.ParseAdd("HVMC-School-Launcher");
            request.Headers.Accept.ParseAdd("application/vnd.github+json");

            using var response = await Http.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead);

            // GitHub is allowed to be unavailable. In that case the currently
            // installed launcher may continue. Any other unexpected HTTP result
            // is a hard stop: we must never start an outdated/broken launcher.
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                if (status == 408 || status == 429 || status >= 500)
                    return LauncherUpdateResult.Offline;

                throw new InvalidOperationException(
                    $"GitHub releasecontrole mislukt (HTTP {status} {response.StatusCode}).");
            }

            var json = await response.Content.ReadAsStringAsync();
            var release = JsonSerializer.Deserialize<GitHubRelease>(json, JsonOptions);

            if (release is null ||
                release.Draft ||
                release.Prerelease ||
                string.IsNullOrWhiteSpace(release.TagName))
            {
                throw new InvalidOperationException(
                    "GitHub gaf geen geldige stabiele HVMC-release terug.");
            }

            var remoteText = release.TagName.Trim().TrimStart('v', 'V');

            if (!Version.TryParse(remoteText, out var remoteVersion) ||
                !Version.TryParse(AppVersion, out var currentVersion))
            {
                throw new InvalidOperationException(
                    "De HVMC-versie van de launcher kon niet worden gecontroleerd.");
            }

            if (remoteVersion <= currentVersion)
                return LauncherUpdateResult.UpToDate;

            var asset = release.Assets?
                .FirstOrDefault(x => string.Equals(
                    x.Name,
                    "HVMCLauncher.exe",
                    StringComparison.OrdinalIgnoreCase));

            if (asset is null || string.IsNullOrWhiteSpace(asset.BrowserDownloadUrl))
                throw new InvalidOperationException(
                    $"HVMC {release.TagName} is beschikbaar, maar bevat geen HVMCLauncher.exe.");

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

                if (!downloadResponse.IsSuccessStatusCode)
                {
                    var status = (int)downloadResponse.StatusCode;
                    if (status == 408 || status == 429 || status >= 500)
                        return LauncherUpdateResult.Offline;

                    throw new InvalidOperationException(
                        $"HVMC {release.TagName} kon niet worden gedownload (HTTP {status} {downloadResponse.StatusCode}).");
                }

                await using (var source = await downloadResponse.Content.ReadAsStreamAsync())
                await using (var target = File.Create(tempPath))
                {
                    await source.CopyToAsync(target);
                }

                var downloadedSize = new FileInfo(tempPath).Length;
                if (downloadedSize <= 0 || (asset.Size > 0 && downloadedSize != asset.Size))
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
                    throw new InvalidOperationException(
                        "GitHub meldt een nieuwere versie, maar het gedownloade bestand is identiek aan de huidige launcher.");
                }

                ScheduleSelfReplacement(InstalledExe, tempPath);
                return LauncherUpdateResult.Updated;
            }
            catch
            {
                try { File.Delete(tempPath); } catch { }
                throw;
            }
        }
        catch (HttpRequestException)
        {
            // Network/DNS/TLS failures are the explicit offline exception.
            return LauncherUpdateResult.Offline;
        }
        catch (TaskCanceledException)
        {
            return LauncherUpdateResult.Offline;
        }
    }

    private enum LauncherUpdateResult
    {
        UpToDate,
        Updated,
        Offline
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
