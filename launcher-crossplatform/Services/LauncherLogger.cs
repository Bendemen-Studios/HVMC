namespace HVMCLauncher.CrossPlatform.Services;

public sealed class LauncherLogger
{
    private readonly object _sync = new();
    public void Info(string message) => Write("INFO", message);
    public void Error(string message) => Write("ERROR", message);

    private void Write(string level, string message)
    {
        try
        {
            HvmcPaths.Ensure();
            lock (_sync)
                File.AppendAllText(HvmcPaths.LauncherLog,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}