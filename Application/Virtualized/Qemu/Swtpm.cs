using System.Diagnostics;

namespace Virtualized.Qemu;

/// <summary>
/// Runs swtpm, the software TPM 2.0 that QEMU's "emulator" TPM backend talks to. Only on Linux and
/// macOS: QEMU on Windows has no TPM backend at all. The TPM's state (keys, measurements) lives in the
/// VM's folder so it survives restarts, which BitLocker and Windows Hello rely on.
/// </summary>
public static class Swtpm
{
    public const string StateFolderName = "tpm";
    public const string LogFileName = "swtpm.log";

    /// <summary>sun_path is 104 bytes on macOS and 108 on Linux; stay below both with room for the terminator.</summary>
    private const int MaxSocketPathLength = 100;

    private static readonly TimeSpan SocketTimeout = TimeSpan.FromSeconds(5);

    /// <summary>"--terminate" makes swtpm exit once QEMU closes the connection, so it never outlives its VM.</summary>
    public static IReadOnlyList<string> BuildArguments(string stateDirectory, string socketPath, string logPath) =>
    [
        "socket", "--tpm2",
        "--tpmstate", $"dir={stateDirectory}",
        "--ctrl", $"type=unixio,path={socketPath}",
        "--terminate",
        "--log", $"file={logPath},level=5",
    ];

    /// <summary>The control socket goes next to the VM, or in the temp folder when that path would be too long.</summary>
    public static string SocketPath(string vmDirectory, Guid vmId)
    {
        var local = Path.Combine(vmDirectory, "swtpm.sock");
        return local.Length <= MaxSocketPathLength ? local : Path.Combine(Path.GetTempPath(), $"sqemu-{vmId:N}.sock");
    }

    /// <summary>Starts swtpm for a VM and waits until its socket exists, so QEMU can connect right away.</summary>
    public static async Task<Process> StartAsync(string swtpmPath, string vmDirectory, Guid vmId, CancellationToken ct = default)
    {
        var state = Path.Combine(vmDirectory, StateFolderName);
        var socket = SocketPath(vmDirectory, vmId);
        var log = Path.Combine(vmDirectory, LogFileName);
        Directory.CreateDirectory(state);
        File.Delete(socket);

        // Started from the temp folder, not the VM's own, so the VM's folder can be deleted later.
        var process = ProcessLauncher.Start(swtpmPath, BuildArguments(state, socket, log), log, Path.GetTempPath());
        var deadline = DateTime.UtcNow + SocketTimeout;
        while (!File.Exists(socket))
        {
            if (process.HasExited || DateTime.UtcNow > deadline)
            {
                if (!process.HasExited)
                    process.Kill();
                var details = File.Exists(log) ? (await File.ReadAllTextAsync(log, ct)).Trim() : "";
                throw new QemuConfigurationException($"swtpm didn't start{(details.Length > 0 ? $": {details}" : ".")}");
            }

            await Task.Delay(50, ct);
        }

        return process;
    }
}
