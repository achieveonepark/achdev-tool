using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AchDevTool.Views;

public partial class ConfirmDialog : Window
{
    public ConfirmDialog()
    {
        InitializeComponent();
    }

    public static ConfirmDialog Create(string title, string message, string okLabel, string cancelLabel, bool showCancel)
    {
        var dialog = new ConfirmDialog { Title = title };
        dialog.MessageText.Text = message;
        dialog.OkButton.Content = okLabel;
        dialog.CancelButton.Content = cancelLabel;
        dialog.CancelButton.IsVisible = showCancel;
        return dialog;
    }

    private void OnOk(object? sender, RoutedEventArgs e) => Close(true);

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(false);
}
