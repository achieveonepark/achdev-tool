using System.IO.Compression;
using System.Text;
using AchDevTool.Models;

namespace AchDevTool.Services;

/// <summary>Locates Android SDK command-line tools (adb/aapt/aapt2) and extracts
/// APK metadata (app name, package name, launcher icon) from them.</summary>
public static class AndroidSdkTools
{
    public static string? FindAdb()
    {
        var candidates = new List<string>();

        if (PlatformUtils.IsWindows)
        {
            var local = Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "";
            candidates.Add(Path.Combine(local, "Android", "Sdk", "platform-tools", "adb.exe"));
            candidates.Add(Path.Combine(local, "Android", "android-sdk", "platform-tools", "adb.exe"));
            candidates.Add(@"C:\Android\platform-tools\adb.exe");
            candidates.Add(@"C:\android-sdk\platform-tools\adb.exe");
        }
        else
        {
            var home = AppPaths.HomeDir;
            candidates.Add("/opt/homebrew/bin/adb");
            candidates.Add("/usr/local/bin/adb");
            candidates.Add(Path.Combine(home, "Library", "Android", "sdk", "platform-tools", "adb"));
            candidates.Add(Path.Combine(home, "Android", "Sdk", "platform-tools", "adb"));
        }

        foreach (var envVar in new[] { "ANDROID_HOME", "ANDROID_SDK_ROOT" })
        {
            var sdk = Environment.GetEnvironmentVariable(envVar);
            if (!string.IsNullOrEmpty(sdk))
            {
                candidates.Add(PlatformUtils.IsWindows
                    ? Path.Combine(sdk, "platform-tools", "adb.exe")
                    : Path.Combine(sdk, "platform-tools", "adb"));
            }
        }

        foreach (var path in candidates)
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return AiToolsService.FindBinary("adb")?.FullName;
    }

    /// <summary>Find the newest aapt/aapt2 inside the Android SDK's build-tools directory.</summary>
    public static string? FindSdkTool(string toolName)
    {
        var exe = PlatformUtils.IsWindows ? $"{toolName}.exe" : toolName;

        var buildToolsBase = PlatformUtils.IsWindows
            ? Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? "", "Android", "Sdk", "build-tools")
            : Path.Combine(AppPaths.HomeDir, "Library", "Android", "sdk", "build-tools");

        if (!Directory.Exists(buildToolsBase))
        {
            return null;
        }

        var versionDirs = Directory.GetDirectories(buildToolsBase)
            .OrderByDescending(d => Path.GetFileName(d), StringComparer.Ordinal);

        foreach (var dir in versionDirs)
        {
            var toolPath = Path.Combine(dir, exe);
            if (File.Exists(toolPath))
            {
                return toolPath;
            }
        }

        return null;
    }

    /// <summary>Runs `aapt dump badging` and extracts (app label, package name).</summary>
    public static async Task<(string AppName, string PackageName)> GetApkBadgeInfoAsync(string aapt, string apkPath)
    {
        var fallbackName = Path.GetFileName(apkPath);

        var result = await ProcessRunner.RunAsync(aapt, ["dump", "badging", apkPath]).ConfigureAwait(false);
        if (result is null)
        {
            return (fallbackName, string.Empty);
        }

        string? defaultLabel = null;
        string? enLabel = null;
        var packageName = string.Empty;

        foreach (var line in result.Stdout.Split('\n'))
        {
            if (line.StartsWith("package:") && packageName.Length == 0)
            {
                foreach (var part in line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (part.StartsWith("name='"))
                    {
                        packageName = part["name='".Length..].TrimEnd('\'');
                        break;
                    }
                }
            }

            if (line.StartsWith("application-label:'") && defaultLabel is null)
            {
                defaultLabel = line["application-label:'".Length..].TrimEnd('\'');
            }

            if (line.StartsWith("application-label-en:'") && enLabel is null)
            {
                enLabel = line["application-label-en:'".Length..].TrimEnd('\'');
            }
        }

        var appName = enLabel ?? defaultLabel ?? fallbackName;
        return (appName, packageName);
    }

    /// <summary>Finds the highest-density launcher icon via `aapt2 dump resources`, then reads
    /// the PNG bytes straight out of the APK zip (no external `unzip` dependency needed).</summary>
    public static async Task<string?> GetApkIconBase64Async(string aapt2, string apkPath)
    {
        var result = await ProcessRunner.RunAsync(aapt2, ["dump", "resources", apkPath]).ConfigureAwait(false);
        if (result is null)
        {
            return null;
        }

        var lines = result.Stdout.Split('\n');
        string[] resourceNames = ["mipmap/ic_launcher_foreground", "mipmap/app_icon"];
        string[] densityPatterns = ["(xxxhdpi) (file)", "(xxhdpi) (file)", "(xhdpi) (file)", "(hdpi) (file)", "(mdpi) (file)"];

        foreach (var resourceName in resourceNames)
        {
            var inSection = false;
            int? bestPriority = null;
            string? bestPath = null;

            foreach (var raw in lines)
            {
                var trimmed = raw.Trim();

                if (trimmed.Contains(resourceName) && trimmed.StartsWith("resource"))
                {
                    inSection = true;
                    continue;
                }

                if (!inSection)
                {
                    continue;
                }

                if (trimmed.StartsWith("resource"))
                {
                    break;
                }

                if (!trimmed.Contains("type=PNG"))
                {
                    continue;
                }

                for (var priority = 0; priority < densityPatterns.Length; priority++)
                {
                    if (!trimmed.Contains(densityPatterns[priority]))
                    {
                        continue;
                    }

                    if (bestPriority is null || priority < bestPriority)
                    {
                        var fileStart = trimmed.IndexOf("(file) ", StringComparison.Ordinal);
                        if (fileStart >= 0)
                        {
                            var rest = trimmed[(fileStart + "(file) ".Length)..];
                            var typeEnd = rest.IndexOf(" type=", StringComparison.Ordinal);
                            if (typeEnd >= 0)
                            {
                                bestPriority = priority;
                                bestPath = rest[..typeEnd];
                            }
                        }
                    }

                    break;
                }

                if (bestPriority == 0)
                {
                    break;
                }
            }

            if (bestPath is null)
            {
                continue;
            }

            var bytes = TryReadZipEntry(apkPath, bestPath);
            if (bytes is { Length: > 0 })
            {
                return $"data:image/png;base64,{Convert.ToBase64String(bytes)}";
            }
        }

        return null;
    }

    private static byte[]? TryReadZipEntry(string zipPath, string entryPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var entry = archive.GetEntry(entryPath);
            if (entry is null)
            {
                return null;
            }

            using var stream = entry.Open();
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    public static string ParseAdbError(string error)
    {
        var lower = error.ToLowerInvariant();

        if (lower.Contains("install_failed_already_exists"))
            return "설치 실패: 이미 설치된 앱이 있습니다. 기존 앱을 삭제하고 다시 시도해주세요.";
        if (lower.Contains("install_failed_insufficient_storage"))
            return "설치 실패: 디바이스 저장 공간이 부족합니다.";
        if (lower.Contains("install_failed_invalid_apk"))
            return "설치 실패: APK 파일이 손상되었거나 유효하지 않습니다.";
        if (lower.Contains("install_failed_version_downgrade"))
            return "설치 실패: 이미 설치된 버전보다 낮은 버전입니다. 기존 앱을 삭제하고 다시 시도해주세요.";
        if (lower.Contains("install_failed_update_incompatible"))
            return "설치 실패: 기존 앱과 서명이 다릅니다. 기존 앱을 삭제하고 다시 시도해주세요.";
        if (lower.Contains("install_failed_older_sdk"))
            return "설치 실패: 디바이스의 Android 버전이 앱의 최소 요구 버전보다 낮습니다.";
        if (lower.Contains("install_failed_no_matching_abis"))
            return "설치 실패: 디바이스의 CPU 아키텍처와 APK가 호환되지 않습니다.";
        if (lower.Contains("install_parse_failed"))
            return "설치 실패: APK 파일을 파싱할 수 없습니다. 파일이 손상되었을 수 있습니다.";
        if (lower.Contains("install_failed_test_only"))
            return "설치 실패: 테스트 전용 APK입니다. -t 옵션이 필요합니다.";
        if (lower.Contains("device not found") || lower.Contains("no devices"))
            return "설치 실패: 디바이스를 찾을 수 없습니다. USB 연결을 확인해주세요.";
        if (lower.Contains("device offline"))
            return "설치 실패: 디바이스가 오프라인 상태입니다. USB 연결을 다시 확인해주세요.";
        if (lower.Contains("unauthorized"))
            return "설치 실패: 디바이스에서 USB 디버깅을 허용해주세요.";
        if (lower.Contains("install_failed_user_restricted"))
            return "설치 실패: 사용자 제한으로 설치할 수 없습니다. 디바이스 설정을 확인해주세요.";

        if (lower.Contains("failure"))
        {
            var start = error.IndexOf('[');
            var end = error.IndexOf(']');
            if (start >= 0 && end > start)
            {
                return $"설치 실패: {error[(start + 1)..end]}";
            }
        }

        return $"설치 실패: {error}";
    }
}
