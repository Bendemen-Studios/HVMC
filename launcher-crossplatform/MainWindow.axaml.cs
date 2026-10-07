using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using HVMCLauncher.CrossPlatform.Services;

namespace HVMCLauncher.CrossPlatform;

public partial class MainWindow : Window
{
    private readonly LauncherLogger _log = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
    private readonly AccountPoolClient _accounts;
    private readonly ContentSyncService _content;
    private readonly MinecraftService _minecraft;
    private readonly LauncherUpdateService _updates;
    private Process? _minecraftProcess;
    private bool _playing;
    private bool _blocked;
    private bool _contentUpdateFailed;

    public MainWindow()
    {
        InitializeComponent();
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        HvmcPaths.Ensure();

        _accounts = new AccountPoolClient(_http, _log);
        _content = new ContentSyncService(_http, _log);
        _minecraft = new MinecraftService(_http, _log);
        _updates = new LauncherUpdateService(_http, _log);

        if (Version.TryParse(App.AppVersion, out var releaseVersion))
            VersionText.Text = $"Versie: v{releaseVersion.Major}.{releaseVersion.Minor}";
        else
            VersionText.Text = $"Versie: v{App.AppVersion}";

        Opened += async (_, _) => await InitializeAsync();
        Closed += async (_, _) =>
        {
            try { await StopMinecraftAsync(); } catch { }
            _accounts.Dispose();
            _http.Dispose();
        };
    }

    private async Task InitializeAsync()
    {
        // The launcher must become usable before network maintenance starts.
        PlayButton.IsEnabled = !string.IsNullOrWhiteSpace(_accounts.DeviceToken);
        AuthorizationPanel.IsVisible = string.IsNullOrWhiteSpace(_accounts.DeviceToken);
        SetStatus(AuthorizationPanel.IsVisible
            ? "Deze pc moet eenmalig worden geautoriseerd."
            : "Klaar om te spelen.");

        _ = InitializeStartupChecksAsync();
    }

    private async Task InitializeStartupChecksAsync()
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(_accounts.DeviceToken))
            {
                var authorized = await _accounts.EnsureAuthorizedAsync();
                if (!authorized)
                {
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        AuthorizationPanel.IsVisible = true;
                        PlayButton.IsEnabled = false;
                        SetStatus("Deze pc moet eenmalig worden geautoriseerd.");
                    });
                    return;
                }

                _accounts.StartPcHeartbeat();
                SetStatus("Klaar om te spelen.");
                PlayButton.IsEnabled = true;
            }

            if (ShouldCheckLauncherUpdate())
            {
                await Task.Delay(TimeSpan.FromSeconds(5));
                if (_playing || _blocked || !ShouldCheckLauncherUpdate())
                    return;

                var updated = await _updates.CheckAndScheduleAsync();
                if (!updated)
                    SaveLauncherUpdateCheck();
            }
        }
        catch (DeviceBlockedException)
        {
            await BlockDeviceAsync();
        }
        catch (Exception ex)
        {
            // Startup maintenance must never make the launcher unusable.
            _log.Error($"Achtergrond-startupcontrole mislukt: {ex}");
            if (!_blocked)
            {
                SetStatus("Klaar om te spelen.");
                PlayButton.IsEnabled = !AuthorizationPanel.IsVisible;
            }
        }
    }

    private static bool ShouldCheckLauncherUpdate()
    {
        try
        {
            if (!File.Exists(HvmcPaths.LauncherUpdateCheck))
                return true;

            var text = File.ReadAllText(HvmcPaths.LauncherUpdateCheck).Trim();
            if (!DateTimeOffset.TryParse(text, out var lastCheck))
                return true;

            return DateTimeOffset.UtcNow - lastCheck >= TimeSpan.FromHours(6);
        }
        catch
        {
            return true;
        }
    }

    private static void SaveLauncherUpdateCheck()
    {
        try
        {
            File.WriteAllText(
                HvmcPaths.LauncherUpdateCheck,
                DateTimeOffset.UtcNow.ToString("O"));
        }
        catch { }
    }

    private async void AuthorizeButton_Click(object? sender, RoutedEventArgs e)
    {
        AuthorizeButton.IsEnabled = false;
        try
        {
            await _accounts.AuthorizeAsync(AuthorizationCodeBox.Text ?? "");
            AuthorizationPanel.IsVisible = false;
            PlayButton.IsEnabled = true;
            _accounts.StartPcHeartbeat();
            SetStatus("Pc geautoriseerd. Klaar om te spelen.");
        }
        catch (DeviceBlockedException)
        {
            await BlockDeviceAsync();
        }
        catch (Exception ex)
        {
            SetStatus("Pc-autorisatie mislukt.");
            _log.Error($"Pc-autorisatie mislukt: {ex}");
            await ShowErrorAsync("Pc-autorisatie mislukt", FriendlyError(ex));
        }
        finally
        {
            AuthorizeButton.IsEnabled = true;
        }
    }

    private async void PlayButton_Click(object? sender, RoutedEventArgs e)
    {
        if (_playing || _blocked) return;

        _playing = true;
        PlayButton.IsEnabled = false;
        ExitButton.IsEnabled = false;

        try
        {
            await StopMinecraftAsync();

            if (!await _accounts.EnsureAuthorizedAsync())
            {
                AuthorizationPanel.IsVisible = true;
                SetStatus("Deze pc moet eenmalig worden geautoriseerd.");
                return;
            }

            _contentUpdateFailed = false;
            SetStatus("HVMC content synchroniseren...");
            try
            {
                await _content.SyncAsync();
            }
            catch
            {
                _contentUpdateFailed = true;
                throw;
            }

            SetStatus("Minecraft voorbereiden...");
            var lease = await _accounts.AcquireLeaseAsync();

            try
            {
                SetStatus($"{lease.AccountName} geselecteerd.");

                var screen = Screens.Primary;
                var width = screen is null ? 1920 : Math.Max(1280, (int)screen.Bounds.Width);
                var height = screen is null ? 1080 : Math.Max(720, (int)screen.Bounds.Height);

                SetStatus("Minecraft starten...");
                _minecraftProcess = await _minecraft.PrepareAndBuildAsync(
                    lease,
                    _content.FabricProfile,
                    width,
                    height);

                AttachMinecraftLogging(_minecraftProcess);
                _minecraftProcess.Start();
                _minecraftProcess.BeginOutputReadLine();
                _minecraftProcess.BeginErrorReadLine();

                _accounts.StartLeaseHeartbeat();
                SetStatus("Minecraft draait.");

                await _minecraftProcess.WaitForExitAsync();
                var exitCode = _minecraftProcess.ExitCode;

                await WaitForMinecraftShutdownAsync(_minecraftProcess);

                if (exitCode != 0)
                    throw new InvalidOperationException(
                        $"Minecraft is direct afgesloten (exitcode {exitCode}). Bekijk het logbestand: {HvmcPaths.MinecraftLog}");
            }
            finally
            {
                await _accounts.ReleaseLeaseAsync();
            }
        }
        catch (DeviceBlockedException)
        {
            await BlockDeviceAsync();
        }
        catch (Exception ex)
        {
            _log.Error($"Minecraft starten mislukt: {ex}");
            SetStatus("Minecraft is gestopt of kon niet starten.");
            await ShowErrorAsync(
                "Minecraft starten mislukt",
                FriendlyError(ex),
                allowContentRetry: _contentUpdateFailed);
        }
        finally
        {
            await StopMinecraftAsync();
            _playing = false;

            if (!_blocked)
            {
                PlayButton.IsEnabled = !AuthorizationPanel.IsVisible;
                ExitButton.IsEnabled = true;

                if (!AuthorizationPanel.IsVisible &&
                    !StatusText.Text.StartsWith("Minecraft is gestopt", StringComparison.OrdinalIgnoreCase))
                    SetStatus("Klaar om te spelen.");
            }
        }
    }

    private async Task WaitForMinecraftShutdownAsync(Process process)
    {
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

    private void AttachMinecraftLogging(Process process)
    {
        try
        {
            Directory.CreateDirectory(HvmcPaths.Root);
            var writer = new StreamWriter(HvmcPaths.MinecraftLog, false, Encoding.UTF8)
            {
                AutoFlush = true
            };

            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                try { writer.WriteLine(e.Data); } catch { }
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is null) return;
                try { writer.WriteLine(e.Data); } catch { }
            };
            process.Exited += (_, _) =>
            {
                try { writer.Dispose(); } catch { }
            };
        }
        catch (Exception ex)
        {
            _log.Error($"Minecraft logging kon niet worden gestart: {ex.Message}");
        }
    }

    private async Task StopMinecraftAsync()
    {
        var process = _minecraftProcess;
        if (process is null) return;

        try
        {
            if (!process.HasExited)
            {
                SetStatus("Vorige Minecraft-sessie wordt afgesloten...");
                try
                {
                    await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
                }
                catch (TimeoutException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                }
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception ex)
        {
            _log.Error($"Minecraft cleanup mislukt: {ex.Message}");
        }
        finally
        {
            try { process.Close(); } catch { }
            try { process.Dispose(); } catch { }
            _minecraftProcess = null;
        }
    }

    private async Task BlockDeviceAsync()
    {
        if (_blocked) return;
        _blocked = true;
        PlayButton.IsEnabled = false;
        ExitButton.IsEnabled = false;

        try { await StopMinecraftAsync(); } catch { }

        SetStatus("Deze pc is geblokkeerd.");
        await ShowErrorAsync(
            "HVMC",
            "Je bent geblokkeerd. Neem contact op met info@bendemen.nl voor meer informatie.");
        Close();
    }

    private async Task ShowErrorAsync(string title, string message, bool allowContentRetry = false)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 620,
            Height = allowContentRetry ? 360 : 320,
            MinWidth = 520,
            MinHeight = 280,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 18 };
        panel.Children.Add(new TextBlock
        {
            Text = message,
            TextWrapping = Avalonia.Media.TextWrapping.Wrap
        });

        var buttons = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 10
        };

        if (allowContentRetry)
        {
            var retry = new Button { Content = "OPNIEUW DOWNLOADEN", Width = 190 };
            retry.Click += async (_, _) =>
            {
                dialog.Close();
                await RetryContentDownloadAsync();
            };
            buttons.Children.Add(retry);
        }

        var ok = new Button { Content = "OK", Width = 100 };
        ok.Click += (_, _) => dialog.Close();
        buttons.Children.Add(ok);
        panel.Children.Add(buttons);

        dialog.Content = panel;
        await dialog.ShowDialog(this);
    }

    private async Task RetryContentDownloadAsync()
    {
        PlayButton.IsEnabled = false;
        ExitButton.IsEnabled = false;
        _contentUpdateFailed = false;

        try
        {
            SetStatus("HVMC content opnieuw downloaden...");
            await _content.SyncAsync(forceRedownload: true);
            SetStatus("HVMC content is bijgewerkt. Klaar om te spelen.");
            PlayButton.IsEnabled = true;
        }
        catch (Exception ex)
        {
            _contentUpdateFailed = true;
            SetStatus("Opnieuw downloaden mislukt.");
            _log.Error($"Content opnieuw downloaden mislukt: {ex}");
            await ShowErrorAsync("Opnieuw downloaden mislukt", FriendlyError(ex), allowContentRetry: true);
        }
        finally
        {
            ExitButton.IsEnabled = true;
            if (!_contentUpdateFailed)
                PlayButton.IsEnabled = true;
        }
    }

    private static string FriendlyError(Exception ex)
    {
        if (ex is HttpRequestException)
            return "De accountserver of downloadserver is niet bereikbaar. Controleer je internetverbinding en probeer het opnieuw.";
        if (ex is TaskCanceledException)
            return "De verbinding duurde te lang. Probeer het opnieuw.";
        if (ex is UnauthorizedAccessException)
            return "HVMC heeft geen toegang tot de benodigde bestanden.";
        if (ex is IOException)
            return "HVMC kon een bestand niet lezen of schrijven. Controleer de bestandsrechten.";
        return string.IsNullOrWhiteSpace(ex.Message)
            ? "Er is een onverwachte fout opgetreden."
            : ex.Message;
    }

    private void ExitButton_Click(object? sender, RoutedEventArgs e) => Close();

    private void SetStatus(string text) =>
        Dispatcher.UIThread.Post(() => StatusText.Text = text);
}
