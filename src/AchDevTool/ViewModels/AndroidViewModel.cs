using System.Collections.ObjectModel;
using AchDevTool.Models;
using AchDevTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AchDevTool.ViewModels;

public partial class AndroidViewModel : ObservableObject
{
    private readonly AndroidService _androidService = new();
    private readonly Func<string> _getBuildPath;
    private readonly Action<string, MessageKind> _showMessage;

    public ObservableCollection<DeviceCardItemViewModel> Devices { get; } = [];
    public ObservableCollection<BuildCardItemViewModel> Apks { get; } = [];

    [ObservableProperty]
    private string? selectedDeviceId;

    [ObservableProperty]
    private BuildCardItemViewModel? selectedApk;

    [ObservableProperty]
    private bool launchAfterInstall;

    [ObservableProperty]
    private bool isDevicesLoading;

    [ObservableProperty]
    private bool isApksLoading;

    [ObservableProperty]
    private bool isInstalling;

    [ObservableProperty]
    private bool showProgress;

    [ObservableProperty]
    private double progressValue;

    [ObservableProperty]
    private string progressText = "";

    [ObservableProperty]
    private bool progressCompleted;

    [ObservableProperty]
    private bool progressError;

    public bool CanInstall => !IsInstalling && !string.IsNullOrEmpty(SelectedDeviceId) && SelectedApk is not null;

    public AndroidViewModel(Func<string> getBuildPath, Action<string, MessageKind> showMessage)
    {
        _getBuildPath = getBuildPath;
        _showMessage = showMessage;
    }

    [RelayCommand]
    public async Task RefreshDevicesAsync()
    {
        IsDevicesLoading = true;
        Devices.Clear();
        try
        {
            var devices = await _androidService.GetDevicesAsync();
            foreach (var d in devices)
            {
                var emoji = !d.Authorized ? "🔒" : d.IsTablet ? "📟" : "📱";
                var name = d.Authorized ? d.Model : "권한 없음";
                var sub = d.Authorized
                    ? string.Join('\n', new[] { d.Manufacturer, d.AndroidVersion.Length > 0 ? $"Android {d.AndroidVersion}" : "" }.Where(s => s.Length > 0))
                    : "USB 디버깅 허용 필요";

                Devices.Add(new DeviceCardItemViewModel(d.Id, name, sub, emoji, d.Authorized, () => SelectDevice(d.Id)));
            }

            if (devices.Count == 0)
            {
                _showMessage("연결된 Android 디바이스가 없습니다", MessageKind.Info);
            }
        }
        catch (Exception e)
        {
            _showMessage($"디바이스 목록 로드 실패: {e.Message}", MessageKind.Error);
        }
        finally
        {
            IsDevicesLoading = false;
        }
    }

    private void SelectDevice(string id)
    {
        SelectedDeviceId = id;
        foreach (var d in Devices)
        {
            d.IsSelected = d.DeviceId == id;
        }
        OnPropertyChanged(nameof(CanInstall));
    }

    [RelayCommand]
    public async Task RefreshApksAsync()
    {
        IsApksLoading = true;
        Apks.Clear();
        try
        {
            var apks = await _androidService.GetApkListAsync(_getBuildPath());
            foreach (var apk in apks)
            {
                Apks.Add(new BuildCardItemViewModel(apk.AppName, apk.Name, apk.IconBase64, apk.Path, apk.PackageName, "📦", () => SelectApk(apk.Path)));
            }
        }
        catch
        {
            // no Android folder / no APKs yet — same silent fallback as the original UI
        }
        finally
        {
            IsApksLoading = false;
        }
    }

    private void SelectApk(string path)
    {
        foreach (var apk in Apks)
        {
            apk.IsSelected = apk.Path == path;
        }
        SelectedApk = Apks.FirstOrDefault(a => a.Path == path);
        OnPropertyChanged(nameof(CanInstall));
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (string.IsNullOrEmpty(SelectedDeviceId))
        {
            _showMessage("디바이스를 선택해주세요", MessageKind.Error);
            return;
        }
        if (SelectedApk is null)
        {
            _showMessage("APK 파일을 선택해주세요", MessageKind.Error);
            return;
        }

        IsInstalling = true;
        ShowProgress = true;
        ProgressValue = 0;
        ProgressText = "준비 중...";
        ProgressCompleted = false;
        ProgressError = false;

        var progress = new Progress<InstallProgress>(p =>
        {
            ProgressValue = p.Progress;
            ProgressText = p.Message;
            ProgressCompleted = p.Status == InstallStatus.Completed;
            ProgressError = p.Status == InstallStatus.Error;
        });

        try
        {
            var result = await _androidService.InstallApkAsync(
                SelectedDeviceId, SelectedApk.Path, SelectedApk.PackageName, LaunchAfterInstall, progress);
            _showMessage(result, MessageKind.Success);
        }
        catch (Exception e)
        {
            _showMessage(e.Message, MessageKind.Error);
        }
        finally
        {
            IsInstalling = false;
            _ = HideProgressAfterDelayAsync();
        }
    }

    private async Task HideProgressAfterDelayAsync()
    {
        await Task.Delay(2000);
        ShowProgress = false;
    }

    partial void OnSelectedDeviceIdChanged(string? value) => InstallCommand.NotifyCanExecuteChanged();
    partial void OnSelectedApkChanged(BuildCardItemViewModel? value) => InstallCommand.NotifyCanExecuteChanged();
    partial void OnIsInstallingChanged(bool value) => InstallCommand.NotifyCanExecuteChanged();
}
