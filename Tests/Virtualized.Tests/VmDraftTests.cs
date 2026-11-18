using Virtualized.Vms;

namespace Virtualized.Tests;

public class VmDraftTests
{
    [Fact]
    public void Windows11PresetFallsBackToCompatibilityOnWindowsHosts()
    {
        var draft = new VmDraft { Architecture = "x86_64" };

        draft.ApplyPreset(GuestOs.Windows11, TestQemu.Windows());

        Assert.False(draft.Tpm);
        Assert.False(draft.SecureBoot);
        Assert.True(draft.Windows11Compatibility);
        Assert.Equal(FirmwareType.Uefi, draft.Firmware);
    }

    [Fact]
    public void Windows11PresetUsesTpmAndSecureBootWhenAvailable()
    {
        var draft = new VmDraft { Architecture = "x86_64" };

        draft.ApplyPreset(GuestOs.Windows11, TestQemu.LinuxWithSwtpm());

        Assert.True(draft.Tpm);
        Assert.True(draft.SecureBoot);
        Assert.False(draft.Windows11Compatibility);
    }

    [Fact]
    public void Aarch64PresetAlwaysUsesUefi()
    {
        var draft = new VmDraft { Architecture = "aarch64" };

        draft.ApplyPreset(GuestOs.Other, TestQemu.Windows());

        Assert.Equal(FirmwareType.Uefi, draft.Firmware);
    }

    [Fact]
    public void NewDraftCreatesOneDiskOfTheChosenSize()
    {
        var vm = new VmDraft { Name = " Debian ", DiskSizeGiB = 20 }.ToDefinition();

        Assert.Equal("Debian", vm.Name);
        Assert.Equal([new VmDisk("disk0.qcow2", 20)], vm.Disks);
    }

    [Fact]
    public void EditingKeepsIdDisksAndCreationDate()
    {
        var original = new VmDefinition
        {
            Name = "Debian",
            Disks = [new VmDisk("disk0.qcow2", 20)],
            CreatedUtc = new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
        };

        var draft = VmDraft.FromDefinition(original);
        draft.Name = "Debian 13";
        draft.DiskSizeGiB = 999;
        var saved = draft.ToDefinition();

        Assert.Equal(original.Id, saved.Id);
        Assert.Equal(original.Disks, saved.Disks);
        Assert.Equal(original.CreatedUtc, saved.CreatedUtc);
        Assert.Equal("Debian 13", saved.Name);
    }

    [Fact]
    public void CompatibilityOnlyAppliesToWindows11()
    {
        var vm = new VmDraft { Name = "x", GuestOs = GuestOs.Linux, Windows11Compatibility = true }.ToDefinition();

        Assert.False(vm.Windows11Compatibility);
    }

    [Fact]
    public void BiosDropsSecureBoot()
    {
        var vm = new VmDraft { Name = "x", Firmware = FirmwareType.Bios, SecureBoot = true }.ToDefinition();

        Assert.False(vm.SecureBoot);
    }

    [Fact]
    public void DisabledNetworkDropsPortForwards()
    {
        var draft = new VmDraft { Name = "x", NetworkEnabled = false };
        draft.PortForwards.Add(new PortForwardDraft { HostPort = 2222, GuestPort = 22 });

        Assert.Empty(draft.ToDefinition().PortForwards);
    }
}
