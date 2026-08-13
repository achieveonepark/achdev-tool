using System.Collections.ObjectModel;
using AchDevTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AchDevTool.ViewModels;

public partial class WebglViewModel : ObservableObject
{
    private readonly WebglService _webglService = new();
    private readonly WebglServerService _serverService = new();
    private readonly Func<string> _getBuildPath;
    private readonly Action<string, MessageKind> _showMessage;

    public ObservableCollection<BuildCardItemViewModel> Builds { get; } = [];

    [ObservableProperty]
    private BuildCardItemViewModel? selectedBuild;

    [ObservableProperty]
    private bool isLoading;

    [ObservableProperty]
    private int port = 8080;

    [ObservableProperty]
    private bool isServerRunning;

    public string ServerStatusText => IsServerRunning ? "서버 실행 중" : "서버 중지됨";

    public WebglViewModel(Func<string> getBuildPath, Action<string, MessageKind> showMessage)
    {
        _getBuildPath = getBuildPath;
        _showMessage = showMessage;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        Builds.Clear();
        try
        {
            var builds = await Task.Run(() => _webglService.GetWebglBuilds(_getBuildPath()));
            foreach (var b in builds)
            {
                Builds.Add(new BuildCardItemViewModel(b.AppName, b.Name, b.IconBase64, b.Path, "", "🌐", () => SelectBuild(b.Path)));
            }
        }
        catch
        {
            // no WebGL folder / no builds yet
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SelectBuild(string path)
    {
        foreach (var b in Builds)
        {
            b.IsSelected = b.Path == path;
        }
        SelectedBuild = Builds.FirstOrDefault(b => b.Path == path);
    }

    [RelayCommand]
    private void StartServer()
    {
        if (SelectedBuild is null)
        {
            _showMessage("빌드를 선택해주세요", MessageKind.Error);
            return;
        }
        if (Port is < 1024 or > 65535)
        {
            _showMessage("올바른 포트 번호를 입력해주세요 (1024-65535)", MessageKind.Error);
            return;
        }

        try
        {
            var result = _serverService.Start(SelectedBuild.Path, Port);
            _showMessage(result, MessageKind.Success);
            UpdateServerStatus();
        }
        catch (Exception e)
        {
            _showMessage(e.Message, MessageKind.Error);
        }
    }

    [RelayCommand]
    private void StopServer()
    {
        var result = _serverService.Stop();
        _showMessage(result, MessageKind.Info);
        UpdateServerStatus();
    }

    public void UpdateServerStatus()
    {
        IsServerRunning = _serverService.IsRunning;
        OnPropertyChanged(nameof(ServerStatusText));
    }
}
