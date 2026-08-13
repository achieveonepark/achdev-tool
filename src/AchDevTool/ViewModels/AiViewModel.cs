using System.Collections.ObjectModel;
using AchDevTool.Models;
using AchDevTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AchDevTool.ViewModels;

public partial class AiViewModel : ObservableObject
{
    private readonly AiToolsService _aiToolsService = new();
    private readonly McpService _mcpService = new();
    private readonly AiSettingsService _settings = new();
    private readonly FolderPickerService _folderPicker = new();
    private bool _initialized;

    public ObservableCollection<ToolCardViewModel> Tools { get; } = [];
    public ObservableCollection<RecentFolderItemViewModel> RecentFolders { get; } = [];

    [ObservableProperty]
    private string folder = "";

    [ObservableProperty]
    private ToolId? selectedToolId;

    [ObservableProperty]
    private bool autorun = true;

    [ObservableProperty]
    private string status = "";

    [ObservableProperty]
    private string statusKind = "info";

    [ObservableProperty]
    private bool isOpening;

    public bool CanOpen => !IsOpening && Folder.Length > 0 &&
        Tools.FirstOrDefault(t => t.Id == SelectedToolId) is { Installed: true };

    public async Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        Autorun = _settings.LoadAutorun();
        var savedTool = _settings.LoadSelectedTool();
        SelectedToolId = savedTool is not null ? AiToolsService.ParseToolId(savedTool) : null;

        RebuildRecentFolders();

        await RefreshAsync();
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        var infos = await Task.Run(() => _aiToolsService.ListTools());

        // If the persisted choice isn't installed, fall back to the first installed tool.
        if (SelectedToolId is null || infos.FirstOrDefault(t => t.Id == SelectedToolId) is not { Installed: true })
        {
            SelectedToolId = infos.FirstOrDefault(t => t.Installed)?.Id;
        }

        Tools.Clear();
        foreach (var info in infos)
        {
            var card = new ToolCardViewModel(info, _aiToolsService, _mcpService, SetStatus, ChooseTool)
            {
                IsChosen = info.Id == SelectedToolId,
            };
            Tools.Add(card);
        }

        var hasCode = await Task.Run(() => _aiToolsService.HasCode());
        if (!hasCode)
        {
            SetStatus("안내: 'code' 명령을 찾을 수 없습니다. VSCode에서 \"Shell Command: Install 'code' command in PATH\"를 실행하세요.", "error");
        }
        else if (!infos.Any(t => t.Installed))
        {
            SetStatus("아직 설치된 AI CLI가 없습니다 — 아래 설치 버튼을 사용하세요.", "info");
        }

        OnPropertyChanged(nameof(CanOpen));
        OpenInVscodeCommand.NotifyCanExecuteChanged();
    }

    private void ChooseTool(ToolId id)
    {
        SelectedToolId = id;
        _settings.SaveSelectedTool(AiToolsService.StringIdOf(id));
        foreach (var t in Tools)
        {
            t.IsChosen = t.Id == id;
        }
        OnPropertyChanged(nameof(CanOpen));
        OpenInVscodeCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        var result = await _folderPicker.PickFolderAsync("폴더 선택");
        if (result is not null)
        {
            SetFolder(result);
        }
    }

    private void SetFolder(string value)
    {
        Folder = value;
        OnPropertyChanged(nameof(CanOpen));
    }

    partial void OnFolderChanged(string value)
    {
        OnPropertyChanged(nameof(CanOpen));
        OpenInVscodeCommand.NotifyCanExecuteChanged();
    }

    partial void OnAutorunChanged(bool value) => _settings.SaveAutorun(value);

    [RelayCommand(CanExecute = nameof(CanOpen))]
    private async Task OpenInVscodeAsync()
    {
        if (SelectedToolId is not { } tool)
        {
            return;
        }

        IsOpening = true;
        SetStatus("VSCode를 여는 중…", "info");
        try
        {
            var message = await Task.Run(() => _aiToolsService.OpenInVscode(Folder, tool, Autorun));
            PushRecent(Folder);
            SetStatus(message, "ok");
        }
        catch (Exception e)
        {
            SetStatus(e.Message, "error");
        }
        finally
        {
            IsOpening = false;
            OnPropertyChanged(nameof(CanOpen));
        }
    }

    private void PushRecent(string folder)
    {
        _settings.PushRecent(folder);
        RebuildRecentFolders();
    }

    private void RebuildRecentFolders()
    {
        RecentFolders.Clear();
        foreach (var f in _settings.LoadRecent())
        {
            RecentFolders.Add(new RecentFolderItemViewModel(f, SetFolder));
        }
    }

    private void SetStatus(string message, string kind)
    {
        Status = message;
        StatusKind = kind;
    }

    partial void OnIsOpeningChanged(bool value) => OpenInVscodeCommand.NotifyCanExecuteChanged();
    partial void OnSelectedToolIdChanged(ToolId? value) => OpenInVscodeCommand.NotifyCanExecuteChanged();
}
