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

    [ObservableProperty]
    private bool installed;

    public bool NotInstalled => !Installed;
    public string InstalledLabel => Installed ? "설치됨" : "미설치";

    partial void OnInstalledChanged(bool value)
    {
        OnPropertyChanged(nameof(NotInstalled));
        OnPropertyChanged(nameof(InstalledLabel));
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
        Id = info.Id;
        StringId = AiToolsService.StringIdOf(info.Id);
        Installed = info.Installed;
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
