using AchDevTool.Models;

namespace AchDevTool.Services;

/// <summary>Checks/creates the Android|iOS|MacOS|WebGL subfolders under a chosen build root.</summary>
public sealed class BuildFolderService
{
    private static readonly string[] RequiredFolders = ["Android", "iOS", "MacOS", "WebGL"];

    public string GetCurrentDir() => Directory.GetCurrentDirectory();

    public MissingFolders CheckBuildFolders(string path)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidOperationException("지정한 경로가 존재하지 않습니다.");
        }

        var missing = RequiredFolders
            .Where(folder => !Directory.Exists(Path.Combine(path, folder)))
            .ToList();

        return new MissingFolders(missing, path);
    }

    public string CreateBuildFolders(string path, IEnumerable<string> folders)
    {
        var list = folders.ToList();
        foreach (var folder in list)
        {
            try
            {
                Directory.CreateDirectory(Path.Combine(path, folder));
            }
            catch (Exception e)
            {
                throw new InvalidOperationException($"{folder} 폴더 생성 실패: {e.Message}");
            }
        }

        return $"{string.Join(", ", list)} 폴더가 생성되었습니다.";
    }
}
