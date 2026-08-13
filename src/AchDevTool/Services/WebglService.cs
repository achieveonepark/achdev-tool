using AchDevTool.Models;

namespace AchDevTool.Services;

public sealed class WebglService
{
    public List<WebglBuild> GetWebglBuilds(string buildPath)
    {
        var webglPath = Path.Combine(buildPath, "WebGL");
        if (!Directory.Exists(webglPath))
        {
            throw new InvalidOperationException("WebGL 폴더를 찾을 수 없습니다.");
        }

        var builds = new List<WebglBuild>();

        foreach (var dir in Directory.EnumerateDirectories(webglPath))
        {
            var indexPath = Path.Combine(dir, "index.html");
            if (!File.Exists(indexPath))
            {
                continue;
            }

            var folderName = Path.GetFileName(dir);
            var appName = GetWebglName(dir);
            var iconBase64 = GetWebglIconBase64(dir);

            builds.Add(new WebglBuild(folderName, appName, dir, iconBase64));
        }

        return builds.OrderBy(b => b.AppName, StringComparer.Ordinal).ToList();
    }

    /// <summary>Reads the &lt;title&gt; from index.html. Unity's default title is
    /// "Unity Web Player | Game Name", so only the part after the last " | " is kept.</summary>
    private static string GetWebglName(string buildDir)
    {
        var fallback = Path.GetFileName(buildDir);
        var indexPath = Path.Combine(buildDir, "index.html");

        string content;
        try
        {
            content = File.ReadAllText(indexPath);
        }
        catch
        {
            return fallback;
        }

        var start = content.IndexOf("<title>", StringComparison.Ordinal);
        if (start < 0)
        {
            return fallback;
        }

        var rest = content[(start + "<title>".Length)..];
        var end = rest.IndexOf("</title>", StringComparison.Ordinal);
        if (end < 0)
        {
            return fallback;
        }

        var raw = rest[..end].Trim();
        var pipe = raw.LastIndexOf(" | ", StringComparison.Ordinal);
        return pipe >= 0 ? raw[(pipe + 3)..] : raw;
    }

    /// <summary>Extracts the largest PNG-encoded frame from TemplateData/favicon.ico. Modern
    /// favicons are PNG-in-ICO; legacy BMP-only icons are skipped (falls back to a placeholder
    /// in the UI) rather than pulling in a full BMP/DIB decoder for a rarely-hit case.</summary>
    private static string? GetWebglIconBase64(string buildDir)
    {
        var faviconPath = Path.Combine(buildDir, "TemplateData", "favicon.ico");
        if (!File.Exists(faviconPath))
        {
            return null;
        }

        try
        {
            var bytes = File.ReadAllBytes(faviconPath);
            var png = IcoDecoder.ExtractLargestPng(bytes);
            return png is null ? null : $"data:image/png;base64,{Convert.ToBase64String(png)}";
        }
        catch
        {
            return null;
        }
    }
}
