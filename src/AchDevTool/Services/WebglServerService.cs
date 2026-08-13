using System.Net;

namespace AchDevTool.Services;

/// <summary>Serves a WebGL build directory over HTTP for local preview. Replaces the old
/// Tauri backend's "spawn python3 -m http.server" approach with an in-process
/// HttpListener, so the feature no longer depends on Python being installed.</summary>
public sealed class WebglServerService
{
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;

    public bool IsRunning => _listener is { IsListening: true };

    public string Start(string buildPath, int port)
    {
        Stop();

        var rootDir = Path.GetFullPath(buildPath);
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://localhost:{port}/");

        try
        {
            listener.Start();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"서버 시작 실패: {e.Message}");
        }

        _listener = listener;
        var cts = new CancellationTokenSource();
        _cts = cts;

        _ = Task.Run(() => AcceptLoopAsync(listener, rootDir, cts.Token));

        var url = $"http://localhost:{port}";
        PlatformUtils.OpenWithSystem(url);
        return $"서버 시작됨: {url}";
    }

    public string Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { /* already stopped */ }
        try { _listener?.Close(); } catch { /* already closed */ }
        _listener = null;
        _cts = null;
        return "서버가 중지되었습니다.";
    }

    private static async Task AcceptLoopAsync(HttpListener listener, string rootDir, CancellationToken token)
    {
        while (!token.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch
            {
                break; // listener was stopped/disposed
            }

            _ = HandleRequestAsync(ctx, rootDir);
        }
    }

    private static async Task HandleRequestAsync(HttpListenerContext ctx, string rootDir)
    {
        try
        {
            var requestPath = Uri.UnescapeDataString(ctx.Request.Url?.AbsolutePath ?? "/");
            if (requestPath is "/" or "")
            {
                requestPath = "/index.html";
            }

            var relative = requestPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar);
            var fullPath = Path.GetFullPath(Path.Combine(rootDir, relative));

            // Guard against `..` path traversal escaping the build directory.
            if (!fullPath.StartsWith(rootDir, StringComparison.Ordinal) || !File.Exists(fullPath))
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                return;
            }

            ctx.Response.ContentType = MimeMap.GetContentType(fullPath);
            var bytes = await File.ReadAllBytesAsync(fullPath).ConfigureAwait(false);
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
            ctx.Response.Close();
        }
        catch
        {
            try { ctx.Response.Close(); } catch { /* client likely disconnected */ }
        }
    }
}
