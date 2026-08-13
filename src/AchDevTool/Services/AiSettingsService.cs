using System.Text.Json;
using System.Text.Json.Serialization;

namespace AchDevTool.Services;

/// <summary>Small JSON-file-backed settings store for the AI tab, replacing the old
/// Tauri frontend's localStorage usage (recent folders, last-picked tool, autorun toggle).</summary>
public sealed class AiSettingsService
{
    private const int MaxRecent = 6;
    private static readonly string SettingsFile = Path.Combine(AppPaths.DataDir, "ai_settings.json");

    private sealed class Data
    {
        [JsonPropertyName("recent")] public List<string> Recent { get; set; } = [];
        [JsonPropertyName("tool")] public string? Tool { get; set; }
        [JsonPropertyName("autorun")] public bool Autorun { get; set; } = true;
    }

    private Data _data = Load();

    private static Data Load()
    {
        try
        {
            if (File.Exists(SettingsFile))
            {
                var json = File.ReadAllText(SettingsFile);
                return JsonSerializer.Deserialize<Data>(json) ?? new Data();
            }
        }
        catch
        {
            // fall through to defaults
        }

        return new Data();
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // best-effort persistence, same tolerance as the old localStorage calls
        }
    }

    public List<string> LoadRecent() => [.. _data.Recent];

    public void PushRecent(string folder)
    {
        _data.Recent = new[] { folder }.Concat(_data.Recent.Where(f => f != folder)).Take(MaxRecent).ToList();
        Save();
    }

    public string? LoadSelectedTool() => _data.Tool;

    public void SaveSelectedTool(string toolId)
    {
        _data.Tool = toolId;
        Save();
    }

    public bool LoadAutorun() => _data.Autorun;

    public void SaveAutorun(bool value)
    {
        _data.Autorun = value;
        Save();
    }
}
