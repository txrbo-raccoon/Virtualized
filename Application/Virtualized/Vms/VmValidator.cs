using Virtualized.Qemu;

namespace Virtualized.Vms;

/// <summary>Field-level validation for the VM editor. Errors are keyed by <see cref="VmDraft"/> property name.</summary>
public static class VmValidator
{
    public const int MinMemoryMiB = 256;
    public const int MaxNameLength = 64;
    public const int MaxDiskSizeGiB = 4096;

    /// <summary>Key for errors that don't belong to a single field.</summary>
    public const string General = "";

    public static Dictionary<string, string> Validate(
        VmDraft draft, IEnumerable<VmDefinition> existing, QemuInstallation? qemu, int maxCores, int maxMemoryMiB)
    {
        var errors = new Dictionary<string, string>();

        var name = draft.Name.Trim();
        if (name.Length == 0)
            errors[nameof(draft.Name)] = "Give the VM a name.";
        else if (name.Length > MaxNameLength)
            errors[nameof(draft.Name)] = $"Use at most {MaxNameLength} characters.";
        else if (existing.Any(vm => vm.Id != draft.Id && string.Equals(vm.Name, name, StringComparison.OrdinalIgnoreCase)))
            errors[nameof(draft.Name)] = "Another VM already has this name.";

        if (draft.CpuCores < 1 || draft.CpuCores > maxCores)
            errors[nameof(draft.CpuCores)] = $"Choose between 1 and {maxCores} cores.";

        if (draft.MemoryMiB < MinMemoryMiB || draft.MemoryMiB > maxMemoryMiB)
            errors[nameof(draft.MemoryMiB)] = $"Choose between {MinMemoryMiB} MiB and {maxMemoryMiB} MiB.";

        if (draft.IsNew && (draft.DiskSizeGiB < 1 || draft.DiskSizeGiB > MaxDiskSizeGiB))
            errors[nameof(draft.DiskSizeGiB)] = $"Choose between 1 and {MaxDiskSizeGiB} GiB.";

        if (!string.IsNullOrWhiteSpace(draft.InstallMedia) && !File.Exists(draft.InstallMedia.Trim()))
            errors[nameof(draft.InstallMedia)] = "This file doesn't exist.";

        _ValidatePortForwards(draft, errors);

        if (qemu is null)
        {
            errors[General] = "QEMU wasn't found. Set it up in Settings first.";
            return errors;
        }

        if (draft.IsNew && qemu.QemuImgPath is null)
            errors[General] = "qemu-img wasn't found next to QEMU, so the VM's disk can't be created.";

        if (qemu.FindBinary(draft.Architecture) is null)
            errors[nameof(draft.Architecture)] = $"This QEMU installation has no qemu-system-{draft.Architecture}.";

        if (draft.Firmware == FirmwareType.Bios && draft.Architecture == "aarch64")
            errors[nameof(draft.Firmware)] = "aarch64 VMs need UEFI firmware.";
        else if (draft.Firmware == FirmwareType.Uefi && !qemu.Firmware.Any(f => f.Architecture == draft.Architecture))
            errors[nameof(draft.Firmware)] = $"No UEFI firmware for {draft.Architecture} was found.";

        if (draft.SecureBoot && draft.Firmware == FirmwareType.Uefi
                             && qemu.GetSecureBootSupport(draft.Architecture) != SecureBootSupport.Available)
            errors[nameof(draft.SecureBoot)] = "Secure Boot isn't available with hardware acceleration on this computer.";

        if (draft.Tpm && !qemu.TpmAvailable)
            errors[nameof(draft.Tpm)] = "A TPM isn't available on this computer.";

        return errors;
    }

    private static void _ValidatePortForwards(VmDraft draft, Dictionary<string, string> errors)
    {
        if (!draft.NetworkEnabled)
            return;

        for (var i = 0; i < draft.PortForwards.Count; i++)
        {
            var forward = draft.PortForwards[i];
            if (forward.HostPort is < 1 or > 65535 || forward.GuestPort is < 1 or > 65535)
            {
                errors[$"{nameof(draft.PortForwards)}[{i}]"] = "Ports go from 1 to 65535.";
            }
            else if (draft.PortForwards.Take(i).Any(other => other.Protocol == forward.Protocol && other.HostPort == forward.HostPort))
            {
                errors[$"{nameof(draft.PortForwards)}[{i}]"] = $"Host port {forward.HostPort}/{forward.Protocol.ToString().ToLowerInvariant()} is already forwarded.";
            }
        }
    }
}
