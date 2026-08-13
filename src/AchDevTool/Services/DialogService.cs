using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using AchDevTool.Views;

namespace AchDevTool.Services;

public sealed class DialogService
{
    public async Task<bool> ConfirmAsync(string title, string message, string okLabel = "확인", string cancelLabel = "취소")
    {
        var owner = GetMainWindow();
        var dialog = ConfirmDialog.Create(title, message, okLabel, cancelLabel, showCancel: true);
        return owner is not null && await dialog.ShowDialog<bool>(owner);
    }

    public async Task InfoAsync(string title, string message)
    {
        var owner = GetMainWindow();
        var dialog = ConfirmDialog.Create(title, message, "확인", "", showCancel: false);
        if (owner is not null)
        {
            await dialog.ShowDialog<bool>(owner);
        }
    }

    private static Avalonia.Controls.Window? GetMainWindow()
        => Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.MainWindow
            : null;
}
