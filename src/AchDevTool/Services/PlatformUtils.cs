using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AchDevTool.Services;

public static class PlatformUtils
{
    public static bool IsWindows => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsMacOS => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool IsLinux => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);

    public static string GetPlatformName() => IsWindows ? "windows" : IsMacOS ? "macos" : "linux";

    /// <summary>Open a file/URL with the OS default handler (Finder/Explorer, browser, editor...).</summary>
    public static void OpenWithSystem(string path)
    {
        try
        {
            ProcessStartInfo psi;
            if (IsMacOS)
            {
                psi = new ProcessStartInfo("open") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(path);
            }
            else if (IsWindows)
            {
                psi = new ProcessStartInfo("cmd") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add("/c");
                psi.ArgumentList.Add("start");
                psi.ArgumentList.Add("");
                psi.ArgumentList.Add(path);
            }
            else
            {
                psi = new ProcessStartInfo("xdg-open") { UseShellExecute = false, CreateNoWindow = true };
                psi.ArgumentList.Add(path);
            }

            Process.Start(psi);
        }
        catch
        {
            // Best-effort, same as the Tauri backend which ignored spawn failures here.
        }
    }
}
