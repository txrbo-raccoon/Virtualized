using Virtualized.Vms;

namespace Virtualized.Tests;

public sealed class VmRepositoryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqemu-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private static VmDefinition Vm(string name) => new()
    {
        Name = name,
        GuestOs = GuestOs.Windows11,
        CpuCores = 4,
        MemoryMiB = 4096,
        Disks = [new VmDisk("disk0.qcow2", 64)],
        InstallMedia = @"C:\isos\win11.iso",
        Firmware = FirmwareType.Uefi,
        Windows11Compatibility = true,
        PortForwards = [new PortForward(PortProtocol.Tcp, 3389, 3389)],
    };

    [Fact]
    public async Task ListOfMissingRootIsEmpty()
    {
        var list = await new VmRepository(_root).ListAsync();

        Assert.Empty(list.Vms);
        Assert.Empty(list.Errors);
    }

    [Fact]
    public async Task SavedVmRoundTrips()
    {
        var repository = new VmRepository(_root);
        var vm = Vm("Windows 11");

        await repository.SaveAsync(vm);
        var loaded = await repository.GetAsync(vm.Id);

        Assert.NotNull(loaded);
        Assert.Equal(vm with { Disks = loaded.Disks, PortForwards = loaded.PortForwards }, loaded);
        Assert.Equal(vm.Disks, loaded.Disks);
        Assert.Equal(vm.PortForwards, loaded.PortForwards);
    }

    [Fact]
    public async Task EnumsAreStoredAsText()
    {
        var repository = new VmRepository(_root);
        var vm = Vm("Windows 11");

        await repository.SaveAsync(vm);
        var json = await File.ReadAllTextAsync(Path.Combine(repository.GetDirectory(vm.Id), VmRepository.DefinitionFileName));

        Assert.Contains("\"Windows11\"", json);
        Assert.Contains("\"Uefi\"", json);
        Assert.Contains("\"Tcp\"", json);
    }

    [Fact]
    public async Task ListIsSortedByNameAndReportsBrokenFolders()
    {
        var repository = new VmRepository(_root);
        await repository.SaveAsync(Vm("beta"));
        await repository.SaveAsync(Vm("Alpha"));
        var broken = Path.Combine(_root, "broken");
        Directory.CreateDirectory(broken);
        await File.WriteAllTextAsync(Path.Combine(broken, VmRepository.DefinitionFileName), "{ not json");

        var list = await repository.ListAsync();

        Assert.Equal(["Alpha", "beta"], list.Vms.Select(vm => vm.Name));
        Assert.Equal(broken, Assert.Single(list.Errors).Directory);
    }

    [Fact]
    public async Task VmFromNewerSchemaIsReportedNotLoaded()
    {
        var repository = new VmRepository(_root);
        var vm = Vm("Future") with { SchemaVersion = VmDefinition.CurrentSchemaVersion + 1 };
        await repository.SaveAsync(vm);

        var list = await repository.ListAsync();

        Assert.Empty(list.Vms);
        Assert.Single(list.Errors);
    }

    [Fact]
    public async Task DeleteRemovesTheWholeFolder()
    {
        var repository = new VmRepository(_root);
        var vm = Vm("Doomed");
        await repository.SaveAsync(vm);
        await File.WriteAllTextAsync(Path.Combine(repository.GetDirectory(vm.Id), "disk0.qcow2"), "");

        await repository.DeleteAsync(vm.Id);

        Assert.False(Directory.Exists(repository.GetDirectory(vm.Id)));
        Assert.Null(await repository.GetAsync(vm.Id));
    }

    [Fact]
    public async Task SaveRaisesChanged()
    {
        var repository = new VmRepository(_root);
        var raised = 0;
        repository.Changed += () => raised++;

        await repository.SaveAsync(Vm("x"));

        Assert.Equal(1, raised);
    }
}
