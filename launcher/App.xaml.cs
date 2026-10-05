using System.IO;
using Microsoft.Win32;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using WpfMessageBox = System.Windows.MessageBox;

namespace HVMCLauncher;

public partial class App : System.Windows.Application
{
    private const string AppName = "HVMC School Launcher";
    private const string Publisher = "Bendemen Studios";

    public static string AppVersion => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bendemen", "HVMC");

    private static readonly string InstallDir = Path.Combine(Root, "App");
    private static readonly string InstalledExe = Path.Combine(InstallDir, "HVMC.exe");

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

            // Do not block the first WPF frame on a network release check.
            // MainWindow performs the launcher update check once it is visible.

            // Show the launcher immediately. Registry/shortcut maintenance is
            // non-critical work and must not delay the first visible frame.
            var window = new MainWindow();
            MainWindow = window;
            window.Show();

            _ = Task.Run(() =>
            {
                try { RegisterWindowsApp(); } catch { }
                try { CreateShortcuts(); } catch { }
            });
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

}
