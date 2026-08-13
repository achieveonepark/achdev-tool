using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AchDevTool.ViewModels;

public enum AppCategory
{
    Build,
    Ai,
    Git,
}

public partial class MainWindowViewModel : ObservableObject
{
    [ObservableProperty]
    private AppCategory selectedCategory = AppCategory.Build;

    public BuildViewModel Build { get; } = new();
    public AiViewModel Ai { get; } = new();
    public GitViewModel Git { get; } = new();

    public MainWindowViewModel()
    {
        _ = Build.InitializeAsync();
    }

    [RelayCommand]
    private void SelectCategory(string category)
    {
        if (!Enum.TryParse<AppCategory>(category, out var value))
        {
            return;
        }

        SelectedCategory = value;

        switch (value)
        {
            case AppCategory.Ai:
                _ = Ai.EnsureInitializedAsync();
                break;
            case AppCategory.Git:
                _ = Git.EnsureInitializedAsync();
                break;
        }
    }
}
