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
    public sealed record ToolMeta(ToolId Id, string StringId, string Bin, string Npm);

    public static readonly ToolMeta[] Tools =
    [
        new(ToolId.Claude, "claude", "claude", "@anthropic-ai/claude-code"),
        new(ToolId.Opencode, "opencode", "opencode", "opencode-ai"),
        new(ToolId.Codex, "codex", "codex", "@openai/codex"),
    ];

    public static ToolMeta MetaOf(ToolId id) => Tools.First(t => t.Id == id);

    public static string StringIdOf(ToolId id) => MetaOf(id).StringId;

    public static ToolId? ParseToolId(string id) => id switch
    {
        "claude" => ToolId.Claude,
        "opencode" => ToolId.Opencode,
        "codex" => ToolId.Codex,
        _ => null,
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

    /// <summary>Resolve the on-disk config file for a tool, per-OS conventions.</summary>
    public static string ConfigPath(ToolId id)
    {
        var home = AppPaths.HomeDir;
        return id switch
        {
            ToolId.Claude => Path.Combine(home, ".claude", "settings.json"),
            ToolId.Opencode => Path.Combine(OpencodeConfigBase(), "opencode", "opencode.json"),
            ToolId.Codex => Path.Combine(home, ".codex", "config.toml"),
            _ => throw new ArgumentOutOfRangeException(nameof(id)),
        };
    }

    private static string OpencodeConfigBase()
        => Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(AppPaths.HomeDir, ".config");

    private static string ConfigTemplate(ToolId id) => id switch
    {
        ToolId.Claude => "{\n  \n}\n",
        ToolId.Opencode => "{\n  \"$schema\": \"https://opencode.ai/config.json\"\n}\n",
        ToolId.Codex => "# Codex configuration (https://github.com/openai/codex)\n",
        _ => "",
    };

    public List<ToolInfo> ListTools()
        => Tools.Select(t =>
        {
            var cfg = ConfigPath(t.Id);
            return new ToolInfo(t.Id, FindBinary(t.Bin) is not null, cfg, File.Exists(cfg));
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
        var cmd = $"npm install -g {meta.Npm}";
        RunInTerminal(cmd);
        return $"Installing {meta.StringId} in a terminal: `{cmd}`. Click Refresh when it finishes.";
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
        var toolBin = FindBinary(meta.Bin) ?? throw new InvalidOperationException($"{meta.StringId} is not installed.");

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
