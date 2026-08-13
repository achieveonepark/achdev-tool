using AchDevTool.Models;

namespace AchDevTool.Services;

public sealed class GitService
{
    private static readonly HashSet<string> IgnoredScanDirs = new(StringComparer.Ordinal)
    {
        ".git", "node_modules", "target", "Library", "Temp", "Logs", "obj", "bin", "DerivedData",
    };

    private static readonly IDictionary<string, string> LocaleEnv = new Dictionary<string, string>
    {
        ["LC_ALL"] = "C",
        ["LANG"] = "C",
    };

    public async Task EnsureGitAvailableAsync()
    {
        var result = await ProcessRunner.RunAsync("git", ["--version"]).ConfigureAwait(false);
        if (result is null)
        {
            throw new InvalidOperationException("Git을 찾을 수 없습니다. Git을 설치한 뒤 다시 시도해주세요.");
        }
    }

    public async Task<List<GitRepository>> FindGithubRepositoriesAsync(string rootPath)
    {
        await EnsureGitAvailableAsync().ConfigureAwait(false);

        if (!Directory.Exists(rootPath))
        {
            throw new InvalidOperationException("선택한 폴더를 찾을 수 없습니다.");
        }

        var repositories = new List<GitRepository>();
        var stack = new Stack<string>();
        stack.Push(rootPath);

        while (stack.Count > 0)
        {
            var directory = stack.Pop();

            if (IsGitWorktree(directory))
            {
                var repo = await InspectGithubRepositoryAsync(directory).ConfigureAwait(false);
                if (repo is not null)
                {
                    repositories.Add(repo);
                }
                // A repository can contain millions of generated files; treat it as one
                // unit instead of walking into its working tree.
                continue;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateDirectories(directory);
            }
            catch
            {
                continue;
            }

            foreach (var path in entries)
            {
                var info = new DirectoryInfo(path);
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    continue; // avoid symlink loops / escaping the selected folder
                }

                if (IgnoredScanDirs.Contains(info.Name))
                {
                    continue;
                }

                stack.Push(path);
            }
        }

        repositories.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return repositories;
    }

    public async Task<List<GitUpdateResult>> UpdateGithubRepositoriesAsync(IEnumerable<string> paths)
    {
        await EnsureGitAvailableAsync().ConfigureAwait(false);

        var results = new List<GitUpdateResult>();
        foreach (var path in paths)
        {
            results.Add(await UpdateGithubRepositoryAsync(path).ConfigureAwait(false));
        }

        return results;
    }

    private static bool IsGitWorktree(string path)
    {
        var dotGit = Path.Combine(path, ".git");
        return Directory.Exists(dotGit) || File.Exists(dotGit);
    }

    private static bool IsGithubRemote(string remote)
    {
        var r = remote.Trim().ToLowerInvariant();
        return r.Contains("github.com/") || r.Contains("github.com:");
    }

    private static async Task<string?> GitTextAsync(string repoPath, params string[] args)
    {
        var fullArgs = new List<string> { "-C", repoPath };
        fullArgs.AddRange(args);

        var result = await ProcessRunner.RunAsync("git", fullArgs, env: LocaleEnv).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        return result.Success ? result.Stdout.Trim() : null;
    }

    private static async Task<GitRepository?> InspectGithubRepositoryAsync(string path)
    {
        var remote = await GitTextAsync(path, "remote", "get-url", "origin").ConfigureAwait(false);
        if (remote is null || !IsGithubRemote(remote))
        {
            return null;
        }

        var branch = await GitTextAsync(path, "branch", "--show-current").ConfigureAwait(false) ?? "HEAD";
        var status = await GitTextAsync(path, "status", "--porcelain").ConfigureAwait(false);
        var dirty = status is null || status.Length > 0;
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

        return new GitRepository(name, path, branch, dirty);
    }

    private static async Task<GitUpdateResult> UpdateGithubRepositoryAsync(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar));

        if (!IsGitWorktree(path))
        {
            return new GitUpdateResult(name, path, GitUpdateStatus.Skipped, "Git 저장소가 아니어서 건너뛰었습니다.");
        }

        var remote = await GitTextAsync(path, "remote", "get-url", "origin").ConfigureAwait(false);
        if (remote is null || !IsGithubRemote(remote))
        {
            return new GitUpdateResult(name, path, GitUpdateStatus.Skipped, "GitHub origin 원격 저장소가 아니어서 건너뛰었습니다.");
        }

        var status = await GitTextAsync(path, "status", "--porcelain").ConfigureAwait(false);
        if (status is null)
        {
            return new GitUpdateResult(name, path, GitUpdateStatus.Failed, "상태를 확인하지 못했습니다.");
        }
        if (status.Length > 0)
        {
            return new GitUpdateResult(name, path, GitUpdateStatus.Skipped, "로컬 변경사항이 있어 안전하게 건너뛰었습니다.");
        }

        var upstreamArgs = new List<string> { "-C", path, "rev-parse", "--abbrev-ref", "--symbolic-full-name", "@{upstream}" };
        var upstreamResult = await ProcessRunner.RunAsync("git", upstreamArgs, env: LocaleEnv).ConfigureAwait(false);
        if (upstreamResult is not { Success: true })
        {
            // A branch with no upstream produces a confusing git error; leave it alone.
            return new GitUpdateResult(name, path, GitUpdateStatus.Skipped, "추적 원격 브랜치가 없어 건너뛰었습니다.");
        }

        var pullArgs = new List<string> { "-C", path, "pull", "--ff-only" };
        var pullResult = await ProcessRunner.RunAsync("git", pullArgs, env: LocaleEnv).ConfigureAwait(false);
        if (pullResult is null)
        {
            return new GitUpdateResult(name, path, GitUpdateStatus.Failed, "업데이트에 실패했습니다.");
        }

        if (pullResult.Success)
        {
            return pullResult.Stdout.Contains("Already up to date.")
                ? new GitUpdateResult(name, path, GitUpdateStatus.UpToDate, "이미 최신 상태입니다.")
                : new GitUpdateResult(name, path, GitUpdateStatus.Updated, "업데이트되었습니다.");
        }

        var detail = pullResult.Stderr.Trim();
        return new GitUpdateResult(name, path, GitUpdateStatus.Failed, detail.Length > 0 ? detail : "업데이트에 실패했습니다.");
    }
}
