using System.Text.Json;
using Virtualized.Vms;

namespace Virtualized.Qemu;

public sealed record QemuCommand(string Executable, IReadOnlyList<string> Arguments, string Accelerator, FirmwareDescriptor? Firmware);

/// <summary>Where a VM's per-run files live; disk paths in the definition are relative to <see cref="VmDirectory"/>.</summary>
/// <param name="HostCpuVendor">CPUID vendor of this computer ("GenuineIntel", "AuthenticAMD"), used to pick a CPU model under WHPX.</param>
/// <param name="TpmSocketPath">Control socket of the swtpm started for this run, when the VM has a TPM.</param>
public sealed record QemuLaunchContext(
    string VmDirectory, int QmpPort, int VncWebSocketPort, string HostCpuVendor = "GenuineIntel", string? TpmSocketPath = null,
    bool StartPaused = false)
{
    public const string EfiVarsFileName = "efivars.qcow2";
    public const string UnattendFolderName = "unattend";

    public string EfiVarsPath => Path.Combine(VmDirectory, EfiVarsFileName);

    /// <summary>Folder holding autounattend.xml, presented to the guest as a USB drive.</summary>
    public string UnattendDirectory => Path.Combine(VmDirectory, UnattendFolderName);
}

/// <summary>
/// Turns a <see cref="VmDefinition"/> into a qemu-system command line. Pure: it doesn't touch the
/// file system or start anything, so every choice it makes can be unit tested.
/// </summary>
public static class QemuCommandBuilder
{
    /// <summary>qdev id of the CD drive, used by QMP to change or eject its medium.</summary>
    public const string CdromDeviceId = "cdrom0";

    /// <summary>
    /// Fixed block node names, so snapshot commands can name the images to include. They must differ
    /// from the drive ids ("disk0"), which QEMU keeps in the same namespace.
    /// </summary>
    public const string EfiVarsNodeName = "efivars";

    public static string DiskNodeName(int index) => $"fmt-disk{index}";

    public static QemuCommand Build(VmDefinition vm, QemuInstallation qemu, QemuLaunchContext context)
    {
        var binary = qemu.FindBinary(vm.Architecture)
                     ?? throw new QemuConfigurationException($"This QEMU installation has no qemu-system-{vm.Architecture}.");
        if (vm.Tpm && context.TpmSocketPath is null)
            throw new QemuConfigurationException("The VM has a TPM, but swtpm wasn't started for it.");

        var x86 = vm.Architecture == "x86_64";
        var accelerator = binary.BestAccelerator;
        var windowsGuest = vm.GuestOs is GuestOs.Windows10 or GuestOs.Windows11;
        var firmware = vm.Firmware == FirmwareType.Uefi ? SelectFirmware(vm, qemu) : null;
        var args = new List<string>();

        void Add(params string[] values) => args.AddRange(values);

        Add("-name", $"{_Escape(vm.Name)},process=sqemu-{vm.Id:N}");
        Add("-uuid", vm.Id.ToString());
        Add("-nodefaults", "-no-user-config");

        var machine = x86 ? "q35" : "virt";
        if (firmware is { RequiresSmm: true })
            machine += ",smm=on";
        Add("-machine", machine);
        Add("-accel", AcceleratorOption(accelerator));
        Add("-cpu", CpuModel(accelerator, context.HostCpuVendor));
        Add("-smp", vm.CpuCores.ToString());
        Add("-m", vm.MemoryMiB.ToString());

        if (firmware is not null)
        {
            if (firmware.RequiresSmm)
                Add("-global", "driver=cfi.pflash01,property=secure,value=on");
            Add("-drive", $"if=pflash,unit=0,readonly=on,format={firmware.CodeFormat},file={_Escape(firmware.CodePath)}");
            Add("-drive", $"if=pflash,unit=1,format=qcow2,file={_Escape(context.EfiVarsPath)},node-name={EfiVarsNodeName}");
        }

        // Windows keeps the hardware clock in local time; everything else expects UTC.
        Add("-rtc", windowsGuest ? "base=localtime" : "base=utc");

        // pvpanic lets the guest report a crash (Linux panic, Windows bugcheck). Pausing on it keeps
        // the crash screen visible in the console instead of the VM silently powering off.
        Add("-device", x86 ? "pvpanic" : "pvpanic-pci");
        Add("-action", "panic=pause");

        _AddStorage(vm, context, x86, Add);
        _AddNetwork(vm, x86, windowsGuest, Add);

        // Display goes out over VNC only; the websocket listener is what noVNC connects to.
        Add("-device", x86 ? "VGA" : "virtio-gpu-pci");
        // An absolute pointer keeps the VNC cursor and the guest cursor in sync.
        Add("-device", "qemu-xhci,id=usb", "-device", "usb-tablet,bus=usb.0");
        if (!x86)
            Add("-device", "usb-kbd,bus=usb.0");

        if (NeedsUnattendDrive(vm))
        {
            // vvfat shows a host folder to the guest as a FAT drive; on a removable USB stick, Windows
            // Setup finds autounattend.xml at its root. JSON keeps paths with commas or colons intact.
            Add("-blockdev", JsonSerializer.Serialize(new Dictionary<string, object>
            {
                ["driver"] = "vvfat",
                ["node-name"] = "unattend",
                ["dir"] = context.UnattendDirectory,
                ["read-only"] = true,
            }));
            Add("-device", "usb-storage,bus=usb.0,drive=unattend,removable=on");
        }

        if (vm.Tpm)
        {
            // swtpm emulates the TPM in its own process; QEMU talks to it over a Unix socket.
            Add("-chardev", $"socket,id=chrtpm,path={_Escape(context.TpmSocketPath!)}");
            Add("-tpmdev", "emulator,id=tpm0,chardev=chrtpm");
            Add("-device", x86 ? "tpm-crb,tpmdev=tpm0" : "tpm-tis-device,tpmdev=tpm0");
        }

        Add("-display", "none");
        // The raw VNC port isn't used by Virtualized; "to=" lets QEMU take any free display so VMs don't collide.
        Add("-vnc", $"127.0.0.1:0,to=99,websocket=127.0.0.1:{context.VncWebSocketPort}");
        Add("-qmp", $"tcp:127.0.0.1:{context.QmpPort},server=on,wait=off");

        // Starts with the CPUs stopped, e.g. to load a snapshot before the guest runs. (-loadvm can't be
        // used for that: it reads the saved memory from the first image, the UEFI variables, not the disk.)
        if (context.StartPaused)
            Add("-S");

        return new QemuCommand(binary.Path, args, accelerator, firmware);
    }

    /// <summary>The compatibility answer file only matters while Windows is being installed from an ISO.</summary>
    public static bool NeedsUnattendDrive(VmDefinition vm) =>
        vm.Windows11Compatibility && vm.InstallMedia is not null;

    /// <summary>
    /// With WHPX's in-kernel interrupt controller, resetting a VM with more than one vCPU (the reset
    /// button, or Ctrl+Alt+Del in the guest) fails with "WHPX: Unexpected VP exit code 4" and the VM
    /// freezes (seen with QEMU 11.1). Emulating the APIC in QEMU avoids it, at some cost for guests
    /// with a lot of interrupt traffic.
    /// </summary>
    public static string AcceleratorOption(string accelerator) =>
        accelerator == "whpx" ? "whpx,kernel-irqchip=off" : accelerator;

    /// <summary>
    /// "host" passes the real CPU through under KVM and HVF. Under WHPX both "host" and "max" hang OVMF
    /// before it draws anything (seen with QEMU 11.1), so a named model of the host's vendor is used;
    /// QEMU drops any of its features the real CPU lacks. Both models include the SSE4.2 and POPCNT
    /// that Windows 11 24H2 requires, which the default qemu64 model doesn't.
    /// </summary>
    public static string CpuModel(string accelerator, string hostCpuVendor) => accelerator switch
    {
        "tcg" => "max",
        "whpx" => hostCpuVendor == "AuthenticAMD" ? "EPYC-v3" : "Skylake-Client-v4",
        _ => "host",
    };

    /// <summary>
    /// Picks the UEFI build for the VM's architecture, with Secure Boot only when the VM asks for it:
    /// the Secure Boot builds need SMM, which not every accelerator can provide. With Secure Boot, a build
    /// whose variable store already has Microsoft's keys enrolled is preferred, so Secure Boot is actually
    /// on from the first boot (an empty store leaves it in setup mode).
    /// </summary>
    public static FirmwareDescriptor SelectFirmware(VmDefinition vm, QemuInstallation qemu)
    {
        var candidates = qemu.Firmware.Where(f => f.Architecture == vm.Architecture && f.VarsTemplatePath is not null).ToList();
        var firmware = vm.SecureBoot
            ? candidates.FirstOrDefault(f => f.SecureBoot && f.HasEnrolledKeys) ?? candidates.FirstOrDefault(f => f.SecureBoot)
            : candidates.FirstOrDefault(f => !f.SecureBoot && !f.RequiresSmm);

        return firmware ?? throw new QemuConfigurationException(vm.SecureBoot
            ? $"No UEFI firmware with Secure Boot was found for {vm.Architecture}."
            : $"No UEFI firmware was found for {vm.Architecture}.");
    }

    private static void _AddStorage(VmDefinition vm, QemuLaunchContext context, bool x86, Action<string[]> add)
    {
        // Windows installers have no virtio drivers, so Windows (and unknown) guests on x86 get SATA disks on
        // the q35 AHCI controller; Linux gets virtio. The virt machine on aarch64 has no AHCI, so it's virtio there.
        var virtio = !x86 || vm.GuestOs == GuestOs.Linux;
        var hasMedia = vm.InstallMedia is not null;
        if (!x86)
            add(["-device", "virtio-scsi-pci,id=scsi0"]);

        for (var i = 0; i < vm.Disks.Count; i++)
        {
            var disk = vm.Disks[i];
            var path = Path.Combine(context.VmDirectory, disk.File);
            add(["-drive", $"if=none,id=disk{i},format=qcow2,discard=unmap,file={_Escape(path)},node-name={DiskNodeName(i)}"]);

            // With install media attached, the CD boots first and the disk is the fallback.
            var bootIndex = i == 0 ? $",bootindex={(hasMedia ? 1 : 0)}" : "";
            add(["-device", virtio
                ? $"virtio-blk-pci,drive=disk{i}{bootIndex}"
                : $"ide-hd,drive=disk{i},bus=ide.{i}{bootIndex}"]);
        }

        // The CD drive is always present so an ISO can be inserted while the VM runs; QMP finds it by its id.
        var cd = "if=none,id=cd0,media=cdrom,readonly=on";
        if (hasMedia)
            cd += $",file={_Escape(vm.InstallMedia!)}";
        add(["-drive", cd]);
        var cdBoot = hasMedia ? ",bootindex=0" : "";
        add(["-device", x86
            ? $"ide-cd,drive=cd0,id={CdromDeviceId},bus=ide.{vm.Disks.Count}{cdBoot}"
            : $"scsi-cd,drive=cd0,id={CdromDeviceId},bus=scsi0.0{cdBoot}"]);
    }

    private static void _AddNetwork(VmDefinition vm, bool x86, bool windowsGuest, Action<string[]> add)
    {
        if (!vm.NetworkEnabled)
            return;

        // Forwards listen on the host's loopback only, so services in the VM aren't exposed to the network.
        var netdev = "user,id=net0" + string.Concat(vm.PortForwards.Select(p =>
            $",hostfwd={p.Protocol.ToString().ToLowerInvariant()}:127.0.0.1:{p.HostPort}-:{p.GuestPort}"));
        add(["-netdev", netdev]);

        // Windows ships an e1000e driver but no virtio one.
        var nic = !x86 || vm.GuestOs == GuestOs.Linux ? "virtio-net-pci"
            : windowsGuest ? "e1000e"
            : "e1000";
        add(["-device", $"{nic},netdev=net0"]);
    }

    /// <summary>QEMU option values are comma-separated, so a literal comma in a path is written as ",,".</summary>
    private static string _Escape(string value) => value.Replace(",", ",,");
}

public sealed class QemuConfigurationException(string message) : Exception(message);
