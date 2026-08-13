namespace AchDevTool.Models;

public sealed record DeviceInfo(
    string Id,
    string Model,
    string Manufacturer,
    string AndroidVersion,
    bool IsTablet,
    bool Authorized);

public sealed record ApkInfo(
    string Name,
    string AppName,
    string PackageName,
    string Path,
    string? IconBase64);

public sealed record IosProject(
    string Name,
    string AppName,
    string Path,
    string? IconBase64);

public sealed record WebglBuild(
    string Name,
    string AppName,
    string Path,
    string? IconBase64);

public sealed record MissingFolders(List<string> Missing, string Path);

public enum InstallStatus
{
    Starting,
    Installing,
    Completed,
    Error,
}

public sealed record InstallProgress(InstallStatus Status, int Progress, string Message);
