namespace AchDevTool.Models;

/// <summary>Stable ids for the AI tools this app knows how to detect/manage. A product's
/// desktop client and its CLI are separate entries, since they install and configure
/// independently of each other.</summary>
public enum ToolId
{
    Claude,
    Opencode,
    Codex,
    ClaudeDesktop,
    ChatGptDesktop,
}

/// <summary>Product family an entry belongs to; used to group the cards in the UI.</summary>
public enum ToolProduct
{
    Claude,
    Codex,
    Opencode,
}

/// <summary>Whether an entry is a terminal CLI or a desktop application.</summary>
public enum ToolKind
{
    Cli,
    Desktop,
}

/// <summary>How a tool stores its settings, which decides how "configured" is detected.</summary>
public enum ConfigFormat
{
    Json,
    Toml,
    /// <summary>No user-editable config file; presence of the app's data directory is the
    /// only signal that it has been set up (e.g. the ChatGPT desktop app).</summary>
    Directory,
}

public sealed record ToolInfo(
    ToolId Id,
    bool Installed,
    string ConfigPath,
    bool ConfigExists,
    bool Configured);

public sealed record McpEntry(string Name, string Detail);

public sealed record DepStatus(
    string Id,
    string DisplayName,
    bool Installed,
    string InstallCmd,
    bool Installable);
