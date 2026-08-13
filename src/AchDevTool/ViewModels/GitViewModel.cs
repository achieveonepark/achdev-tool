using System.Collections.ObjectModel;
using AchDevTool.Models;
using AchDevTool.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AchDevTool.ViewModels;

public partial class GitViewModel : ObservableObject
{
    private readonly GitService _gitService = new();
    private readonly ConfigService _configService = new();
    private readonly FolderPickerService _folderPicker = new();
    private readonly DialogService _dialogService = new();
    private bool _initialized;

    public ObservableCollection<GitRepoRowViewModel> Repositories { get; } = [];

    [ObservableProperty]
    private string folder = "";

    [ObservableProperty]
    private string status = "";

    [ObservableProperty]
    private string statusKind = "info";

    [ObservableProperty]
    private bool isScanning;

    [ObservableProperty]
    private bool isUpdatingAll;

    public string RepoCountText => $"{Repositories.Count}개";

    public bool CanUpdateAll => Repositories.Count > 0 && !IsUpdatingAll;

    public async Task EnsureInitializedAsync()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;

        var saved = await _configService.LoadGitPathAsync();
        if (saved is null)
        {
            SetStatus("GitHub 저장소가 있는 기준 폴더를 선택해주세요.", "info");
            return;
        }

        SetFolder(saved);
        await ScanAsync();
    }

    private void SetFolder(string value)
    {
        Folder = value;
        Repositories.Clear();
        OnPropertyChanged(nameof(RepoCountText));
        OnPropertyChanged(nameof(CanUpdateAll));
        UpdateAllCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task PickFolderAsync()
    {
        try
        {
            var selected = await _folderPicker.PickFolderAsync("GitHub 저장소가 있는 폴더 선택");
            if (selected is null)
            {
                return;
            }

            SetFolder(selected);
            await _configService.SaveGitPathAsync(selected);
            await ScanAsync();
        }
        catch (Exception e)
        {
            SetStatus($"폴더 선택 실패: {e.Message}", "error");
        }
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (Folder.Length == 0)
        {
            SetStatus("먼저 기준 폴더를 선택해주세요.", "error");
            return;
        }

        IsScanning = true;
        SetStatus("", "info");
        Repositories.Clear();

        try
        {
            var repos = await _gitService.FindGithubRepositoriesAsync(Folder);
            foreach (var r in repos)
            {
                Repositories.Add(new GitRepoRowViewModel(r));
            }

            SetStatus(
                repos.Count > 0 ? $"{repos.Count}개의 GitHub 저장소를 찾았습니다." : "GitHub 저장소를 찾지 못했습니다.",
                "info");
        }
        catch (Exception e)
        {
            SetStatus(e.Message, "error");
        }
        finally
        {
            IsScanning = false;
            OnPropertyChanged(nameof(RepoCountText));
            OnPropertyChanged(nameof(CanUpdateAll));
            UpdateAllCommand.NotifyCanExecuteChanged();
        }
    }

    [RelayCommand(CanExecute = nameof(CanUpdateAll))]
    private async Task UpdateAllAsync()
    {
        if (Repositories.Count == 0)
        {
            return;
        }

        var confirmed = await _dialogService.ConfirmAsync(
            "GitHub 저장소 일괄 업데이트",
            $"{Repositories.Count}개 저장소를 최신 상태로 업데이트할까요?\n\n로컬 변경사항이 있는 저장소와 추적 브랜치가 없는 저장소는 건너뜁니다.",
            "업데이트", "취소");
        if (!confirmed)
        {
            return;
        }

        IsUpdatingAll = true;
        SetStatus("저장소를 순서대로 업데이트하는 중...", "info");

        try
        {
            var results = await _gitService.UpdateGithubRepositoriesAsync(Repositories.Select(r => r.Path));
            var byPath = results.ToDictionary(r => r.Path);
            foreach (var row in Repositories)
            {
                if (byPath.TryGetValue(row.Path, out var result))
                {
                    row.ApplyResult(result);
                }
            }

            int Count(GitUpdateStatus s) => results.Count(r => r.Status == s);
            var summary = string.Join(" · ", new[]
            {
                Count(GitUpdateStatus.Updated) > 0 ? $"{Count(GitUpdateStatus.Updated)}개 업데이트" : "",
                Count(GitUpdateStatus.UpToDate) > 0 ? $"{Count(GitUpdateStatus.UpToDate)}개 최신" : "",
                Count(GitUpdateStatus.Skipped) > 0 ? $"{Count(GitUpdateStatus.Skipped)}개 건너뜀" : "",
                Count(GitUpdateStatus.Failed) > 0 ? $"{Count(GitUpdateStatus.Failed)}개 실패" : "",
            }.Where(s => s.Length > 0));

            SetStatus(summary.Length > 0 ? summary : "업데이트할 저장소가 없습니다.", Count(GitUpdateStatus.Failed) > 0 ? "error" : "ok");
        }
        catch (Exception e)
        {
            SetStatus($"일괄 업데이트 실패: {e.Message}", "error");
        }
        finally
        {
            IsUpdatingAll = false;
            OnPropertyChanged(nameof(CanUpdateAll));
            UpdateAllCommand.NotifyCanExecuteChanged();
        }
    }

    private void SetStatus(string message, string kind)
    {
        Status = message;
        StatusKind = kind;
    }
}
