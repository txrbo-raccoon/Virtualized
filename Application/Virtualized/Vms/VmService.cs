using Virtualized.Qemu;

namespace Virtualized.Vms;

/// <summary>Creates, updates and deletes VMs: validation, disk images and the vm.json in one place.</summary>
public sealed class VmService(VmRepository repository, QemuEnvironment qemu, VmManager manager)
{
    public VmRepository Repository => repository;

    public async Task<Dictionary<string, string>> ValidateAsync(VmDraft draft)
    {
        var existing = await repository.ListAsync();
        return VmValidator.Validate(draft, existing.Vms, qemu.Installation, HostInfo.ProcessorCount, HostInfo.MaxGuestMemoryMiB);
    }

    public async Task<VmDefinition> CreateAsync(VmDraft draft, CancellationToken ct = default)
    {
        if (!draft.IsNew)
            throw new InvalidOperationException("The draft belongs to an existing VM.");

        var qemuImg = qemu.Installation?.QemuImgPath
                      ?? throw new InvalidOperationException("qemu-img wasn't found.");

        var vm = draft.ToDefinition();
        var directory = repository.GetDirectory(vm.Id);
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var disk in vm.Disks)
                await QemuImg.CreateQcow2Async(qemuImg, Path.Combine(directory, disk.File), disk.SizeGiB, ct);

            await repository.SaveAsync(vm, ct);
            return vm;
        }
        catch
        {
            // Don't leave a half-created VM folder (a disk without vm.json) behind.
            Directory.Delete(directory, recursive: true);
            throw;
        }
    }

    public async Task<VmDefinition> UpdateAsync(VmDraft draft, CancellationToken ct = default)
    {
        if (draft.IsNew)
            throw new InvalidOperationException("The draft is for a new VM.");

        var vm = draft.ToDefinition();
        await repository.SaveAsync(vm, ct);
        return vm;
    }

    /// <summary>
    /// Changes the VM's ISO (null empties the drive). A running VM gets it right away; either way it's
    /// saved so the next start uses it too.
    /// </summary>
    public async Task<VmDefinition> SetInstallMediaAsync(Guid id, string? isoPath)
    {
        var vm = await repository.GetAsync(id) ?? throw new InvalidOperationException("The VM no longer exists.");
        if (isoPath is not null && !File.Exists(isoPath))
            throw new FileNotFoundException("The ISO image doesn't exist.", isoPath);

        if (manager.IsActive(id))
            await manager.ChangeMediaAsync(id, isoPath);

        var updated = vm with { InstallMedia = isoPath };
        await repository.SaveAsync(updated);
        return updated;
    }

    public Task DeleteAsync(Guid id)
    {
        if (manager.IsActive(id))
            throw new InvalidOperationException("Stop the VM before deleting it.");

        return repository.DeleteAsync(id);
    }
}
