using System.Collections.ObjectModel;
using AchDevTool.Models;
using AchDevTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AchDevTool.ViewModels;

public partial class ToolCardViewModel : ObservableObject
{
    private readonly AiToolsService _aiToolsService;
    private readonly McpService _mcpService;
    private readonly Action<string, string> _setStatus;
    private readonly Action<ToolId> _onChoose;

    public ToolId Id { get; }
    public string StringId { get; }
    public string DisplayName { get; }
    public ToolKind Kind { get; }
    public ToolProduct Product { get; }

    /// <summary>"클라이언트" / "CLI" chip shown next to the name.</summary>
    public string KindLabel { get; }

    /// <summary>Where the settings live, shown under the name so the user can find them.</summary>
    public string ConfigPath { get; }

    [ObservableProperty]
    private bool installed;

    [ObservableProperty]
    private bool configured;

    public bool NotInstalled => !Installed;
    public string InstalledLabel => Installed ? "설치됨" : "미설치";

    /// <summary>Setup state is only meaningful once the tool itself is present.</summary>
    public string ConfiguredLabel => Configured ? "설정됨" : "설정 없음";

    public bool ShowConfigured => Installed;
    public bool NotConfigured => Installed && !Configured;

    /// <summary>Only a CLI can be auto-run in VSCode's terminal, so only a CLI is selectable.</summary>
    public bool CanBeChosen => Installed && Kind == ToolKind.Cli;

    public bool CanOpenConfig => Installed && _hasEditableConfig;
    public bool CanShowMcp => Installed && _supportsMcp;

    /// <summary>Desktop clients are not npm packages — the button opens their download page.</summary>
    public string InstallLabel => Kind == ToolKind.Desktop ? "다운로드" : "설치";

    private readonly bool _hasEditableConfig;
    private readonly bool _supportsMcp;

    partial void OnInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(NotInstalled));
        OnPropertyChanged(nameof(InstalledLabel));
        OnPropertyChanged(nameof(ShowConfigured));
        OnPropertyChanged(nameof(NotConfigured));
        OnPropertyChanged(nameof(CanBeChosen));
        OnPropertyChanged(nameof(CanOpenConfig));
        OnPropertyChanged(nameof(CanShowMcp));
    }

    partial void OnConfiguredChanged(bool value)
    {
        OnPropertyChanged(nameof(ConfiguredLabel));
        OnPropertyChanged(nameof(NotConfigured));
    }

    [ObservableProperty]
    private bool isChosen;

    [ObservableProperty]
    private bool isMcpPanelVisible;

    [ObservableProperty]
    private bool isMcpLoading;

    [ObservableProperty]
    private string mcpNameInput = "";

    [ObservableProperty]
    private string mcpCommandInput = "";

    public ObservableCollection<McpEntry> McpEntries { get; } = [];

    public ToolCardViewModel(
        ToolInfo info,
        AiToolsService aiToolsService,
        McpService mcpService,
        Action<string, string> setStatus,
        Action<ToolId> onChoose)
    {
        var meta = AiToolsService.MetaOf(info.Id);
        Id = info.Id;
        StringId = meta.StringId;
        DisplayName = meta.DisplayName;
        Kind = meta.Kind;
        Product = meta.Product;
        KindLabel = meta.Kind == ToolKind.Desktop ? "클라이언트" : "CLI";
        ConfigPath = info.ConfigPath;
        _hasEditableConfig = meta.HasEditableConfig;
        _supportsMcp = meta.SupportsMcp;
        Installed = info.Installed;
        Configured = info.Configured;
        _aiToolsService = aiToolsService;
        _mcpService = mcpService;
        _setStatus = setStatus;
        _onChoose = onChoose;
    }

    [RelayCommand]
    private void Choose() => _onChoose(Id);

    [RelayCommand]
    private void OpenConfig()
    {
        try
        {
            _setStatus(_aiToolsService.OpenConfig(Id), "ok");
        }
        catch (Exception e)
        {
            _setStatus(e.Message, "error");
        }
    }

    [RelayCommand]
    private void Install()
    {
        try
        {
            _setStatus(_aiToolsService.InstallTool(Id), "info");
        }
        catch (Exception e)
        {
            _setStatus(e.Message, "error");
        }
    }

    [RelayCommand]
    private async Task ToggleMcpAsync()
    {
        var willShow = !IsMcpPanelVisible;
        IsMcpPanelVisible = willShow;
        if (willShow)
        {
            await LoadMcpEntriesAsync();
        }
    }

    private async Task LoadMcpEntriesAsync()
    {
        IsMcpLoading = true;
        McpEntries.Clear();
        try
        {
            var entries = await Task.Run(() => _mcpService.ListMcps(Id));
            foreach (var e in entries)
            {
                McpEntries.Add(e);
            }
        }
        catch (Exception e)
        {
            McpEntries.Add(new McpEntry("", e.Message));
        }
        finally
        {
            IsMcpLoading = false;
        }
    }

    [RelayCommand]
    private async Task AddMcpAsync()
    {
        var name = McpNameInput.Trim();
        var command = McpCommandInput.Trim();
        if (name.Length == 0 || command.Length == 0)
        {
            _setStatus("MCP 이름과 명령을 모두 입력해야 합니다.", "error");
            return;
        }

        try
        {
            _setStatus(_mcpService.AddMcp(Id, name, command), "ok");
            McpNameInput = "";
            McpCommandInput = "";
            await LoadMcpEntriesAsync();
        }
        catch (Exception e)
        {
            _setStatus(e.Message, "error");
        }
    }
}

/// <summary>One product family (Claude / Codex / opencode) with its client and CLI cards.</summary>
public sealed class ToolGroupViewModel
{
    public string Name { get; }
    public ObservableCollection<ToolCardViewModel> Items { get; }

    /// <summary>"1/2 설치" style summary shown next to the group name.</summary>
    public string Summary { get; }

    public ToolGroupViewModel(string name, IEnumerable<ToolCardViewModel> items)
    {
        Name = name;
        Items = [.. items];
        Summary = $"{Items.Count(i => i.Installed)}/{Items.Count} 설치 · {Items.Count(i => i.Configured)}개 설정됨";
    }
}
