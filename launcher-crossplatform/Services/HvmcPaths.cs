namespace HVMCLauncher.CrossPlatform.Services;

public static class HvmcPaths
{
    public static string Root
    {
        get
        {
            var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrWhiteSpace(basePath))
                basePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "share");
            return Path.Combine(basePath, "Bendemen", "HVMC");
        }
    }

    public static string MinecraftRoot
    {
        get
        {
            if (OperatingSystem.IsWindows())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
            if (OperatingSystem.IsMacOS())
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Library", "Application Support", "minecraft");
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft");
        }
    }

    public static string ClientId => Path.Combine(Root, "client-id.txt");
    public static string DeviceToken => Path.Combine(Root, "device.token");
    public static string LauncherLog => Path.Combine(Root, "launcher.log");
    public static string LauncherUpdateCheck => Path.Combine(Root, "launcher-update-check.txt");
    public static string MinecraftLog => Path.Combine(Root, "minecraft-launch.log");
    public static string MinecraftDownloadLog => Path.Combine(Root, "minecraft-download.log");
    public static string ContentManifest => Path.Combine(Root, "content-manifest.json");
    public static string ContentState => Path.Combine(Root, "state.json");

    public static void Ensure()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MinecraftRoot);
        foreach (var dir in new[] { "mods", "config", "resourcepacks", "shaderpacks", "datapacks", "kubejs", "versions" })
            Directory.CreateDirectory(Path.Combine(MinecraftRoot, dir));
    }
}