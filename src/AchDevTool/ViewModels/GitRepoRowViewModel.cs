using AchDevTool.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AchDevTool.ViewModels;

public partial class GitRepoRowViewModel : ObservableObject
{
    public string Name { get; }
    public string Path { get; }
    public string Branch { get; }
    public bool Dirty { get; }

    public bool Clean => !Dirty;
    public string StateLabel => Dirty ? "변경사항 있음" : "깨끗함";
    public string BranchLabel => $"⌘ {(Branch.Length > 0 ? Branch : "HEAD")}";

    [ObservableProperty]
    private GitUpdateStatus? updateStatus;

    [ObservableProperty]
    private string updateSummary = "";

    public bool HasUpdateResult => UpdateStatus is not null;
    public bool IsUpdated => UpdateStatus is GitUpdateStatus.Updated or GitUpdateStatus.UpToDate;
    public bool IsSkipped => UpdateStatus is GitUpdateStatus.Skipped;
    public bool IsFailed => UpdateStatus is GitUpdateStatus.Failed;

    public GitRepoRowViewModel(GitRepository repo)
    {
        Name = repo.Name;
        Path = repo.Path;
        Branch = repo.Branch;
        Dirty = repo.Dirty;
    }

    public void ApplyResult(GitUpdateResult result)
    {
        var label = result.Status switch
        {
            GitUpdateStatus.Updated => "업데이트됨",
            GitUpdateStatus.UpToDate => "최신",
            GitUpdateStatus.Skipped => "건너뜀",
            GitUpdateStatus.Failed => "실패",
            _ => "",
        };

        UpdateStatus = result.Status;
        UpdateSummary = $"{label} · {result.Message}";
        OnPropertyChanged(nameof(HasUpdateResult));
        OnPropertyChanged(nameof(IsUpdated));
        OnPropertyChanged(nameof(IsSkipped));
        OnPropertyChanged(nameof(IsFailed));
    }
}
