using Microsoft.Win32;
using System.Diagnostics;
using System.IO;
using System.Windows;
using WpfMessageBox = System.Windows.MessageBox;

namespace HVMCLauncher;

public partial class App : System.Windows.Application
{
    private const string AppName = "HVMC School Launcher";
    private const string Publisher = "Bendemen Studios";
    private const string AppVersion = "1.3.0";

    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bendemen", "HVMC");

    private static readonly string InstallDir = Path.Combine(Root, "App");
    private static readonly string InstalledExe = Path.Combine(InstallDir, "HVMCLauncher.exe");

    private void Application_Startup(object sender, StartupEventArgs e)
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

    private static void RegisterWindowsApp()
    {
        try
        {
            using var uninstall = Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HVMC", true);
            if (uninstall is null) return;

            uninstall.SetValue("DisplayName", AppName);
            uninstall.SetValue("DisplayVersion", AppVersion);
            uninstall.SetValue("Publisher", Publisher);
            uninstall.SetValue("InstallLocation", InstallDir);
            uninstall.SetValue("DisplayIcon", InstalledExe);
            uninstall.SetValue("NoModify", 1, RegistryValueKind.DWord);
            uninstall.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            uninstall.SetValue("UninstallString", $"\"{InstalledExe}\" --uninstall");
        }
        catch { }
    }

    private static void Uninstall()
    {
        try
        {
            var startMenuShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "HVMC School Launcher", "HVMC School Launcher.lnk");
            var desktopShortcut = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "HVMC School Launcher.lnk");

            File.Delete(startMenuShortcut);
            File.Delete(desktopShortcut);
            Directory.Delete(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "HVMC School Launcher"), true);
        }
        catch { }

        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(
                @"Software\Microsoft\Windows\CurrentVersion\Uninstall\HVMC", false);
        }
        catch { }

        try
        {
            var current = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(current)) return;
            var pid = Environment.ProcessId;
            var script = "$pid=" + pid + ";$path=" + Ps(InstallDir) + ";Start-Sleep -Milliseconds 700;while(Get-Process -Id $pid -ErrorAction SilentlyContinue){Start-Sleep -Milliseconds 200};if(Test-Path -LiteralPath $path){Remove-Item -LiteralPath $path -Recurse -Force}";
            Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden,
                ArgumentList = { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", script }
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

            WriteShortcut(Path.Combine(startMenuDir, "HVMC School Launcher.lnk"));
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

        var script = "$shell=New-Object -ComObject WScript.Shell;" +
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
        value.Replace("'", "''", StringComparison.Ordinal);
}
