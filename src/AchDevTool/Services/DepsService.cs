using AchDevTool.Models;

namespace AchDevTool.Services;

/// <summary>Common handling for external CLI dependencies (currently just
/// libimobiledevice). Ported from deps.rs.</summary>
public sealed class DepsService
{
    private sealed record DepMeta(string Id, string DisplayName, string Bin, string Brew, string Scoop, string Apt);

    private static readonly DepMeta[] Deps =
    [
        new("libimobiledevice", "libimobiledevice", "idevice_id", "libimobiledevice", "libimobiledevice", "libimobiledevice-utils"),
    ];

    private static DepMeta MetaOf(string id)
        => Deps.FirstOrDefault(d => d.Id == id) ?? throw new InvalidOperationException($"알 수 없는 도구입니다: {id}");

    private static string? InstallCommand(DepMeta meta)
    {
        if (PlatformUtils.IsMacOS)
        {
            return $"brew install {meta.Brew}";
        }

        if (PlatformUtils.IsWindows)
        {
            return meta.Scoop.Length == 0 ? null : $"scoop install {meta.Scoop}";
        }

        return meta.Apt.Length == 0 ? null : $"sudo apt-get install -y {meta.Apt}";
    }

    public DepStatus CheckDependency(string id)
    {
        var meta = MetaOf(id);
        var installCmd = InstallCommand(meta);
        return new DepStatus(meta.Id, meta.DisplayName, AiToolsService.FindBinary(meta.Bin) is not null, installCmd ?? "", installCmd is not null);
    }

    public string InstallDependency(string id)
    {
        var meta = MetaOf(id);
        var cmd = InstallCommand(meta)
            ?? throw new InvalidOperationException($"{meta.DisplayName} 자동 설치는 이 OS 에서 지원하지 않습니다. 수동으로 설치해주세요.");

        if (PlatformUtils.IsMacOS && AiToolsService.FindBinary("brew") is null)
        {
            throw new InvalidOperationException("Homebrew 가 필요합니다. https://brew.sh 에서 먼저 설치해주세요.");
        }
        if (PlatformUtils.IsWindows && AiToolsService.FindBinary("scoop") is null)
        {
            throw new InvalidOperationException("scoop 이 필요합니다. https://scoop.sh 에서 먼저 설치해주세요.");
        }

        AiToolsService.RunInTerminal(cmd);
        return $"{meta.DisplayName} 설치를 터미널에서 시작했습니다: `{cmd}`\n설치가 끝나면 다시 시도해주세요.";
    }
}
