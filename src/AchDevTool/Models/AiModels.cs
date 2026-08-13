namespace AchDevTool.Models;

/// <summary>Stable ids for the AI CLIs this app knows how to detect/manage.</summary>
public enum ToolId
{
    Claude,
    Opencode,
    Codex,
}

public sealed record ToolInfo(
    ToolId Id,
    bool Installed,
    string ConfigPath,
    bool ConfigExists);

public sealed record McpEntry(string Name, string Detail);

public sealed record DepStatus(
    string Id,
    string DisplayName,
    bool Installed,
    string InstallCmd,
    bool Installable);
