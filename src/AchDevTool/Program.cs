using System;
using Avalonia;
using Velopack;

namespace AchDevTool;

internal static class Program
{
    // Avalonia configuration, don't remove; also used by visual designer.
    [STAThread]
    public static void Main(string[] args)
    {
        // Must run before anything else: handles Velopack install/update/uninstall
        // hooks when the app is launched by the installer or the updater.
        VelopackApp.Build().Run();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
