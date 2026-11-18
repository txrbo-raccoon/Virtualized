using System.Text.Json.Nodes;
using Virtualized.Qemu;

namespace Virtualized.Vms;

/// <summary>A snapshot of a VM, as stored inside its qcow2 images.</summary>
/// <param name="MemorySize">Bytes of saved memory; 0 for a disk-only snapshot.</param>
public sealed record VmSnapshot(string Name, DateTime CreatedUtc, long MemorySize)
{
    /// <summary>Restoring brings back the running VM exactly, not just its disks.</summary>
    public bool HasMemory => MemorySize > 0;
}

public enum SnapshotKind
{
    /// <summary>Disks and the running VM's memory: restoring resumes it where it was.</summary>
    WithMemory,

    /// <summary>Disks only: restoring is like booting after a power cut at that moment.</summary>
    DiskOnly,
}

/// <summary>Restoring this snapshot needs the VM to be turned off first.</summary>
public sealed class SnapshotRequiresStopException(string message) : Exception(message);

/// <summary>
/// Internal qcow2 snapshots of a VM's disks and UEFI variables. A running VM gets a snapshot with its
/// memory where the accelerator allows it (WHPX doesn't: QEMU blocks saving its state), and a disk-only
/// one otherwise; a stopped VM gets disk-only snapshots through qemu-img.
/// </summary>
public sealed class SnapshotService(VmRepository repository, VmManager manager, QemuEnvironment qemu)
{
    public const int MaxNameLength = 64;

    public event Action<Guid>? Changed;

    public async Task<IReadOnlyList<VmSnapshot>> ListAsync(VmDefinition vm, CancellationToken ct = default)
    {
        var images = await _SnapshotsByImageAsync(vm, ct);
        var main = images.FirstOrDefault(i => i.Node == QemuCommandBuilder.DiskNodeName(0));
        return main.Snapshots?
                   .Select(s => new VmSnapshot(s.Name, s.CreatedUtc, s.VmStateSize))
                   .OrderBy(s => s.CreatedUtc)
                   .ToList()
               ?? [];
    }

    /// <summary>Null if the name can be used, otherwise why not.</summary>
    public static string? ValidateName(string name, IEnumerable<VmSnapshot> existing)
    {
        name = name.Trim();
        if (name.Length == 0)
            return "Give the snapshot a name.";
        if (name.Length > MaxNameLength)
            return $"Use at most {MaxNameLength} characters.";
        // qemu-img and QMP look snapshots up by id or name; a number would be taken as an id.
        if (name.All(char.IsAsciiDigit))
            return "Use at least one letter: plain numbers are taken as snapshot ids.";
        if (existing.Any(s => s.Name == name))
            return "There's already a snapshot with this name.";
        return null;
    }

    public async Task<SnapshotKind> CreateAsync(VmDefinition vm, string name, CancellationToken ct = default)
    {
        name = name.Trim();
        var images = _Images(vm);
        SnapshotKind kind;

        if (!manager.IsActive(vm.Id))
        {
            foreach (var (_, path) in images)
                await QemuImgSnapshots.RunAsync(_QemuImg, 'c', name, path, ct);
            kind = SnapshotKind.DiskOnly;
        }
        else if (SupportsMemorySnapshots(_Accelerator(vm)) && await _TrySaveWithMemoryAsync(vm, name, images))
        {
            kind = SnapshotKind.WithMemory;
        }
        else
        {
            await _SaveDiskOnlyWhileRunningAsync(vm, name, images);
            kind = SnapshotKind.DiskOnly;
        }

        Changed?.Invoke(vm.Id);
        return kind;
    }

    /// <summary>
    /// Restores a snapshot. A disk-only one can't be applied under a running VM (its memory would no
    /// longer match its disks), so with <paramref name="stopIfNeeded"/> false that throws
    /// <see cref="SnapshotRequiresStopException"/> for the caller to confirm first.
    /// </summary>
    public async Task RestoreAsync(VmDefinition vm, VmSnapshot snapshot, bool stopIfNeeded, CancellationToken ct = default)
    {
        var running = manager.IsActive(vm.Id);
        if (snapshot.HasMemory && running)
        {
            // snapshot-load pauses the VM while it reads the saved state, then lets it run on.
            manager.ExpectPause(vm.Id);
            await _LoadWithMemoryAsync(vm, snapshot.Name, ct);
        }
        else if (snapshot.HasMemory)
        {
            // QEMU starts with its CPUs stopped, loads the saved state, then runs from there.
            await manager.StartAsync(vm, paused: true, ct);
            try
            {
                await _LoadWithMemoryAsync(vm, snapshot.Name, ct);
                await manager.ResumeAsync(vm.Id);
            }
            catch
            {
                // A failed load leaves QEMU stuck in "restore-vm"; nothing useful can run from there.
                await manager.ForceStopAsync(vm.Id);
                throw;
            }
        }
        else
        {
            if (running)
            {
                if (!stopIfNeeded)
                    throw new SnapshotRequiresStopException($"{vm.Name} has to be turned off to restore a disk-only snapshot.");
                await manager.ForceStopAsync(vm.Id);
            }

            foreach (var (_, path) in await _ImagesWithAsync(vm, snapshot.Name, ct))
                await QemuImgSnapshots.RunAsync(_QemuImg, 'a', snapshot.Name, path, ct);
        }

        Changed?.Invoke(vm.Id);
    }

    public async Task DeleteAsync(VmDefinition vm, string name, CancellationToken ct = default)
    {
        if (manager.IsActive(vm.Id))
        {
            await _RunJobAsync(vm.Id, "snapshot-delete", new()
            {
                ["tag"] = name,
                ["devices"] = await _NodesWithAsync(vm, name, ct),
            });
        }
        else
        {
            foreach (var (_, path) in await _ImagesWithAsync(vm, name, ct))
                await QemuImgSnapshots.RunAsync(_QemuImg, 'd', name, path, ct);
        }

        Changed?.Invoke(vm.Id);
    }

    /// <summary>Loads disks and memory. Only for snapshots that have memory: on a disk-only one QEMU would get stuck.</summary>
    private async Task _LoadWithMemoryAsync(VmDefinition vm, string name, CancellationToken ct) =>
        await _RunJobAsync(vm.Id, "snapshot-load", new()
        {
            ["tag"] = name,
            ["vmstate"] = QemuCommandBuilder.DiskNodeName(0),
            ["devices"] = await _NodesWithAsync(vm, name, ct),
        });

    /// <summary>WHPX registers a migration blocker, so QEMU refuses to save its state (verified with QEMU 11.1).</summary>
    public static bool SupportsMemorySnapshots(string accelerator) => accelerator != "whpx";

    /// <summary>Saves disks and memory. False when QEMU refuses because the accelerator can't save its state.</summary>
    private async Task<bool> _TrySaveWithMemoryAsync(VmDefinition vm, string name, IReadOnlyList<(string Node, string Path)> images)
    {
        manager.ExpectPause(vm.Id);
        try
        {
            await _RunJobAsync(vm.Id, "snapshot-save", new()
            {
                ["tag"] = name,
                ["vmstate"] = QemuCommandBuilder.DiskNodeName(0),
                ["devices"] = images.Select(i => i.Node).ToArray(),
            });
            return true;
        }
        catch (QmpException ex) when (ex.Message.Contains("blocked", StringComparison.OrdinalIgnoreCase)
                                      || ex.Message.Contains("migrat", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

    /// <summary>Pauses the VM so all its images are captured at the same instant, then lets it run again.</summary>
    private async Task _SaveDiskOnlyWhileRunningAsync(VmDefinition vm, string name, IReadOnlyList<(string Node, string Path)> images)
    {
        var wasRunning = manager.GetInfo(vm.Id).Status == VmStatus.Running;
        if (wasRunning)
            await manager.PauseAsync(vm.Id);
        try
        {
            foreach (var (node, _) in images)
                await manager.ExecuteQmpAsync(vm.Id, "blockdev-snapshot-internal-sync", new Dictionary<string, object> { ["device"] = node, ["name"] = name });
        }
        finally
        {
            if (wasRunning)
                await manager.ResumeAsync(vm.Id);
        }
    }

    /// <summary>Runs a QMP job (snapshot-save/-load/-delete) to completion, throwing its error if it failed.</summary>
    private async Task _RunJobAsync(Guid vmId, string command, Dictionary<string, object> arguments)
    {
        var jobId = $"sqemu-{Guid.NewGuid():N}";
        arguments["job-id"] = jobId;
        await manager.ExecuteQmpAsync(vmId, command, arguments);

        while (true)
        {
            var jobs = (await manager.ExecuteQmpAsync(vmId, "query-jobs"))?.AsArray() ?? [];
            var job = jobs.FirstOrDefault(j => j?["id"]?.GetValue<string>() == jobId);
            if (job?["status"]?.GetValue<string>() == "concluded")
            {
                await manager.ExecuteQmpAsync(vmId, "job-dismiss", new Dictionary<string, object> { ["id"] = jobId });
                if (job["error"]?.GetValue<string>() is { } error)
                    throw new QmpException(error);
                return;
            }

            await Task.Delay(100);
        }
    }

    /// <summary>The VM's snapshot-capable images: its disks and, for UEFI, its variable store.</summary>
    private IReadOnlyList<(string Node, string Path)> _Images(VmDefinition vm)
    {
        var directory = repository.GetDirectory(vm.Id);
        var images = vm.Disks.Select((disk, i) => (QemuCommandBuilder.DiskNodeName(i), Path.Combine(directory, disk.File))).ToList();
        var vars = Path.Combine(directory, QemuLaunchContext.EfiVarsFileName);
        if (vm.Firmware == FirmwareType.Uefi && File.Exists(vars))
            images.Add((QemuCommandBuilder.EfiVarsNodeName, vars));
        return images;
    }

    /// <summary>Snapshots per image, from QEMU when the VM runs and from the files otherwise.</summary>
    private async Task<List<(string Node, string Path, IReadOnlyList<ImageSnapshot> Snapshots)>> _SnapshotsByImageAsync(VmDefinition vm, CancellationToken ct)
    {
        var images = _Images(vm);
        if (manager.IsActive(vm.Id))
        {
            var blocks = (await manager.ExecuteQmpAsync(vm.Id, "query-block"))?.AsArray() ?? [];
            var byNode = blocks
                .Select(b => b?["inserted"])
                .Where(i => i?["node-name"] is not null)
                .ToDictionary(i => i!["node-name"]!.GetValue<string>(), i => i!["image"]?["snapshots"]?.AsArray());
            return images.Select(i => (i.Node, i.Path, (IReadOnlyList<ImageSnapshot>)(byNode.GetValueOrDefault(i.Node)?
                .Select(s => QemuImgSnapshots.FromJson(s!)).ToList() ?? []))).ToList();
        }

        var result = new List<(string, string, IReadOnlyList<ImageSnapshot>)>();
        foreach (var (node, path) in images)
            result.Add((node, path, await QemuImgSnapshots.ListAsync(_QemuImg, path, ct)));
        return result;
    }

    /// <summary>
    /// Only the images that actually hold the snapshot: a UEFI variable store recreated after a firmware
    /// change doesn't have the older ones, and naming it would make QEMU refuse the whole operation.
    /// </summary>
    private async Task<List<(string Node, string Path)>> _ImagesWithAsync(VmDefinition vm, string name, CancellationToken ct) =>
        (await _SnapshotsByImageAsync(vm, ct))
        .Where(i => i.Snapshots.Any(s => s.Name == name))
        .Select(i => (i.Node, i.Path))
        .ToList();

    private async Task<string[]> _NodesWithAsync(VmDefinition vm, string name, CancellationToken ct) =>
        (await _ImagesWithAsync(vm, name, ct)).Select(i => i.Node).ToArray();

    private string _Accelerator(VmDefinition vm) =>
        qemu.Installation?.FindBinary(vm.Architecture)?.BestAccelerator ?? "tcg";

    private string _QemuImg => qemu.Installation?.QemuImgPath ?? throw new InvalidOperationException("qemu-img wasn't found.");
}
