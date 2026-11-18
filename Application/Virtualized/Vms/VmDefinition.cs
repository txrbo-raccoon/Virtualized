namespace Virtualized.Vms;

public enum GuestOs
{
    Linux,
    Windows10,
    Windows11,
    Other,
}

public enum FirmwareType
{
    Bios,
    Uefi,
}

public enum PortProtocol
{
    Tcp,
    Udp,
}

/// <summary>Forwards a port on the host's loopback interface (127.0.0.1) to the guest through QEMU's user-mode (NAT) network.</summary>
public sealed record PortForward(PortProtocol Protocol, int HostPort, int GuestPort);

/// <param name="File">Image file name, relative to the VM's folder.</param>
/// <param name="SizeGiB">Virtual size the image was created with.</param>
public sealed record VmDisk(string File, int SizeGiB);

/// <summary>A virtual machine as stored in its vm.json. Runtime state (PID, ports) lives separately in run.json.</summary>
public sealed record VmDefinition
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public Guid Id { get; init; } = Guid.NewGuid();

    public required string Name { get; init; }

    public GuestOs GuestOs { get; init; }

    /// <summary>QEMU target architecture, e.g. x86_64 or aarch64. Fixed once the VM is created.</summary>
    public string Architecture { get; init; } = "x86_64";

    public int CpuCores { get; init; }

    public int MemoryMiB { get; init; }

    public IReadOnlyList<VmDisk> Disks { get; init; } = [];

    /// <summary>ISO image attached to the virtual CD drive, if any.</summary>
    public string? InstallMedia { get; init; }

    public FirmwareType Firmware { get; init; }

    public bool SecureBoot { get; init; }

    public bool Tpm { get; init; }

    /// <summary>Lets the Windows 11 installer skip its TPM and Secure Boot checks on hosts that can't provide them.</summary>
    public bool Windows11Compatibility { get; init; }

    public bool NetworkEnabled { get; init; } = true;

    public IReadOnlyList<PortForward> PortForwards { get; init; } = [];

    public DateTime CreatedUtc { get; init; } = DateTime.UtcNow;
}
