using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Interactivity;
using CmlLib.Core;

namespace HVMCLauncher.CrossPlatform;

public partial class MainWindow : Window
{
    private readonly LauncherLogger _log = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };
    private readonly AccountPoolClient _accounts;
    private readonly ContentSyncService _content;
    private readonly MinecraftService _minecraft;
    private Process? _minecraftProcess;
    private bool _playing;
    private bool _blocked;

    public MainWindow()
    {
        InitializeComponent();
        HvmcPaths.Ensure();

        _accounts = new AccountPoolClient(_http, _log);
        _content = new ContentSyncService(_http, _log);
        _minecraft = new MinecraftService(_http, _log);

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
        try
        {
            SetStatus("HVMC controleren...");
            if (!await _accounts.EnsureAuthorizedAsync())
            {
                AuthorizationPanel.IsVisible = true;
                PlayButton.IsEnabled = false;
                SetStatus("Deze pc moet eenmalig worden geautoriseerd.");
                return;
            }

            AuthorizationPanel.IsVisible = false;
            PlayButton.IsEnabled = true;
            _accounts.StartPcHeartbeat();
            SetStatus("Klaar om te spelen.");
        }
        catch (DeviceBlockedException)
        {
            await BlockDeviceAsync();
        }
        catch (Exception ex)
        {
            PlayButton.IsEnabled = false;
            SetStatus("Accountserver niet bereikbaar.");
            _log.Error($"Startupcontrole mislukt: {ex}");
        }
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
        catch (Exception ex)
        {
            SetStatus("Pc-autorisatie mislukt.");
            _log.Error($"Pc-autorisatie mislukt: {ex}");
            await ShowErrorAsync("Pc-autorisatie mislukt", ex.Message);
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

            SetStatus("HVMC content synchroniseren...");
            await _content.SyncAsync();

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

                if (exitCode != 0)
                    _log.Error($"Minecraft afgesloten met exitcode {exitCode}.");
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
            SetStatus("Minecraft kon niet worden gestart.");
            await ShowErrorAsync("Minecraft starten mislukt", FriendlyError(ex));
        }
        finally
        {
            await StopMinecraftAsync();
            _playing = false;
            if (!_blocked)
            {
                PlayButton.IsEnabled = true;
                ExitButton.IsEnabled = true;
                if (!AuthorizationPanel.IsVisible)
                    SetStatus("Klaar om te spelen.");
            }
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
                try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); }
                catch (TimeoutException)
                {
                    try { process.Kill(entireProcessTree: true); } catch { }
                    try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
                }
            }
        }
        catch (InvalidOperationException) { }
        catch (Exception ex) { _log.Error($"Minecraft cleanup mislukt: {ex.Message}"); }
        finally
        {
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
        await ShowErrorAsync("HVMC", "Je bent geblokkeerd. Neem contact op met info@bendemen.nl voor meer informatie.");
        Close();
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 560,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = true
        };

        var panel = new StackPanel { Margin = new Avalonia.Thickness(24), Spacing = 18 };
        panel.Children.Add(new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var button = new Button { Content = "OK", HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right, Width = 100 };
        button.Click += (_, _) => dialog.Close();
        panel.Children.Add(button);
        dialog.Content = panel;
        await dialog.ShowDialog(this);
    }

    private static string FriendlyError(Exception ex)
    {
        if (ex is HttpRequestException) return "De accountserver of downloadserver is niet bereikbaar.";
        if (ex is TaskCanceledException) return "De verbinding duurde te lang. Probeer het opnieuw.";
        if (ex is UnauthorizedAccessException) return "HVMC heeft geen toegang tot de benodigde bestanden.";
        return string.IsNullOrWhiteSpace(ex.Message) ? "Er is een onverwachte fout opgetreden." : ex.Message;
    }

    private void ExitButton_Click(object? sender, RoutedEventArgs e) => Close();
    private void SetStatus(string text) => Dispatcher.UIThread.Post(() => StatusText.Text = text);
}