using System.Text.Json;
using System.Text.Json.Nodes;
using AchDevTool.Models;
using Tomlyn;
using Tomlyn.Model;

namespace AchDevTool.Services;

/// <summary>Reads/writes the MCP server registrations for each AI CLI. claude/opencode keep
/// theirs as JSON, codex as TOML — ported from ai.rs's MCP section.</summary>
public sealed class McpService
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    /// <summary>Where each tool stores its MCP server definitions (differs from the general
    /// config file for Claude, which keeps MCP in ~/.claude.json).</summary>
    private static string McpPath(ToolId id) => id switch
    {
        ToolId.Claude => Path.Combine(AppPaths.HomeDir, ".claude.json"),
        ToolId.Opencode => AiToolsService.ConfigPath(ToolId.Opencode),
        ToolId.Codex => AiToolsService.ConfigPath(ToolId.Codex),
        // The desktop client keeps its own MCP list, separate from the CLI's.
        ToolId.ClaudeDesktop => AiToolsService.ConfigPath(ToolId.ClaudeDesktop),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public List<McpEntry> ListMcps(ToolId tool)
    {
        if (!AiToolsService.MetaOf(tool).SupportsMcp)
        {
            return [];
        }

        var path = McpPath(tool);
        return tool switch
        {
            ToolId.Claude => ListJsonMcps(path, "mcpServers"),
            ToolId.ClaudeDesktop => ListJsonMcps(path, "mcpServers"),
            ToolId.Opencode => ListJsonMcps(path, "mcp"),
            ToolId.Codex => ListCodexMcps(path),
            _ => [],
        };
    }

    public string AddMcp(ToolId tool, string name, string command)
    {
        var meta = AiToolsService.MetaOf(tool);
        if (!meta.SupportsMcp)
        {
            throw new InvalidOperationException($"{meta.DisplayName}은(는) MCP 서버 등록을 지원하지 않습니다.");
        }

        name = name.Trim();
        if (name.Length == 0)
        {
            throw new InvalidOperationException("MCP name is required.");
        }

        var (prog, args) = Tokenize(command.Trim());
        var path = McpPath(tool);

        switch (tool)
        {
            case ToolId.Claude:
            case ToolId.ClaudeDesktop:
                AddJsonMcp(path, "mcpServers", name, new JsonObject
                {
                    ["command"] = prog,
                    ["args"] = new JsonArray(args.Select(a => (JsonNode)a!).ToArray()),
                });
                break;
            case ToolId.Opencode:
                var cmdArray = new JsonArray { prog };
                foreach (var a in args)
                {
                    cmdArray.Add(a);
                }
                AddJsonMcp(path, "mcp", name, new JsonObject
                {
                    ["type"] = "local",
                    ["command"] = cmdArray,
                    ["enabled"] = true,
                });
                break;
            case ToolId.Codex:
                AddCodexMcp(path, name, prog, args);
                break;
        }

        return $"{meta.DisplayName}에 MCP '{name}'을(를) 등록했습니다: {path}";
    }

    private static (string Prog, List<string> Args) Tokenize(string command)
    {
        var parts = command.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (parts.Count == 0)
        {
            throw new InvalidOperationException("Command is empty.");
        }

        var prog = parts[0];
        parts.RemoveAt(0);
        return (prog, parts);
    }

    private static string JsonMcpDetail(JsonNode? v)
    {
        if (v is null)
        {
            return "";
        }

        if (v["url"]?.GetValue<string>() is { } url)
        {
            return url;
        }

        string ArgsOf(string key) => v[key] is JsonArray arr
            ? string.Join(" ", arr.Select(x => x?.GetValue<string>()).Where(s => s is not null))
            : "";

        var command = v["command"];
        if (command is JsonValue val && val.TryGetValue<string>(out var s))
        {
            return $"{s} {ArgsOf("args")}".Trim();
        }

        if (command is JsonArray cmdArr)
        {
            return string.Join(" ", cmdArr.Select(x => x?.GetValue<string>()).Where(x => x is not null));
        }

        return "";
    }

    internal static List<McpEntry> ListJsonMcps(string path, string container)
    {
        var root = AiToolsService.ReadJsonc(path);
        if (root?[container] is not JsonObject obj)
        {
            return [];
        }

        return obj.Select(kv => new McpEntry(kv.Key, JsonMcpDetail(kv.Value))).ToList();
    }

    internal static List<McpEntry> ListCodexMcps(string path)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        try
        {
            var model = TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path), TomlSerializerOptions.Default) ?? new TomlTable();
            if (model.TryGetValue("mcp_servers", out var serversObj) && serversObj is TomlTable servers)
            {
                var result = new List<McpEntry>();
                foreach (var kv in servers)
                {
                    if (kv.Value is not TomlTable item)
                    {
                        continue;
                    }

                    var cmd = item.TryGetValue("command", out var c) ? c as string ?? "" : "";
                    var argsStr = item.TryGetValue("args", out var a) && a is TomlArray arr
                        ? string.Join(" ", arr.OfType<string>())
                        : "";
                    result.Add(new McpEntry(kv.Key, $"{cmd} {argsStr}".Trim()));
                }

                return result;
            }
        }
        catch
        {
            // fall through to empty list, same as the Rust implementation's Ok(_) => Vec::new()
        }

        return [];
    }

    internal static void AddJsonMcp(string path, string container, string name, JsonNode entry)
    {
        var root = AiToolsService.ReadJsonc(path) as JsonObject ?? new JsonObject();
        if (root[container] is not JsonObject map)
        {
            map = new JsonObject();
            root[container] = map;
        }

        map[name] = entry;

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, root.ToJsonString(PrettyJson));
    }

    internal static void AddCodexMcp(string path, string name, string prog, List<string> args)
    {
        TomlTable model;
        try
        {
            model = File.Exists(path)
                ? TomlSerializer.Deserialize<TomlTable>(File.ReadAllText(path), TomlSerializerOptions.Default) ?? new TomlTable()
                : new TomlTable();
        }
        catch (Exception e)
        {
            throw new InvalidOperationException($"parse config.toml: {e.Message}");
        }

        if (!model.TryGetValue("mcp_servers", out var serversObj) || serversObj is not TomlTable servers)
        {
            servers = new TomlTable();
            model["mcp_servers"] = servers;
        }

        var argsArray = new TomlArray();
        foreach (var a in args)
        {
            argsArray.Add(a);
        }

        servers[name] = new TomlTable
        {
            ["command"] = prog,
            ["args"] = argsArray,
        };

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, TomlSerializer.Serialize(model, TomlSerializerOptions.Default));
    }
}
