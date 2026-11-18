using Virtualized.Vms;

namespace Virtualized.Tests;

public class VmValidatorTests
{
    private const int MaxCores = 8;
    private const int MaxMemoryMiB = 16384;

    private static Dictionary<string, string> Validate(VmDraft draft, params VmDefinition[] existing) =>
        VmValidator.Validate(draft, existing, TestQemu.Windows(), MaxCores, MaxMemoryMiB);

    private static VmDraft ValidDraft() => new()
    {
        Name = "Ubuntu",
        Architecture = "x86_64",
        CpuCores = 2,
        MemoryMiB = 2048,
        DiskSizeGiB = 32,
        Firmware = FirmwareType.Uefi,
    };

    [Fact]
    public void ValidDraftHasNoErrors()
    {
        Assert.Empty(Validate(ValidDraft()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void NameIsRequired(string name)
    {
        var draft = ValidDraft();
        draft.Name = name;

        Assert.Contains(nameof(VmDraft.Name), Validate(draft).Keys);
    }

    [Fact]
    public void NameMustBeUniqueIgnoringCase()
    {
        var existing = new VmDefinition { Name = "ubuntu" };

        Assert.Contains(nameof(VmDraft.Name), Validate(ValidDraft(), existing).Keys);
    }

    [Fact]
    public void EditedVmMayKeepItsOwnName()
    {
        var existing = new VmDefinition { Name = "Ubuntu", CpuCores = 2, MemoryMiB = 2048, Firmware = FirmwareType.Uefi };

        Assert.Empty(Validate(VmDraft.FromDefinition(existing), existing));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(MaxCores + 1)]
    public void CoresMustFitTheHost(int cores)
    {
        var draft = ValidDraft();
        draft.CpuCores = cores;

        Assert.Contains(nameof(VmDraft.CpuCores), Validate(draft).Keys);
    }

    [Theory]
    [InlineData(VmValidator.MinMemoryMiB - 1)]
    [InlineData(MaxMemoryMiB + 1)]
    public void MemoryMustBeInRange(int memory)
    {
        var draft = ValidDraft();
        draft.MemoryMiB = memory;

        Assert.Contains(nameof(VmDraft.MemoryMiB), Validate(draft).Keys);
    }

    [Fact]
    public void MissingIsoIsReported()
    {
        var draft = ValidDraft();
        draft.InstallMedia = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.iso");

        Assert.Contains(nameof(VmDraft.InstallMedia), Validate(draft).Keys);
    }

    [Fact]
    public void Aarch64NeedsUefi()
    {
        var draft = ValidDraft();
        draft.Architecture = "aarch64";
        draft.Firmware = FirmwareType.Bios;

        Assert.Contains(nameof(VmDraft.Firmware), Validate(draft).Keys);
    }

    [Fact]
    public void TpmIsRejectedOnWindowsHosts()
    {
        var draft = ValidDraft();
        draft.Tpm = true;

        Assert.Contains(nameof(VmDraft.Tpm), Validate(draft).Keys);
    }

    [Fact]
    public void SecureBootIsRejectedWhenItNeedsSmmThatWhpxLacks()
    {
        var draft = ValidDraft();
        draft.SecureBoot = true;

        Assert.Contains(nameof(VmDraft.SecureBoot), Validate(draft).Keys);
    }

    [Fact]
    public void TpmAndSecureBootAreAcceptedWithKvmAndSwtpm()
    {
        var draft = ValidDraft();
        draft.Tpm = true;
        draft.SecureBoot = true;

        Assert.Empty(VmValidator.Validate(draft, [], TestQemu.LinuxWithSwtpm(), MaxCores, MaxMemoryMiB));
    }

    [Fact]
    public void DuplicateHostPortsAreRejected()
    {
        var draft = ValidDraft();
        draft.PortForwards.Add(new PortForwardDraft { HostPort = 2222, GuestPort = 22 });
        draft.PortForwards.Add(new PortForwardDraft { HostPort = 2222, GuestPort = 80 });

        Assert.Equal([$"{nameof(VmDraft.PortForwards)}[1]"], Validate(draft).Keys);
    }

    [Fact]
    public void SameHostPortIsAllowedForTcpAndUdp()
    {
        var draft = ValidDraft();
        draft.PortForwards.Add(new PortForwardDraft { Protocol = PortProtocol.Tcp, HostPort = 5353, GuestPort = 53 });
        draft.PortForwards.Add(new PortForwardDraft { Protocol = PortProtocol.Udp, HostPort = 5353, GuestPort = 53 });

        Assert.Empty(Validate(draft));
    }

    [Fact]
    public void MissingQemuIsAGeneralError()
    {
        var errors = VmValidator.Validate(ValidDraft(), [], null, MaxCores, MaxMemoryMiB);

        Assert.Contains(VmValidator.General, errors.Keys);
    }

    [Fact]
    public void NewVmNeedsQemuImg()
    {
        var qemu = TestQemu.Create("whpx", tpmBackend: false, firmware: [TestQemu.Uefi("x86_64")], qemuImg: false);

        Assert.Contains(VmValidator.General, VmValidator.Validate(ValidDraft(), [], qemu, MaxCores, MaxMemoryMiB).Keys);
    }
}
