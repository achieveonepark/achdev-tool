using System.Runtime.InteropServices;

namespace AchDevTool.Services;

/// <summary>Cross-platform app data / config directory resolution.</summary>
public static class AppPaths
{
    private const string AppFolderName = "AchDevTool";

    public static string DataDir
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                return Path.Combine(appData, AppFolderName);
            }

            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                return Path.Combine(home, "Library", "Application Support", AppFolderName);
            }

            var xdgConfig = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            var configBase = string.IsNullOrEmpty(xdgConfig)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config")
                : xdgConfig;
            return Path.Combine(configBase, AppFolderName);
        }
    }

    public static string HomeDir => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
