namespace AchDevTool.Models;

public sealed record GitRepository(
    string Name,
    string Path,
    string Branch,
    bool Dirty);

public enum GitUpdateStatus
{
    Updated,
    UpToDate,
    Skipped,
    Failed,
}

public sealed record GitUpdateResult(
    string Name,
    string Path,
    GitUpdateStatus Status,
    string Message);
