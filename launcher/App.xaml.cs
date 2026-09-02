using System.Diagnostics;
using System.IO;
using System.Windows;

namespace HVMCLauncher;

public partial class App : System.Windows.Application
{
    private static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Bendemen", "HVMC");

    private static readonly string InstallDir = Path.Combine(Root, "App");
    private static readonly string InstalledExe = Path.Combine(InstallDir, "HVMCLauncher.exe");

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(Root);

            var currentExe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(currentExe) || !File.Exists(currentExe))
                throw new InvalidOperationException("HVMC kan het huidige uitvoerbare bestand niet vinden.");

            var currentPath = Path.GetFullPath(currentExe);
            var installedPath = Path.GetFullPath(InstalledExe);

            // The first time HVMC is launched, install the launcher into a stable
            // per-user application directory and create normal Windows shortcuts.
            if (!string.Equals(currentPath, installedPath, StringComparison.OrdinalIgnoreCase))
            {
                Directory.CreateDirectory(InstallDir);
                File.Copy(currentPath, InstalledExe, true);
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

            CreateShortcuts();
            var window = new MainWindow();
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "HVMC", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    private static void CreateShortcuts()
    {
        try
        {
            var startMenuDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
                "Programs", "HVMC");
            Directory.CreateDirectory(startMenuDir);

            WriteShortcut(Path.Combine(startMenuDir, "HVMC.lnk"));
            WriteShortcut(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                "HVMC.lnk"));
        }
        catch
        {
            // Shortcut creation must never prevent the launcher from opening.
        }
    }

    private static void WriteShortcut(string shortcutPath)
    {
        var target = EscapePowerShellLiteral(InstalledExe);
        var workingDir = EscapePowerShellLiteral(InstallDir);
        var link = EscapePowerShellLiteral(shortcutPath);
        var description = EscapePowerShellLiteral("HVMC - Hero's Vault MC");

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

    private static string EscapePowerShellLiteral(string value) =>
        value.Replace("'", "''", StringComparison.Ordinal);
}
