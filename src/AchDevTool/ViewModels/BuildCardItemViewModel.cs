using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AchDevTool.ViewModels;

/// <summary>Shared card shape for Android APK / iOS project / WebGL build grids —
/// they all render as "icon + app name + file/folder name".</summary>
public partial class BuildCardItemViewModel : ObservableObject
{
    public string AppName { get; }
    public string SubText { get; }
    public string? IconBase64 { get; }
    public bool HasIcon => IconBase64 is { Length: > 0 };
    public bool NoIcon => !HasIcon;
    public string Path { get; }
    public string PackageName { get; }
    public string FallbackEmoji { get; }

    [ObservableProperty]
    private bool isSelected;

    public IRelayCommand SelectCommand { get; }

    public BuildCardItemViewModel(
        string appName,
        string subText,
        string? iconBase64,
        string path,
        string packageName,
        string fallbackEmoji,
        Action onSelect)
    {
        AppName = appName;
        SubText = subText;
        IconBase64 = iconBase64;
        Path = path;
        PackageName = packageName;
        FallbackEmoji = fallbackEmoji;
        SelectCommand = new RelayCommand(onSelect);
    }
}

public sealed class RecentFolderItemViewModel
{
    public string Path { get; }
    public string DisplayName { get; }
    public IRelayCommand PickCommand { get; }

    public RecentFolderItemViewModel(string path, Action<string> onPick)
    {
        Path = path;
        var trimmed = path.TrimEnd('/', '\\');
        var name = System.IO.Path.GetFileName(trimmed);
        DisplayName = name.Length > 0 ? name : path;
        PickCommand = new RelayCommand(() => onPick(path));
    }
}

public partial class DeviceCardItemViewModel : ObservableObject
{
    public string DisplayName { get; }
    public string SubText { get; }
    public string Emoji { get; }
    public bool Authorized { get; }
    public string DeviceId { get; }

    [ObservableProperty]
    private bool isSelected;

    public IRelayCommand SelectCommand { get; }

    public DeviceCardItemViewModel(string deviceId, string displayName, string subText, string emoji, bool authorized, Action onSelect)
    {
        DeviceId = deviceId;
        DisplayName = displayName;
        SubText = subText;
        Emoji = emoji;
        Authorized = authorized;
        SelectCommand = new RelayCommand(onSelect);
    }
}
