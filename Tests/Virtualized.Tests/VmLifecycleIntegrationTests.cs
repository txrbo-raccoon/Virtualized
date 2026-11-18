using System.Text.Json.Nodes;
using Virtualized.Qemu;
using Virtualized.Settings;
using Virtualized.Vms;

namespace Virtualized.Tests;

/// <summary>
/// Drives a real QEMU through VmService and VmManager: start, pause/resume, change and eject the ISO,
/// reset, force stop. Returns early (passes) when no QEMU is installed.
/// </summary>
[Trait("Category", "Integration")]
public sealed class VmLifecycleIntegrationTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqemu-it", Guid.NewGuid().ToString("N"));
    private QemuEnvironment _qemu = null!;
    private VmManager _manager = null!;
    private VmService _service = null!;

    public async Task InitializeAsync()
    {
        _qemu = new QemuEnvironment(new SettingsStore(), new QemuLocator());
        await _qemu.RefreshAsync();
        var repository = new VmRepository(_root);
        _manager = new VmManager(repository, _qemu);
        _service = new VmService(repository, _qemu, _manager);
    }

    public async Task DisposeAsync()
    {
        await _manager.DisposeAsync();
        // Same retrying delete the app uses for a VM's folder.
        foreach (var directory in Directory.Exists(_root) ? Directory.GetDirectories(_root) : [])
            await new VmRepository(_root).DeleteAsync(Guid.ParseExact(Path.GetFileName(directory), "N"));
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private async Task<VmStatus> WaitForAsync(Guid id, VmStatus status)
    {
        for (var i = 0; i < 100 && _manager.GetInfo(id).Status != status; i++)
            await Task.Delay(100);
        return _manager.GetInfo(id).Status;
    }

    private async Task<string?> CdromFileAsync(Guid id)
    {
        var devices = (await _manager.ExecuteQmpAsync(id, "query-block"))!.AsArray();
        var cdrom = devices.First(d => d!["qdev"]?.GetValue<string>()?.Contains(QemuCommandBuilder.CdromDeviceId) == true)!;
        return cdrom["inserted"]?["file"]?.GetValue<string>();
    }

    private async Task<VmDefinition> _CreateAsync(string name, string architecture)
    {
        var draft = new VmDraft { Name = name, DiskSizeGiB = 1, Architecture = architecture };
        draft.ApplyPreset(GuestOs.Linux, _qemu.Installation);
        draft.CpuCores = 1;
        draft.MemoryMiB = 512;
        return await _service.CreateAsync(draft);
    }

    [Fact]
    public async Task DiskOnlySnapshotsUnderWhpx()
    {
        if (_qemu.Installation?.FindBinary("x86_64") is not { BestAccelerator: "whpx" })
            return;

        var snapshots = new SnapshotService(_service.Repository, _manager, _qemu);
        var vm = await _CreateAsync("Snapshots", "x86_64");

        // Stopped VM: qemu-img; before the first start there are no UEFI variables yet, only the disk.
        Assert.Equal(SnapshotKind.DiskOnly, await snapshots.CreateAsync(vm, "fresh"));

        await _manager.StartAsync(vm);
        try
        {
            // Running under WHPX: QEMU can't save memory, so it's disk-only, and the VM keeps running.
            Assert.Equal(SnapshotKind.DiskOnly, await snapshots.CreateAsync(vm, "running"));
            Assert.Equal(VmStatus.Running, await WaitForAsync(vm.Id, VmStatus.Running));
            var list = await snapshots.ListAsync(vm);
            Assert.Equal(["fresh", "running"], list.Select(s => s.Name));
            Assert.All(list, s => Assert.False(s.HasMemory));

            // A disk-only snapshot can't be restored under the running VM without stopping it.
            await Assert.ThrowsAsync<SnapshotRequiresStopException>(() => snapshots.RestoreAsync(vm, list[1], stopIfNeeded: false));
            Assert.Equal(VmStatus.Running, _manager.GetInfo(vm.Id).Status);

            await snapshots.DeleteAsync(vm, "running");
            Assert.Equal(["fresh"], (await snapshots.ListAsync(vm)).Select(s => s.Name));

            // "fresh" was taken before UEFI variables existed; restoring only touches the disk, which has it.
            await snapshots.RestoreAsync(vm, (await snapshots.ListAsync(vm))[0], stopIfNeeded: true);
            Assert.Equal(VmStatus.Stopped, _manager.GetInfo(vm.Id).Status);
        }
        finally
        {
            if (_manager.IsActive(vm.Id))
                await _manager.ForceStopAsync(vm.Id);
        }

        await snapshots.DeleteAsync(vm, "fresh");
        Assert.Empty(await snapshots.ListAsync(vm));
    }

    [Fact]
    public async Task SnapshotsWithMemoryUnderTcg()
    {
        // aarch64 guests on an x86 host run under TCG, which can save memory.
        if (_qemu.Installation?.FindBinary("aarch64") is not { BestAccelerator: "tcg" })
            return;

        var snapshots = new SnapshotService(_service.Repository, _manager, _qemu);
        var vm = await _CreateAsync("Memory", "aarch64");
        await _manager.StartAsync(vm);
        try
        {
            await Task.Delay(3000);
            Assert.Equal(SnapshotKind.WithMemory, await snapshots.CreateAsync(vm, "live"));
            var live = Assert.Single(await snapshots.ListAsync(vm));
            Assert.True(live.HasMemory);
            Assert.Equal(VmStatus.Running, await WaitForAsync(vm.Id, VmStatus.Running));

            // Restoring with memory while running: snapshot-load, and the VM carries on.
            await snapshots.RestoreAsync(vm, live, stopIfNeeded: false);
            Assert.Equal(VmStatus.Running, await WaitForAsync(vm.Id, VmStatus.Running));
            Assert.Null(_manager.GetInfo(vm.Id).Error);

            // Restoring with memory while stopped: QEMU starts straight into the snapshot (-loadvm).
            await _manager.ForceStopAsync(vm.Id);
            await snapshots.RestoreAsync(vm, live, stopIfNeeded: false);
            Assert.Equal(VmStatus.Running, await WaitForAsync(vm.Id, VmStatus.Running));
        }
        finally
        {
            if (_manager.IsActive(vm.Id))
                await _manager.ForceStopAsync(vm.Id);
        }
    }

    [Fact]
    public async Task RecreatesTheUefiVariablesWhenTheFirmwareChanges()
    {
        if (_qemu.Installation?.QemuImgPath is null)
            return;

        var draft = new VmDraft { Name = "Firmware", DiskSizeGiB = 1 };
        draft.ApplyPreset(GuestOs.Linux, _qemu.Installation);
        draft.Architecture = "x86_64";
        draft.CpuCores = 1;
        draft.MemoryMiB = 512;
        var vm = await _service.CreateAsync(draft);
        var directory = _service.Repository.GetDirectory(vm.Id);
        var vars = Path.Combine(directory, QemuLaunchContext.EfiVarsFileName);
        var source = Path.Combine(directory, VmManager.EfiVarsSourceFileName);

        await _manager.StartAsync(vm);
        await _manager.ForceStopAsync(vm.Id);
        var expectedSource = await File.ReadAllTextAsync(source);

        // A store made from another build (e.g. before Secure Boot was turned on) is replaced. The old one
        // is swapped for an empty file QEMU couldn't boot with, so the VM only starts if it was recreated.
        // (Timestamps can't tell: the firmware writes the store on every boot.)
        await File.WriteAllBytesAsync(vars, []);
        await File.WriteAllTextAsync(source, "some-other-firmware.json|elsewhere.fd");
        await _manager.StartAsync(vm);
        await _manager.ForceStopAsync(vm.Id);

        Assert.True(new FileInfo(vars).Length > 0);
        Assert.Equal(expectedSource, await File.ReadAllTextAsync(source));
    }

    [Fact]
    public async Task RunsAVmThroughItsLifecycle()
    {
        if (_qemu.Installation?.QemuImgPath is null)
            return;

        var iso1 = Path.Combine(_root, "first.iso");
        var iso2 = Path.Combine(_root, "second disc.iso");
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(iso1, new byte[2048 * 16]);
        await File.WriteAllBytesAsync(iso2, new byte[2048 * 16]);

        var draft = new VmDraft { Name = "Integration", DiskSizeGiB = 1, InstallMedia = iso1 };
        draft.ApplyPreset(GuestOs.Linux, _qemu.Installation);
        draft.Architecture = "x86_64";
        draft.CpuCores = 1;
        draft.MemoryMiB = 512;
        var vm = await _service.CreateAsync(draft);

        await _manager.StartAsync(vm);
        try
        {
            Assert.Equal(VmStatus.Running, _manager.GetInfo(vm.Id).Status);
            Assert.Equal(iso1, await CdromFileAsync(vm.Id));

            await _manager.PauseAsync(vm.Id);
            Assert.Equal(VmStatus.Paused, await WaitForAsync(vm.Id, VmStatus.Paused));
            await _manager.ResumeAsync(vm.Id);
            Assert.Equal(VmStatus.Running, await WaitForAsync(vm.Id, VmStatus.Running));

            // Changing the ISO while running reaches QEMU and is saved for the next start.
            await _service.SetInstallMediaAsync(vm.Id, iso2);
            Assert.Equal(iso2, await CdromFileAsync(vm.Id));
            Assert.Equal(iso2, (await _service.Repository.GetAsync(vm.Id))!.InstallMedia);

            await _service.SetInstallMediaAsync(vm.Id, null);
            Assert.Null(await CdromFileAsync(vm.Id));
            Assert.Null((await _service.Repository.GetAsync(vm.Id))!.InstallMedia);

            // Reset from a paused VM leaves it running.
            await _manager.PauseAsync(vm.Id);
            await WaitForAsync(vm.Id, VmStatus.Paused);
            await _manager.ResetAsync(vm.Id);
            Assert.Equal(VmStatus.Running, await WaitForAsync(vm.Id, VmStatus.Running));

            await Assert.ThrowsAsync<InvalidOperationException>(() => _service.DeleteAsync(vm.Id));
        }
        finally
        {
            if (_manager.IsActive(vm.Id))
                await _manager.ForceStopAsync(vm.Id);
        }

        Assert.Equal(VmStatus.Stopped, _manager.GetInfo(vm.Id).Status);
        Assert.Null(_manager.GetInfo(vm.Id).Error);
        Assert.False(File.Exists(Path.Combine(_service.Repository.GetDirectory(vm.Id), VmManager.RuntimeFileName)));
    }
}
