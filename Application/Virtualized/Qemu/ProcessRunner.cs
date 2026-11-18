using System.Diagnostics;

namespace Virtualized.Qemu;

public sealed record ProcessResult(int ExitCode, string StdOut, string StdErr);

/// <summary>Runs short-lived tools (qemu --version, -accel help, qemu-img...) and captures their output.</summary>
internal static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(string fileName, IEnumerable<string> arguments, TimeSpan timeout, CancellationToken ct = default)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var argument in arguments)
            startInfo.ArgumentList.Add(argument);

        using var process = Process.Start(startInfo)!;
        var stdOut = process.StandardOutput.ReadToEndAsync(ct);
        var stdErr = process.StandardError.ReadToEndAsync(ct);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"{Path.GetFileName(fileName)} did not finish within {timeout.TotalSeconds:0}s.");
        }

        return new ProcessResult(process.ExitCode, await stdOut, await stdErr);
    }
}
