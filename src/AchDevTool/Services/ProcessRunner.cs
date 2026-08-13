using System.Diagnostics;

namespace AchDevTool.Services;

public sealed record ProcessResult(int ExitCode, string Stdout, string Stderr)
{
    public bool Success => ExitCode == 0;
}

/// <summary>Small helper around <see cref="Process"/> for the "run a CLI, capture output"
/// pattern shared by the git/adb/aapt integrations.</summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult?> RunAsync(
        string fileName,
        IEnumerable<string> args,
        string? workingDirectory = null,
        IDictionary<string, string>? env = null,
        CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args)
        {
            psi.ArgumentList.Add(a);
        }
        if (workingDirectory is not null)
        {
            psi.WorkingDirectory = workingDirectory;
        }
        if (env is not null)
        {
            foreach (var (key, value) in env)
            {
                psi.Environment[key] = value;
            }
        }

        try
        {
            using var process = Process.Start(psi);
            if (process is null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            var stdout = await stdoutTask.ConfigureAwait(false);
            var stderr = await stderrTask.ConfigureAwait(false);
            return new ProcessResult(process.ExitCode, stdout, stderr);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
