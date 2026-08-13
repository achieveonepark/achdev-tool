using System.Diagnostics;
using AchDevTool.Models;

namespace AchDevTool.Services;

public sealed class AndroidService
{
    public async Task<List<DeviceInfo>> GetDevicesAsync()
    {
        var adb = AndroidSdkTools.FindAdb()
            ?? throw new InvalidOperationException("ADB를 찾을 수 없습니다. Android SDK를 설치하거나 ANDROID_HOME 환경변수를 설정해주세요.");

        var result = await ProcessRunner.RunAsync(adb, ["devices", "-l"]).ConfigureAwait(false)
            ?? throw new InvalidOperationException("ADB 실행 실패");

        var devices = new List<DeviceInfo>();

        foreach (var line in result.Stdout.Split('\n').Skip(1))
        {
            if (string.IsNullOrWhiteSpace(line) || line.Contains("offline"))
            {
                continue;
            }

            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0)
            {
                continue;
            }

            var id = parts[0];

            if (line.Contains("unauthorized"))
            {
                devices.Add(new DeviceInfo(id, "알 수 없는 기기", "", "", false, false));
                continue;
            }

            if (parts.Length >= 2 && parts[1] == "device")
            {
                var propsResult = await ProcessRunner.RunAsync(adb, [
                    "-s", id, "shell",
                    "getprop ro.product.model; echo '---'; " +
                    "getprop ro.product.manufacturer; echo '---'; " +
                    "getprop ro.build.version.release; echo '---'; " +
                    "getprop ro.product.characteristics",
                ]).ConfigureAwait(false);

                string model = id, manufacturer = "", version = "";
                var isTablet = false;

                if (propsResult is not null)
                {
                    var segments = propsResult.Stdout.Split("---\n");
                    var m = segments.ElementAtOrDefault(0)?.Trim() ?? "";
                    manufacturer = segments.ElementAtOrDefault(1)?.Trim() ?? "";
                    version = segments.ElementAtOrDefault(2)?.Trim() ?? "";
                    var characteristics = segments.ElementAtOrDefault(3)?.Trim() ?? "";
                    isTablet = characteristics.Contains("tablet");
                    model = string.IsNullOrEmpty(m) ? id : m;
                }

                devices.Add(new DeviceInfo(id, model, manufacturer, version, isTablet, true));
            }
        }

        return devices;
    }

    public async Task<List<ApkInfo>> GetApkListAsync(string buildPath)
    {
        var androidPath = Path.Combine(buildPath, "Android");
        if (!Directory.Exists(androidPath))
        {
            throw new InvalidOperationException("Android 폴더를 찾을 수 없습니다.");
        }

        var aapt = AndroidSdkTools.FindSdkTool("aapt");
        var aapt2 = AndroidSdkTools.FindSdkTool("aapt2");

        var apks = new List<ApkInfo>();
        foreach (var path in Directory.EnumerateFiles(androidPath, "*.apk"))
        {
            var filename = Path.GetFileName(path);

            var (appName, packageName) = aapt is not null
                ? await AndroidSdkTools.GetApkBadgeInfoAsync(aapt, path).ConfigureAwait(false)
                : (filename, string.Empty);

            var iconBase64 = aapt2 is not null
                ? await AndroidSdkTools.GetApkIconBase64Async(aapt2, path).ConfigureAwait(false)
                : null;

            apks.Add(new ApkInfo(filename, appName, packageName, path, iconBase64));
        }

        return apks.OrderBy(a => a.AppName, StringComparer.Ordinal).ToList();
    }

    public async Task<string> InstallApkAsync(
        string deviceId,
        string apkPath,
        string packageName,
        bool launchAfter,
        IProgress<InstallProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var adb = AndroidSdkTools.FindAdb()
            ?? throw new InvalidOperationException("ADB를 찾을 수 없습니다. Android SDK를 설치하거나 ANDROID_HOME 환경변수를 설정해주세요.");

        if (!File.Exists(apkPath))
        {
            const string err = "APK 파일을 찾을 수 없습니다.";
            progress?.Report(new InstallProgress(InstallStatus.Error, 0, err));
            throw new InvalidOperationException(err);
        }

        var fileSizeMb = new FileInfo(apkPath).Length / 1_000_000.0;
        progress?.Report(new InstallProgress(InstallStatus.Starting, 0, "설치 준비 중..."));

        var psi = new ProcessStartInfo(adb)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in new[] { "-s", deviceId, "install", "-r", apkPath })
        {
            psi.ArgumentList.Add(a);
        }

        Process process;
        try
        {
            process = Process.Start(psi)!;
        }
        catch (Exception e)
        {
            var err = $"ADB 실행 실패: {e.Message}";
            progress?.Report(new InstallProgress(InstallStatus.Error, 0, err));
            throw new InvalidOperationException(err);
        }

        progress?.Report(new InstallProgress(InstallStatus.Installing, 10, $"APK 전송 중... ({fileSizeMb:F1} MB)"));

        var stdoutLines = new List<string>();
        var readStdout = Task.Run(async () =>
        {
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false)) is not null)
            {
                stdoutLines.Add(line);
                if (line.Contains("Performing") || line.Contains("pkg:"))
                {
                    progress?.Report(new InstallProgress(InstallStatus.Installing, 50, "디바이스에 설치 중..."));
                }
            }
        }, cancellationToken);

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        await readStdout.ConfigureAwait(false);
        var stderrOutput = await stderrTask.ConfigureAwait(false);

        var stdoutOutput = string.Join('\n', stdoutLines);
        var combinedOutput = $"{stdoutOutput}\n{stderrOutput}";

        if (process.ExitCode == 0 && (combinedOutput.Contains("Success") || stdoutOutput.Contains("Success")))
        {
            if (launchAfter && !string.IsNullOrEmpty(packageName))
            {
                progress?.Report(new InstallProgress(InstallStatus.Installing, 95, "앱 실행 중..."));
                await ProcessRunner.RunAsync(adb, [
                    "-s", deviceId, "shell", "monkey",
                    "-p", packageName,
                    "-c", "android.intent.category.LAUNCHER",
                    "1",
                ], cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            var successMsg = launchAfter ? "APK 설치 완료! 앱을 실행했습니다." : "APK 설치 완료!";
            progress?.Report(new InstallProgress(InstallStatus.Completed, 100, successMsg));
            return successMsg;
        }

        var errorMsg = AndroidSdkTools.ParseAdbError(combinedOutput);
        progress?.Report(new InstallProgress(InstallStatus.Error, 0, errorMsg));
        throw new InvalidOperationException(errorMsg);
    }
}
