namespace Virtualized.Qemu;

public sealed record AcceleratorInfo(string Name, bool Usable);

public sealed record QemuSystemBinary(string Architecture, string Path, IReadOnlyList<AcceleratorInfo> Accelerators)
{
    /// <summary>The fastest usable accelerator; TCG (software emulation) is always available as the fallback.</summary>
    public string BestAccelerator => AcceleratorProbe.Preference
        .First(name => name == "tcg" || Accelerators.Any(a => a.Name == name && a.Usable));

    public bool HasHardwareAcceleration => BestAccelerator != "tcg";
}

public sealed record QemuInstallation(
    string Directory,
    Version Version,
    IReadOnlyList<QemuSystemBinary> Binaries,
    string? QemuImgPath,
    IReadOnlyList<FirmwareDescriptor> Firmware,
    bool TpmBackendAvailable,
    string? SwtpmPath)
{
    public static readonly Version MinimumVersion = new(8, 0);

    public bool IsVersionSupported => Version >= MinimumVersion;

    /// <summary>QEMU only builds its TPM backends on POSIX hosts, and the emulator backend also needs swtpm.</summary>
    public bool TpmAvailable => TpmBackendAvailable && SwtpmPath is not null;

    public QemuSystemBinary? FindBinary(string architecture) =>
        Binaries.FirstOrDefault(binary => binary.Architecture == architecture);

    public SecureBootSupport GetSecureBootSupport(string architecture)
    {
        var binary = FindBinary(architecture);
        var secureFirmware = Firmware.Where(f => f.Architecture == architecture && f.SecureBoot).ToList();
        if (binary is null || secureFirmware.Count == 0)
            return SecureBootSupport.NoFirmware;

        if (secureFirmware.Any(f => !f.RequiresSmm || AcceleratorProbe.SupportsSmm(binary.BestAccelerator)))
            return SecureBootSupport.Available;

        // The only Secure Boot builds need SMM, which the hardware accelerator here (WHPX, HVF) lacks.
        return SecureBootSupport.SoftwareEmulationOnly;
    }
}

public enum SecureBootSupport
{
    Available,
    SoftwareEmulationOnly,
    NoFirmware,
}

/// <summary>Outcome of looking for QEMU: the installation, if one was found, and where it was looked for.</summary>
public sealed record QemuDetection(QemuInstallation? Installation, IReadOnlyList<string> SearchedDirectories, string? Error = null);
