using System.Collections.ObjectModel;
using AchDevTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AchDevTool.ViewModels;

public partial class IosViewModel : ObservableObject
{
    private readonly IosService _iosService = new();
    private readonly Func<string> _getBuildPath;
    private readonly Action<string, MessageKind> _showMessage;

    public ObservableCollection<BuildCardItemViewModel> Projects { get; } = [];

    [ObservableProperty]
    private BuildCardItemViewModel? selectedProject;

    [ObservableProperty]
    private bool isLoading;

    public IosViewModel(Func<string> getBuildPath, Action<string, MessageKind> showMessage)
    {
        _getBuildPath = getBuildPath;
        _showMessage = showMessage;
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        IsLoading = true;
        Projects.Clear();
        try
        {
            var projects = await Task.Run(() => _iosService.GetIosProjects(_getBuildPath()));
            foreach (var p in projects)
            {
                Projects.Add(new BuildCardItemViewModel(p.AppName, p.Name, p.IconBase64, p.Path, "", "📱", () => SelectProject(p.Path)));
            }
        }
        catch
        {
            // no iOS folder / no projects yet
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SelectProject(string path)
    {
        foreach (var p in Projects)
        {
            p.IsSelected = p.Path == path;
        }
        SelectedProject = Projects.FirstOrDefault(p => p.Path == path);
    }

    [RelayCommand]
    private void OpenXcode()
    {
        if (SelectedProject is null)
        {
            _showMessage("프로젝트를 선택해주세요", MessageKind.Error);
            return;
        }

        try
        {
            var result = _iosService.OpenXcodeProject(SelectedProject.Path);
            _showMessage(result, MessageKind.Success);
        }
        catch (Exception e)
        {
            _showMessage(e.Message, MessageKind.Error);
        }
    }
}
