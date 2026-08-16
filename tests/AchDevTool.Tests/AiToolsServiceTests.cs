using System.Text.Json.Nodes;
using AchDevTool.Models;
using AchDevTool.Services;
using Xunit;

namespace AchDevTool.Tests;

public class AiToolsServiceTests
{
    [Fact]
    public void MergesIntoExistingJsoncPreservingUserTasks()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aih-test-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "tasks.json"), """
                {
                  // user's own config
                  "version": "2.0.0",
                  "tasks": [
                    { "label": "build", "type": "shell", "command": "make", },
                    { "label": "AI Helper: codex", "type": "shell", "command": "codex" }
                  ],
                }
                """);

            AiToolsService.WriteTasksJson(dir, "claude", "/abs/path/claude");

            var parsed = AiToolsService.ReadJsonc(Path.Combine(dir, "tasks.json"));
            var tasks = parsed!["tasks"]!.AsArray();
            var labels = tasks.Select(t => t!["label"]!.GetValue<string>()).ToList();

            Assert.Contains("build", labels);
            Assert.Contains("AI Helper: claude", labels);
            Assert.Single(labels, l => l.StartsWith("AI Helper:"));

            var ours = tasks.First(t => t!["label"]!.GetValue<string>() == "AI Helper: claude")!;
            Assert.Equal("/abs/path/claude", ours["command"]!.GetValue<string>());
            Assert.Equal("folderOpen", ours["runOptions"]!["runOn"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AddsAndListsCodexMcpRoundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aih-mcp-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "config.toml");
            File.WriteAllText(path, "model = \"o3\"\n");

            McpService.AddCodexMcp(path, "files", "npx", ["-y", "@modelcontextprotocol/server-filesystem"]);

            var text = File.ReadAllText(path);
            Assert.Contains("model = \"o3\"", text);

            var entries = McpService.ListCodexMcps(path);
            var files = entries.Single(e => e.Name == "files");
            Assert.Contains("npx", files.Detail);
            Assert.Contains("server-filesystem", files.Detail);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AddsAndListsJsonMcpRoundtrip()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aih-mcpj-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, ".claude.json");
            File.WriteAllText(path, """{ "numStartups": 3 }""");

            McpService.AddJsonMcp(path, "mcpServers", "files", new JsonObject
            {
                ["command"] = "npx",
                ["args"] = new JsonArray("-y", "server-filesystem"),
            });

            var root = AiToolsService.ReadJsonc(path)!;
            Assert.Equal(3, root["numStartups"]!.GetValue<int>());

            var entries = McpService.ListJsonMcps(path, "mcpServers");
            var files = entries.Single(e => e.Name == "files");
            Assert.Equal("npx -y server-filesystem", files.Detail);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void InsertsSettingPreservingExistingContentAndComments()
    {
        const string src = "{\n  // user comment\n  \"editor.fontSize\": 13\n}\n";
        var outText = AiToolsService.InsertJsoncSetting(src, "task.allowAutomaticTasks", "on")!;
        Assert.Contains("// user comment", outText);
        Assert.Contains("\"editor.fontSize\": 13", outText);
        Assert.Contains("\"task.allowAutomaticTasks\": \"on\"", outText);

        var options = new System.Text.Json.JsonDocumentOptions { CommentHandling = System.Text.Json.JsonCommentHandling.Skip };
        var parsed = JsonNode.Parse(outText, documentOptions: options)!;
        Assert.Equal("on", parsed["task.allowAutomaticTasks"]!.GetValue<string>());

        var out2 = AiToolsService.InsertJsoncSetting("{}", "task.allowAutomaticTasks", "on")!;
        Assert.Equal("on", JsonNode.Parse(out2)!["task.allowAutomaticTasks"]!.GetValue<string>());

        var out3 = AiToolsService.InsertJsoncSetting("", "task.allowAutomaticTasks", "on")!;
        Assert.Equal("on", JsonNode.Parse(out3)!["task.allowAutomaticTasks"]!.GetValue<string>());

        Assert.Null(AiToolsService.InsertJsoncSetting(outText, "task.allowAutomaticTasks", "on"));
    }

    [Fact]
    public void ConfigPathsAreDefinedForAllTools()
    {
        foreach (var tool in AiToolsService.Tools)
        {
            var path = AiToolsService.ConfigPath(tool.Id);
            Assert.True(Path.IsPathRooted(path));
        }
    }

    [Fact]
    public void EveryToolHasADistinctStringIdThatRoundTrips()
    {
        var ids = AiToolsService.Tools.Select(t => t.StringId).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());

        foreach (var tool in AiToolsService.Tools)
        {
            Assert.Equal(tool.Id, AiToolsService.ParseToolId(tool.StringId));
        }

        Assert.Null(AiToolsService.ParseToolId("not-a-tool"));
    }

    /// <summary>A CLI must have an npm package to install; a desktop client must have a
    /// download page instead, since it is not an npm package.</summary>
    [Fact]
    public void CliAndDesktopEntriesCarryTheirOwnInstallRoute()
    {
        foreach (var tool in AiToolsService.Tools)
        {
            if (tool.Kind == ToolKind.Cli)
            {
                Assert.NotEqual("", tool.Bin);
                Assert.NotEqual("", tool.Npm);
            }
            else
            {
                Assert.NotEqual("", tool.DownloadUrl);
            }
        }
    }

    [Fact]
    public void EmptyOrTemplateConfigsDoNotCountAsConfigured()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"aih-cfg-{Environment.ProcessId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var json = Path.Combine(dir, "settings.json");

            // Missing entirely.
            Assert.False(AiToolsService.HasMeaningfulConfig(json, ConfigFormat.Json));

            // The exact template our own "설정 열기" button writes must not read as configured.
            File.WriteAllText(json, "{\n  \n}\n");
            Assert.False(AiToolsService.HasMeaningfulConfig(json, ConfigFormat.Json));

            File.WriteAllText(json, "{}");
            Assert.False(AiToolsService.HasMeaningfulConfig(json, ConfigFormat.Json));

            File.WriteAllText(json, """{ "model": "opus" }""");
            Assert.True(AiToolsService.HasMeaningfulConfig(json, ConfigFormat.Json));

            var toml = Path.Combine(dir, "config.toml");
            Assert.False(AiToolsService.HasMeaningfulConfig(toml, ConfigFormat.Toml));

            File.WriteAllText(toml, "# Codex configuration (https://github.com/openai/codex)\n\n");
            Assert.False(AiToolsService.HasMeaningfulConfig(toml, ConfigFormat.Toml));

            File.WriteAllText(toml, "# comment\nmodel = \"o3\"\n");
            Assert.True(AiToolsService.HasMeaningfulConfig(toml, ConfigFormat.Toml));

            var dataDir = Path.Combine(dir, "appdata");
            Assert.False(AiToolsService.HasMeaningfulConfig(dataDir, ConfigFormat.Directory));

            Directory.CreateDirectory(dataDir);
            Assert.False(AiToolsService.HasMeaningfulConfig(dataDir, ConfigFormat.Directory));

            File.WriteAllText(Path.Combine(dataDir, "state"), "x");
            Assert.True(AiToolsService.HasMeaningfulConfig(dataDir, ConfigFormat.Directory));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
