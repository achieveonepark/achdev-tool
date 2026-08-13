namespace AchDevTool.Services;

public static class MimeMap
{
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "application/javascript",
        [".mjs"] = "application/javascript",
        [".css"] = "text/css",
        [".json"] = "application/json",
        [".wasm"] = "application/wasm",
        [".data"] = "application/octet-stream",
        [".mem"] = "application/octet-stream",
        [".unityweb"] = "application/octet-stream",
        [".symbols"] = "application/octet-stream",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".ico"] = "image/x-icon",
        [".txt"] = "text/plain",
        [".xml"] = "application/xml",
        [".wav"] = "audio/wav",
        [".mp3"] = "audio/mpeg",
    };

    public static string GetContentType(string path)
        => Types.TryGetValue(Path.GetExtension(path), out var type) ? type : "application/octet-stream";
}
