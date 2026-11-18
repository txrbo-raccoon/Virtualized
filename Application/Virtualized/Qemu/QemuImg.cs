namespace Virtualized.Qemu;

public static class QemuImg
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    /// <summary>Creates a thin-provisioned qcow2 image; it only grows on disk as the guest writes to it.</summary>
    public static async Task CreateQcow2Async(string qemuImgPath, string imagePath, int sizeGiB, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync(
            qemuImgPath, ["create", "-q", "-f", "qcow2", imagePath, $"{sizeGiB}G"], Timeout, ct);

        if (result.ExitCode != 0)
            throw new QemuImgException($"qemu-img couldn't create {Path.GetFileName(imagePath)}: {result.StdErr.Trim()}");
    }

    /// <summary>Copies an image into a new qcow2 file, e.g. a raw UEFI variable store template.</summary>
    public static async Task ConvertToQcow2Async(string qemuImgPath, string source, string sourceFormat, string destination, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync(
            qemuImgPath, ["convert", "-q", "-f", sourceFormat, "-O", "qcow2", source, destination], Timeout, ct);

        if (result.ExitCode != 0)
            throw new QemuImgException($"qemu-img couldn't create {Path.GetFileName(destination)}: {result.StdErr.Trim()}");
    }
}

/// <summary>An internal qcow2 snapshot as qemu-img or QMP report it.</summary>
/// <param name="VmStateSize">Bytes of saved memory and device state; 0 for a disk-only snapshot.</param>
public sealed record ImageSnapshot(string Name, DateTime CreatedUtc, long VmStateSize);

public static partial class QemuImgSnapshots
{
    /// <summary>
    /// Reads the snapshots of a qcow2 image. "-U" (force share) lets it read an image a running QEMU has
    /// open; only the snapshot table is read, which QEMU changes atomically.
    /// </summary>
    public static async Task<IReadOnlyList<ImageSnapshot>> ListAsync(string qemuImgPath, string imagePath, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync(qemuImgPath, ["info", "-U", "--output=json", imagePath], TimeSpan.FromSeconds(30), ct);
        if (result.ExitCode != 0)
            throw new QemuImgException($"qemu-img couldn't read {Path.GetFileName(imagePath)}: {result.StdErr.Trim()}");

        return ParseInfo(result.StdOut);
    }

    /// <summary>Parses the "snapshots" array of qemu-img info's JSON (absent when there are none).</summary>
    internal static IReadOnlyList<ImageSnapshot> ParseInfo(string json)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(json);
        return root?["snapshots"]?.AsArray().Select(s => FromJson(s!)).ToList() ?? [];
    }

    /// <summary>qemu-img info and QMP's query-block describe snapshots with the same fields.</summary>
    internal static ImageSnapshot FromJson(System.Text.Json.Nodes.JsonNode snapshot) => new(
        snapshot["name"]!.GetValue<string>(),
        DateTime.UnixEpoch.AddSeconds(snapshot["date-sec"]!.GetValue<long>()).AddTicks(snapshot["date-nsec"]!.GetValue<long>() / 100),
        snapshot["vm-state-size"]?.GetValue<long>() ?? 0);

    /// <summary>Creates (-c), applies (-a) or deletes (-d) a snapshot in an image that no QEMU has open.</summary>
    public static async Task RunAsync(string qemuImgPath, char operation, string name, string imagePath, CancellationToken ct = default)
    {
        var result = await ProcessRunner.RunAsync(qemuImgPath, ["snapshot", $"-{operation}", name, imagePath], TimeSpan.FromMinutes(5), ct);
        if (result.ExitCode != 0)
            throw new QemuImgException($"qemu-img snapshot -{operation} failed on {Path.GetFileName(imagePath)}: {result.StdErr.Trim()}");
    }
}

public sealed class QemuImgException(string message) : Exception(message);
