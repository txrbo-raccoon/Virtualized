using System.Diagnostics;
using System.Text.Json;

namespace Virtualized.Qemu;

/// <summary>
/// What the app needs to find a running QEMU process again after it has been closed and reopened.
/// </summary>
public sealed record VmRuntimeState(int Pid, DateTime StartTimeUtc, int QmpPort, int VncWebSocketPort)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    public static async Task<VmRuntimeState?> LoadAsync(string path)
    {
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<VmRuntimeState>(stream);
    }

    public async Task SaveAsync(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await using var stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, this, JsonOptions);
    }

    /// <summary>
    /// Returns the QEMU process if it is still alive. The start time is compared as well,
    /// so a recycled PID belonging to an unrelated process is not mistaken for the VM.
    /// </summary>
    public Process? FindProcess()
    {
        try
        {
            var process = Process.GetProcessById(Pid);
            var started = process.StartTime.ToUniversalTime();
            if (process.HasExited || Math.Abs((started - StartTimeUtc).TotalSeconds) > 2)
                return null;

            // Keep a handle so the process can still be queried after it exits.
            _ = process.Handle;
            return process;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
