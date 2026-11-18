using Virtualized.Qemu;

namespace Virtualized.Vms;

public sealed class PortForwardDraft
{
    public PortProtocol Protocol { get; set; } = PortProtocol.Tcp;

    public int HostPort { get; set; }

    public int GuestPort { get; set; }
}

/// <summary>Mutable form model behind the VM editor; converted to and from the immutable <see cref="VmDefinition"/>.</summary>
public sealed class VmDraft
{
    public Guid? Id { get; init; }

    public bool IsNew => Id is null;

    public string Name { get; set; } = "";

    public GuestOs GuestOs { get; set; } = GuestOs.Linux;

    public string Architecture { get; set; } = QemuLocator.HostArchitecture;

    public int CpuCores { get; set; } = 2;

    public int MemoryMiB { get; set; } = 2048;

    /// <summary>Size of the disk created with a new VM. Existing disks are not resized from the editor.</summary>
    public int DiskSizeGiB { get; set; } = 32;

    public string? InstallMedia { get; set; }

    public FirmwareType Firmware { get; set; } = FirmwareType.Uefi;

    public bool SecureBoot { get; set; }

    public bool Tpm { get; set; }

    public bool Windows11Compatibility { get; set; }

    public bool NetworkEnabled { get; set; } = true;

    public List<PortForwardDraft> PortForwards { get; set; } = [];

    /// <summary>Settings of the VM being edited, kept so fields the editor doesn't show survive a save.</summary>
    private VmDefinition? Original { get; init; }

    public static VmDraft FromDefinition(VmDefinition vm) => new()
    {
        Id = vm.Id,
        Original = vm,
        Name = vm.Name,
        GuestOs = vm.GuestOs,
        Architecture = vm.Architecture,
        CpuCores = vm.CpuCores,
        MemoryMiB = vm.MemoryMiB,
        DiskSizeGiB = vm.Disks.FirstOrDefault()?.SizeGiB ?? 0,
        InstallMedia = vm.InstallMedia,
        Firmware = vm.Firmware,
        SecureBoot = vm.SecureBoot,
        Tpm = vm.Tpm,
        Windows11Compatibility = vm.Windows11Compatibility,
        NetworkEnabled = vm.NetworkEnabled,
        PortForwards = vm.PortForwards
            .Select(p => new PortForwardDraft { Protocol = p.Protocol, HostPort = p.HostPort, GuestPort = p.GuestPort })
            .ToList(),
    };

    public VmDefinition ToDefinition()
    {
        var uefi = Firmware == FirmwareType.Uefi;
        var definition = Original ?? new VmDefinition
        {
            Name = "",
            Disks = [new VmDisk("disk0.qcow2", DiskSizeGiB)],
        };

        return definition with
        {
            Name = Name.Trim(),
            GuestOs = GuestOs,
            Architecture = Architecture,
            CpuCores = CpuCores,
            MemoryMiB = MemoryMiB,
            InstallMedia = string.IsNullOrWhiteSpace(InstallMedia) ? null : InstallMedia.Trim(),
            Firmware = Firmware,
            SecureBoot = uefi && SecureBoot,
            Tpm = Tpm,
            Windows11Compatibility = GuestOs == GuestOs.Windows11 && Windows11Compatibility,
            NetworkEnabled = NetworkEnabled,
            PortForwards = NetworkEnabled
                ? PortForwards.Select(p => new PortForward(p.Protocol, p.HostPort, p.GuestPort)).ToList()
                : [],
        };
    }

    /// <summary>
    /// Fills in sensible defaults for a guest OS, turning on TPM and Secure Boot only where this host
    /// can provide them and falling back to the Windows 11 compatibility option where it can't.
    /// </summary>
    public void ApplyPreset(GuestOs os, QemuInstallation? qemu)
    {
        GuestOs = os;
        (CpuCores, MemoryMiB, DiskSizeGiB, Firmware) = os switch
        {
            GuestOs.Windows11 => (4, 4096, 64, FirmwareType.Uefi),
            GuestOs.Windows10 => (2, 4096, 64, FirmwareType.Uefi),
            GuestOs.Linux => (2, 2048, 32, FirmwareType.Uefi),
            _ => (1, 1024, 16, FirmwareType.Bios),
        };

        if (Architecture == "aarch64")
            Firmware = FirmwareType.Uefi;

        CpuCores = Math.Min(CpuCores, HostInfo.ProcessorCount);
        MemoryMiB = Math.Min(MemoryMiB, HostInfo.MaxGuestMemoryMiB);

        var wantsSecurity = os == GuestOs.Windows11;
        var secureBootAvailable = qemu?.GetSecureBootSupport(Architecture) == SecureBootSupport.Available;
        SecureBoot = wantsSecurity && secureBootAvailable;
        Tpm = wantsSecurity && qemu?.TpmAvailable == true;
        Windows11Compatibility = wantsSecurity && (!Tpm || !SecureBoot);
    }
}
