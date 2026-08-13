using AchDevTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AchDevTool.ViewModels;

public enum BuildTab
{
    Android,
    Ios,
    Webgl,
}

public enum MessageKind
{
    Info,
    Success,
    Error,
}

public partial class BuildViewModel : ObservableObject
{
    private readonly ConfigService _configService = new();
    private readonly BuildFolderService _folderService = new();
    private readonly FolderPickerService _folderPicker = new();
    private MissingFolderRequest? _pendingFolderCreation;
    private CancellationTokenSource? _messageCts;

    private sealed record MissingFolderRequest(List<string> Missing, string Path);

    [ObservableProperty]
    private string buildPath = "";

    [ObservableProperty]
    private BuildTab selectedTab = BuildTab.Android;

    [ObservableProperty]
    private string message = "";

    [ObservableProperty]
    private MessageKind messageKind = MessageKind.Info;

    [ObservableProperty]
    private bool isFolderModalVisible;

    [ObservableProperty]
    private string modalMessage = "";

    public bool ShowIosTab { get; } = !PlatformUtils.IsWindows;

    public bool HasMessage => Message.Length > 0;

    public AndroidViewModel Android { get; }
    public IosViewModel Ios { get; }
    public WebglViewModel Webgl { get; }

    public BuildViewModel()
    {
        Android = new AndroidViewModel(() => BuildPath, ShowMessage);
        Ios = new IosViewModel(() => BuildPath, ShowMessage);
        Webgl = new WebglViewModel(() => BuildPath, ShowMessage);
    }

    public async Task InitializeAsync()
    {
        var saved = await _configService.LoadBuildPathAsync();
        var path = saved ?? _folderService.GetCurrentDir();

        if (string.IsNullOrEmpty(path))
        {
            ShowMessage("빌드 경로를 설정해주세요.", MessageKind.Info);
            return;
        }

        BuildPath = path;

        try
        {
            var result = _folderService.CheckBuildFolders(path);
            if (result.Missing.Count > 0)
            {
                ShowFolderModal(result.Missing, path);
            }
        }
        catch
        {
            // Path doesn't exist yet — same as the Tauri init flow, ignore and let
            // the user pick a valid one via Browse.
        }

        await RefreshAllAsync();
    }

    [RelayCommand]
    private void SetTab(string tab)
    {
        if (Enum.TryParse<BuildTab>(tab, out var value))
        {
            SelectedTab = value;
        }
    }

    partial void OnMessageChanged(string value) => OnPropertyChanged(nameof(HasMessage));

    [RelayCommand]
    private async Task BrowsePathAsync()
    {
        string? selected;
        try
        {
            selected = await _folderPicker.PickFolderAsync("빌드 폴더 선택");
        }
        catch (Exception e)
        {
            ShowMessage($"폴더 선택 실패: {e.Message}", MessageKind.Error);
            return;
        }

        if (selected is null)
        {
            return;
        }

        try
        {
            var result = _folderService.CheckBuildFolders(selected);
            if (result.Missing.Count > 0)
            {
                ShowFolderModal(result.Missing, selected);
                return;
            }

            BuildPath = selected;
            await _configService.SaveBuildPathAsync(selected);
            await RefreshAllAsync();
        }
        catch (Exception e)
        {
            ShowMessage($"폴더 선택 실패: {e.Message}", MessageKind.Error);
        }
    }

    private void ShowFolderModal(List<string> missing, string path)
    {
        _pendingFolderCreation = new MissingFolderRequest(missing, path);
        ModalMessage = $"선택한 경로에 다음 폴더가 없습니다:\n\n{string.Join(", ", missing)}\n\n해당 폴더들을 생성하시겠습니까?";
        IsFolderModalVisible = true;
    }

    [RelayCommand]
    private void CancelFolderCreation()
    {
        IsFolderModalVisible = false;
        _pendingFolderCreation = null;
    }

    [RelayCommand]
    private async Task ConfirmFolderCreationAsync()
    {
        var pending = _pendingFolderCreation;
        if (pending is null)
        {
            return;
        }

        try
        {
            var result = _folderService.CreateBuildFolders(pending.Path, pending.Missing);
            ShowMessage(result, MessageKind.Success);
            BuildPath = pending.Path;
            await _configService.SaveBuildPathAsync(pending.Path);
            IsFolderModalVisible = false;
            _pendingFolderCreation = null;
            await RefreshAllAsync();
        }
        catch (Exception e)
        {
            ShowMessage(e.Message, MessageKind.Error);
            IsFolderModalVisible = false;
            _pendingFolderCreation = null;
        }
    }

    [RelayCommand]
    public async Task RefreshAllAsync()
    {
        await Task.WhenAll(
            Android.RefreshDevicesAsync(),
            Android.RefreshApksAsync(),
            Ios.RefreshAsync(),
            Webgl.RefreshAsync());
        Webgl.UpdateServerStatus();
    }

    private void ShowMessage(string text, MessageKind kind)
    {
        Message = text;
        MessageKind = kind;

        _messageCts?.Cancel();
        var cts = new CancellationTokenSource();
        _messageCts = cts;
        var timeoutMs = kind == MessageKind.Error ? 8000 : 5000;
        _ = ClearMessageAfterAsync(timeoutMs, cts.Token);
    }

    private async Task ClearMessageAfterAsync(int delayMs, CancellationToken token)
    {
        try
        {
            await Task.Delay(delayMs, token);
        }
        catch (TaskCanceledException)
        {
            return;
        }

        if (!token.IsCancellationRequested)
        {
            Message = "";
        }
    }
}
