using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AchDevTool.Models;

namespace AchDevTool.Services;

/// <summary>Detects installed AI CLIs (claude/opencode/codex), opens their config,
/// installs missing ones, and launches VSCode with an auto-run terminal task.
/// Ported from the Tauri app's ai.rs (absorbed from the standalone "ai-helper" app).</summary>
public sealed class AiToolsService
{
    public sealed record ToolMeta(
        ToolId Id,
        string StringId,
        ToolProduct Product,
        ToolKind Kind,
        string DisplayName,
        ConfigFormat ConfigFormat,
        string Bin = "",
        string Npm = "",
        bool SupportsMcp = false,
        bool HasEditableConfig = true,
        string DownloadUrl = "");

    /// <summary>Listed in display order: each product's CLI first, then its desktop client.</summary>
    public static readonly ToolMeta[] Tools =
    [
        new(ToolId.Claude, "claude", ToolProduct.Claude, ToolKind.Cli, "Claude Code CLI",
            ConfigFormat.Json, Bin: "claude", Npm: "@anthropic-ai/claude-code", SupportsMcp: true),
        new(ToolId.ClaudeDesktop, "claude-desktop", ToolProduct.Claude, ToolKind.Desktop, "Claude 데스크톱 앱",
            ConfigFormat.Json, SupportsMcp: true, DownloadUrl: "https://claude.ai/download"),
        new(ToolId.Codex, "codex", ToolProduct.Codex, ToolKind.Cli, "Codex CLI",
            ConfigFormat.Toml, Bin: "codex", Npm: "@openai/codex", SupportsMcp: true),
        new(ToolId.ChatGptDesktop, "chatgpt-desktop", ToolProduct.Codex, ToolKind.Desktop, "ChatGPT 데스크톱 앱",
            ConfigFormat.Directory, HasEditableConfig: false, DownloadUrl: "https://openai.com/chatgpt/download/"),
        new(ToolId.Opencode, "opencode", ToolProduct.Opencode, ToolKind.Cli, "opencode CLI",
            ConfigFormat.Json, Bin: "opencode", Npm: "opencode-ai", SupportsMcp: true),
    ];

    public static ToolMeta MetaOf(ToolId id) => Tools.First(t => t.Id == id);

    public static string StringIdOf(ToolId id) => MetaOf(id).StringId;

    public static ToolId? ParseToolId(string id)
        => Tools.FirstOrDefault(t => t.StringId == id)?.Id;

    public static string ProductName(ToolProduct product) => product switch
    {
        ToolProduct.Claude => "Claude",
        ToolProduct.Codex => "Codex",
        ToolProduct.Opencode => "opencode",
        _ => product.ToString(),
    };

    /// <summary>Directories likely to contain user-installed CLIs, so a GUI-launched app
    /// (which on macOS does not inherit the shell PATH) can still locate binaries.</summary>
    private static IEnumerable<string> ExtraPathDirs()
    {
        var home = AppPaths.HomeDir;
        foreach (var sub in new[]
                 {
                     ".local/bin", ".opencode/bin", ".bun/bin", ".cargo/bin", ".deno/bin",
                     ".npm-global/bin", ".volta/bin", "bin", ".codex/bin",
                     "AppData/Roaming/npm", "AppData/Local/Programs/Microsoft VS Code/bin",
                 })
        {
            yield return Path.Combine(home, sub.Replace('/', Path.DirectorySeparatorChar));
        }

        foreach (var p in new[]
                 {
                     "/opt/homebrew/bin", "/usr/local/bin", "/usr/bin", "/bin", "/snap/bin",
                     "/usr/share/code/bin",
                     "/Applications/Visual Studio Code.app/Contents/Resources/app/bin",
                     @"C:\Program Files\Microsoft VS Code\bin",
                     @"C:\Program Files (x86)\Microsoft VS Code\bin",
                 })
        {
            yield return p;
        }
    }

    private static IEnumerable<string> BinaryNames(string stem)
        => PlatformUtils.IsWindows ? [$"{stem}.cmd", $"{stem}.exe", $"{stem}.bat", stem] : [stem];

    /// <summary>Tries hard to find an executable: augmented PATH dirs first, then PATH itself.</summary>
    public static FileInfo? FindBinary(string stem)
    {
        var names = BinaryNames(stem).ToList();

        foreach (var dir in ExtraPathDirs())
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(dir, name);
                if (File.Exists(candidate))
                {
                    return new FileInfo(candidate);
                }
            }
        }

        var path = Environment.GetEnvironmentVariable("PATH");
        if (!string.IsNullOrEmpty(path))
        {
            var sep = PlatformUtils.IsWindows ? ';' : ':';
            foreach (var dir in path.Split(sep))
            {
                if (dir.Length == 0)
                {
                    continue;
                }

                foreach (var name in names)
                {
                    var candidate = Path.Combine(dir, name);
                    if (File.Exists(candidate))
                    {
                        return new FileInfo(candidate);
                    }
                }
            }
        }

        return null;
    }

    /// <summary>Locations a product's desktop client is installed to, per-OS. macOS entries are
    /// <c>.app</c> bundles (directories), Windows entries are executables.</summary>
    private static IEnumerable<string> DesktopAppPaths(ToolId id)
    {
        var home = AppPaths.HomeDir;
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        if (id == ToolId.ClaudeDesktop)
        {
            if (PlatformUtils.IsMacOS)
            {
                yield return "/Applications/Claude.app";
                yield return Path.Combine(home, "Applications", "Claude.app");
            }
            else if (PlatformUtils.IsWindows)
            {
                yield return Path.Combine(localAppData, "AnthropicClaude", "claude.exe");
                yield return Path.Combine(localAppData, "Programs", "Claude", "Claude.exe");
            }
        }
        else if (id == ToolId.ChatGptDesktop)
        {
            if (PlatformUtils.IsMacOS)
            {
                yield return "/Applications/ChatGPT.app";
                yield return Path.Combine(home, "Applications", "ChatGPT.app");
            }
            else if (PlatformUtils.IsWindows)
            {
                yield return Path.Combine(localAppData, "Programs", "ChatGPT", "ChatGPT.exe");
            }
        }
    }

    public static bool IsInstalled(ToolId id)
    {
        var meta = MetaOf(id);
        return meta.Kind == ToolKind.Cli
            ? FindBinary(meta.Bin) is not null
            : DesktopAppPaths(id).Any(p => Directory.Exists(p) || File.Exists(p));
    }

    /// <summary>Resolve the on-disk config for a tool, per-OS conventions. For entries with no
    /// user-editable config this is the app's data directory instead of a file.</summary>
    public static string ConfigPath(ToolId id)
    {
        var home = AppPaths.HomeDir;
        return id switch
        {
            ToolId.Claude => Path.Combine(home, ".claude", "settings.json"),
            ToolId.Opencode => Path.Combine(XdgConfigBase(), "opencode", "opencode.json"),
            ToolId.Codex => Path.Combine(home, ".codex", "config.toml"),
            ToolId.ClaudeDesktop => ClaudeDesktopConfigPath(),
            ToolId.ChatGptDesktop => ChatGptDataDir(),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }

    private static string XdgConfigBase()
        => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(AppPaths.HomeDir, ".config");

    /// <summary>Claude Desktop keeps its MCP servers here (separate from the CLI's settings).</summary>
    private static string ClaudeDesktopConfigPath()
    {
        const string file = "claude_desktop_config.json";

        if (PlatformUtils.IsWindows)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Claude", file);
        }

        if (PlatformUtils.IsMacOS)
        {
            return Path.Combine(AppPaths.HomeDir, "Library", "Application Support", "Claude", file);
        }

        return Path.Combine(XdgConfigBase(), "Claude", file);
    }

    private static string ChatGptDataDir()
    {
        if (PlatformUtils.IsWindows)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "ChatGPT");
        }

        if (PlatformUtils.IsMacOS)
        {
            return Path.Combine(AppPaths.HomeDir, "Library", "Application Support", "com.openai.chat");
        }

        return Path.Combine(XdgConfigBase(), "ChatGPT");
    }

    private static string ConfigTemplate(ToolId id) => id switch
    {
        ToolId.Claude => "{\n  \n}\n",
        ToolId.ClaudeDesktop => "{\n  \n}\n",
        ToolId.Opencode => "{\n  \"$schema\": \"https://opencode.ai/config.json\"\n}\n",
        ToolId.Codex => "# Codex configuration (https://github.com/openai/codex)\n",
        _ => "",
    };

    /// <summary>Whether a tool has actually been set up, as opposed to merely having a config file.
    /// An empty file or a bare <c>{}</c> — which is exactly what our own "설정 열기" button creates —
    /// must not count as configured.</summary>
    public static bool IsConfigured(ToolId id)
        => HasMeaningfulConfig(ConfigPath(id), MetaOf(id).ConfigFormat);

    internal static bool HasMeaningfulConfig(string path, ConfigFormat format) => format switch
    {
        ConfigFormat.Directory => DirectoryHasEntries(path),
        ConfigFormat.Json => ReadJsonc(path) is JsonObject o && o.Count > 0,
        ConfigFormat.Toml => HasTomlContent(path),
        _ => false,
    };

    private static bool DirectoryHasEntries(string path)
    {
        try
        {
            return Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the TOML file has at least one line that is not blank or a comment.</summary>
    private static bool HasTomlContent(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }

        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Trim();
                if (line.Length > 0 && !line.StartsWith('#'))
                {
                    return true;
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public List<ToolInfo> ListTools()
        => Tools.Select(t =>
        {
            var cfg = ConfigPath(t.Id);
            var exists = t.ConfigFormat == ConfigFormat.Directory
                ? Directory.Exists(cfg)
                : File.Exists(cfg);
            return new ToolInfo(t.Id, IsInstalled(t.Id), cfg, exists, IsConfigured(t.Id));
        }).ToList();

    public bool HasCode() => FindBinary("code") is not null;

    /// <summary>Parses JSON tolerant of comments/trailing commas (common in VSCode config files).</summary>
    public static JsonNode? ReadJsonc(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            var text = File.ReadAllText(path);
            var options = new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
            return JsonNode.Parse(text, documentOptions: options);
        }
        catch
        {
            return null;
        }
    }

    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>Open a path: prefer VSCode (consistent with this app), else OS default handler.</summary>
    private void OpenPath(string path)
    {
        var code = FindBinary("code");
        if (code is not null)
        {
            var psi = new ProcessStartInfo(code.FullName) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add(path);
            Process.Start(psi);
            return;
        }

        PlatformUtils.OpenWithSystem(path);
    }

    public string OpenConfig(ToolId tool)
    {
        var meta = MetaOf(tool);
        if (!meta.HasEditableConfig)
        {
            throw new InvalidOperationException(
                $"{meta.DisplayName}은(는) 편집할 수 있는 설정 파일이 없습니다. 앱 안에서 설정하세요.");
        }

        var path = ConfigPath(tool);
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        if (!File.Exists(path))
        {
            File.WriteAllText(path, ConfigTemplate(tool));
        }

        OpenPath(path);
        return $"Opened {path}";
    }

    /// <summary>Launches an OS-appropriate terminal running <paramref name="cmd"/> so the user
    /// can watch the install (and provide any required input, e.g. sudo password).</summary>
    public static void RunInTerminal(string cmd)
    {
        if (PlatformUtils.IsMacOS)
        {
            var escaped = cmd.Replace("\\", "\\\\").Replace("\"", "\\\"");
            var script = $"tell application \"Terminal\"\nactivate\ndo script \"{escaped}\"\nend tell";
            var psi = new ProcessStartInfo("osascript") { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add(script);
            Process.Start(psi);
            return;
        }

        if (PlatformUtils.IsWindows)
        {
            var psi = new ProcessStartInfo("cmd") { UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in new[] { "/c", "start", "AI Helper Install", "cmd", "/k", cmd })
            {
                psi.ArgumentList.Add(a);
            }
            Process.Start(psi);
            return;
        }

        foreach (var term in new[] { "x-terminal-emulator", "gnome-terminal", "konsole", "xterm" })
        {
            var found = FindBinary(term);
            if (found is null)
            {
                continue;
            }

            var hold = $"{cmd}; echo; echo '[done]'; exec $SHELL";
            var psi = new ProcessStartInfo(found.FullName) { UseShellExecute = false, CreateNoWindow = true };
            psi.ArgumentList.Add("-e");
            psi.ArgumentList.Add("bash");
            psi.ArgumentList.Add("-lc");
            psi.ArgumentList.Add(hold);
            Process.Start(psi);
            return;
        }

        throw new InvalidOperationException("No terminal emulator found.");
    }

    public string InstallTool(ToolId tool)
    {
        var meta = MetaOf(tool);

        // Desktop clients are not npm packages — send the user to the official download page.
        if (meta.Kind == ToolKind.Desktop)
        {
            PlatformUtils.OpenWithSystem(meta.DownloadUrl);
            return $"{meta.DisplayName} 다운로드 페이지를 열었습니다: {meta.DownloadUrl}";
        }

        var cmd = $"npm install -g {meta.Npm}";
        RunInTerminal(cmd);
        return $"터미널에서 {meta.DisplayName} 설치 중: `{cmd}`. 끝나면 새로고침을 누르세요.";
    }

    /// <summary>Merges an auto-run task into a (possibly existing) .vscode/tasks.json, preserving
    /// any tasks the user already had (comments in the original file are not preserved, matching
    /// the previous Rust implementation which also round-tripped through a plain JSON value).</summary>
    internal static void WriteTasksJson(string vscodeDir, string toolId, string command)
    {
        var tasksPath = Path.Combine(vscodeDir, "tasks.json");
        var label = $"AI Helper: {toolId}";

        var root = ReadJsonc(tasksPath) as JsonObject ?? new JsonObject();
        root["version"] ??= "2.0.0";

        var existingTasks = (root["tasks"] as JsonArray)?
            .Where(t => t?["label"]?.GetValue<string>() is not { } l || !l.StartsWith("AI Helper:"))
            .Select(t => t!.DeepClone())
            .ToList() ?? [];

        var newTask = new JsonObject
        {
            ["label"] = label,
            ["type"] = "shell",
            ["command"] = command,
            ["presentation"] = new JsonObject
            {
                ["reveal"] = "always",
                ["panel"] = "dedicated",
                ["focus"] = true,
                ["clear"] = true,
            },
            ["runOptions"] = new JsonObject { ["runOn"] = "folderOpen" },
            ["problemMatcher"] = new JsonArray(),
        };
        existingTasks.Add(newTask);

        var tasksArray = new JsonArray();
        foreach (var t in existingTasks)
        {
            tasksArray.Add(t);
        }
        root["tasks"] = tasksArray;

        Directory.CreateDirectory(vscodeDir);
        File.WriteAllText(tasksPath, root.ToJsonString(PrettyJson));
    }

    /// <summary>Path to VSCode's global (user) settings.json, per-OS.</summary>
    private static string VscodeUserSettingsPath()
    {
        if (PlatformUtils.IsWindows)
        {
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            return Path.Combine(appData, "Code", "User", "settings.json");
        }

        if (PlatformUtils.IsMacOS)
        {
            return Path.Combine(AppPaths.HomeDir, "Library", "Application Support", "Code", "User", "settings.json");
        }

        return Path.Combine(AppPaths.HomeDir, ".config", "Code", "User", "settings.json");
    }

    /// <summary>Inserts "&lt;key&gt;": "&lt;value&gt;" into a JSONC document via raw string
    /// manipulation, preserving the user's existing comments/formatting. Returns null if the key
    /// is already present (respecting whatever the user already set).</summary>
    internal static string? InsertJsoncSetting(string text, string key, string value)
    {
        if (text.Contains(key))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return $"{{\n  \"{key}\": \"{value}\"\n}}\n";
        }

        var idx = text.IndexOf('{');
        if (idx < 0)
        {
            return $"{{\n  \"{key}\": \"{value}\"\n}}\n";
        }

        var after = text[(idx + 1)..];
        var entry = after.TrimStart().StartsWith('}')
            ? $"\n  \"{key}\": \"{value}\"\n"
            : $"\n  \"{key}\": \"{value}\",";

        return text[..(idx + 1)] + entry + after;
    }

    private static void EnableAutomaticTasks()
    {
        var path = VscodeUserSettingsPath();
        var text = File.Exists(path) ? File.ReadAllText(path) : "";

        var newText = InsertJsoncSetting(text, "task.allowAutomaticTasks", "on");
        if (newText is null)
        {
            return;
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, newText);
    }

    /// <summary>Opens `folder` in VSCode, optionally auto-running `tool` in the terminal.</summary>
    public string OpenInVscode(string folder, ToolId tool, bool autoRun)
    {
        var meta = MetaOf(tool);
        if (meta.Kind != ToolKind.Cli)
        {
            throw new InvalidOperationException(
                $"{meta.DisplayName}은(는) 터미널에서 실행할 수 없습니다. CLI를 선택하세요.");
        }

        var toolBin = FindBinary(meta.Bin) ?? throw new InvalidOperationException($"{meta.DisplayName}이(가) 설치되어 있지 않습니다.");

        if (!Directory.Exists(folder))
        {
            throw new InvalidOperationException($"Not a directory: {folder}");
        }

        if (autoRun)
        {
            var vscodeDir = Path.Combine(folder, ".vscode");
            Directory.CreateDirectory(vscodeDir);
            WriteTasksJson(vscodeDir, meta.StringId, toolBin.FullName);
            EnableAutomaticTasks();
        }

        var codeBin = FindBinary("code")
            ?? throw new InvalidOperationException(
                "Could not find the 'code' command. Install VSCode and run \"Shell Command: Install 'code' command in PATH\" from the command palette.");

        var psi = new ProcessStartInfo(codeBin.FullName) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("-n");
        psi.ArgumentList.Add(folder);
        Process.Start(psi);

        return autoRun
            ? $"Opened {folder} in VSCode — '{meta.StringId}' will start in the integrated terminal."
            : $"Opened {folder} in VSCode.";
    }
}
