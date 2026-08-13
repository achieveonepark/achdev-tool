namespace AchDevTool.Services;

/// <summary>Persists the last-used build/git folder picks, mirroring the old
/// Tauri app's build_path.txt / git_path.txt files under the app data dir.</summary>
public sealed class ConfigService
{
    private static readonly string BuildPathFile = Path.Combine(AppPaths.DataDir, "build_path.txt");
    private static readonly string GitPathFile = Path.Combine(AppPaths.DataDir, "git_path.txt");

    public Task SaveBuildPathAsync(string path) => WriteAsync(BuildPathFile, path);

    public Task<string?> LoadBuildPathAsync() => ReadExistingDirectoryAsync(BuildPathFile);

    public Task SaveGitPathAsync(string path) => WriteAsync(GitPathFile, path);

    public Task<string?> LoadGitPathAsync() => ReadExistingDirectoryAsync(GitPathFile);

    private static async Task WriteAsync(string file, string value)
    {
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        await File.WriteAllTextAsync(file, value).ConfigureAwait(false);
    }

    private static async Task<string?> ReadExistingDirectoryAsync(string file)
    {
        if (!File.Exists(file))
        {
            return null;
        }

        var path = (await File.ReadAllTextAsync(file).ConfigureAwait(false)).Trim();
        return path.Length > 0 && Directory.Exists(path) ? path : null;
    }
}
