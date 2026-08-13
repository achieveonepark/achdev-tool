using AchDevTool.Models;

namespace AchDevTool.Services;

public sealed class IosService
{
    public List<IosProject> GetIosProjects(string buildPath)
    {
        var iosPath = Path.Combine(buildPath, "iOS");
        if (!Directory.Exists(iosPath))
        {
            throw new InvalidOperationException("iOS 폴더를 찾을 수 없습니다.");
        }

        var projects = new List<IosProject>();

        foreach (var projectDir in Directory.EnumerateDirectories(iosPath))
        {
            var workspace = Directory.EnumerateDirectories(projectDir, "*.xcworkspace").FirstOrDefault();
            if (workspace is null)
            {
                continue;
            }

            var folderName = Path.GetFileName(projectDir);
            var appName = GetIosName(projectDir);
            var iconBase64 = GetIosIconBase64(projectDir);

            projects.Add(new IosProject(folderName, appName, workspace, iconBase64));
        }

        return projects.OrderBy(p => p.AppName, StringComparer.Ordinal).ToList();
    }

    public string OpenXcodeProject(string workspacePath)
    {
        if (PlatformUtils.IsWindows)
        {
            throw new InvalidOperationException("Windows에서는 Xcode를 사용할 수 없습니다. macOS가 필요합니다.");
        }

        PlatformUtils.OpenWithSystem(workspacePath);
        return "Xcode 프로젝트를 열었습니다.";
    }

    /// <summary>Reads CFBundleDisplayName/CFBundleName from an XML Info.plist (Unity always
    /// generates the XML variant, so a full plist parser isn't required).</summary>
    private static string GetIosName(string projectDir)
    {
        var fallback = Path.GetFileName(projectDir);
        var plistPath = Path.Combine(projectDir, "Info.plist");
        if (!File.Exists(plistPath))
        {
            return fallback;
        }

        string content;
        try
        {
            content = File.ReadAllText(plistPath);
        }
        catch
        {
            return fallback;
        }

        foreach (var key in new[] { "CFBundleDisplayName", "CFBundleName" })
        {
            var search = $"<key>{key}</key>";
            var pos = content.IndexOf(search, StringComparison.Ordinal);
            if (pos < 0)
            {
                continue;
            }

            var rest = content[(pos + search.Length)..];
            var start = rest.IndexOf("<string>", StringComparison.Ordinal);
            if (start < 0)
            {
                continue;
            }

            rest = rest[(start + "<string>".Length)..];
            var end = rest.IndexOf("</string>", StringComparison.Ordinal);
            if (end < 0)
            {
                continue;
            }

            var name = rest[..end].Trim();
            if (name.Length > 0)
            {
                return name;
            }
        }

        return fallback;
    }

    /// <summary>BFS up to 5 levels deep for an .appiconset folder, then picks the largest PNG.</summary>
    private static string? GetIosIconBase64(string projectDir)
    {
        var queue = new List<string> { projectDir };
        string? appIconSet = null;

        for (var depth = 0; depth < 5 && appIconSet is null; depth++)
        {
            var next = new List<string>();
            foreach (var dir in queue)
            {
                IEnumerable<string> subDirs;
                try
                {
                    subDirs = Directory.EnumerateDirectories(dir);
                }
                catch
                {
                    continue;
                }

                foreach (var path in subDirs)
                {
                    if (path.EndsWith(".appiconset", StringComparison.Ordinal))
                    {
                        appIconSet = path;
                        break;
                    }

                    next.Add(path);
                }

                if (appIconSet is not null)
                {
                    break;
                }
            }

            queue = next;
        }

        if (appIconSet is null)
        {
            return null;
        }

        var largest = Directory.EnumerateFiles(appIconSet, "*.png")
            .Select(p => new FileInfo(p))
            .OrderByDescending(f => f.Length)
            .FirstOrDefault();

        if (largest is null)
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(largest.FullName);
            return bytes.Length == 0 ? null : $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
        }
        catch
        {
            return null;
        }
    }
}
