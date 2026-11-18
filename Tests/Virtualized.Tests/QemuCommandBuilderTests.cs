using Virtualized.Qemu;
using Virtualized.Vms;

namespace Virtualized.Tests;

public class QemuCommandBuilderTests
{
    private static readonly QemuLaunchContext Context = new(Path.Combine("vms", "abc"), 4444, 5700);

    private static VmDefinition Vm(GuestOs os = GuestOs.Linux) => new()
    {
        Id = Guid.Parse("11111111-2222-3333-4444-555555555555"),
        Name = "Debian",
        GuestOs = os,
        Architecture = "x86_64",
        CpuCores = 2,
        MemoryMiB = 2048,
        Disks = [new VmDisk("disk0.qcow2", 32)],
        Firmware = FirmwareType.Uefi,
    };

    private static QemuCommand Build(VmDefinition vm, QemuInstallation? qemu = null) =>
        QemuCommandBuilder.Build(vm, qemu ?? TestQemu.Windows(), Context);

    /// <summary>The value following each occurrence of an option, e.g. every "-device".</summary>
    private static List<string> Values(QemuCommand command, string option) =>
        command.Arguments.Select((arg, i) => (arg, i))
            .Where(p => p.arg == option && p.i + 1 < command.Arguments.Count)
            .Select(p => command.Arguments[p.i + 1])
            .ToList();

    private static string Value(QemuCommand command, string option) => Assert.Single(Values(command, option));

    [Fact]
    public void UsesTheArchitecturesBinaryAndBestAccelerator()
    {
        var command = Build(Vm());

        Assert.Equal("qemu-system-x86_64", command.Executable);
        Assert.Equal("whpx", command.Accelerator);
        Assert.Equal("whpx,kernel-irqchip=off", Value(command, "-accel"));
        Assert.Equal("Skylake-Client-v4", Value(command, "-cpu"));
        Assert.Equal("q35", Value(command, "-machine"));
        Assert.Equal("2", Value(command, "-smp"));
        Assert.Equal("2048", Value(command, "-m"));
    }

    [Theory]
    [InlineData("whpx", "GenuineIntel", "Skylake-Client-v4")]
    [InlineData("whpx", "AuthenticAMD", "EPYC-v3")]
    [InlineData("kvm", "AuthenticAMD", "host")]
    [InlineData("hvf", "", "host")]
    [InlineData("tcg", "GenuineIntel", "max")]
    public void CpuModelDependsOnAcceleratorAndVendor(string accelerator, string vendor, string expected)
    {
        Assert.Equal(expected, QemuCommandBuilder.CpuModel(accelerator, vendor));
    }

    [Fact]
    public void SoftwareEmulationUsesMaxCpuModel()
    {
        var command = Build(Vm() with { Architecture = "aarch64" });

        Assert.Equal("qemu-system-aarch64", command.Executable);
        Assert.Equal("tcg", Value(command, "-accel"));
        Assert.Equal("max", Value(command, "-cpu"));
        Assert.Equal("virt", Value(command, "-machine"));
    }

    [Fact]
    public void ControlChannelsListenOnLoopbackOnly()
    {
        var command = Build(Vm());

        Assert.Equal("tcp:127.0.0.1:4444,server=on,wait=off", Value(command, "-qmp"));
        Assert.Equal("127.0.0.1:0,to=99,websocket=127.0.0.1:5700", Value(command, "-vnc"));
        Assert.Equal("none", Value(command, "-display"));
        Assert.Contains("-nodefaults", command.Arguments);
    }

    [Fact]
    public void UefiUsesReadOnlyCodeAndPerVmQcow2Vars()
    {
        var command = Build(Vm());
        var pflash = Values(command, "-drive").Where(d => d.StartsWith("if=pflash")).ToList();

        Assert.Equal(2, pflash.Count);
        Assert.Contains("readonly=on", pflash[0]);
        Assert.Contains("file=code.fd", pflash[0]);
        Assert.Equal($"if=pflash,unit=1,format=qcow2,file={Context.EfiVarsPath},node-name=efivars", pflash[1]);
        Assert.False(command.Firmware!.SecureBoot);
    }

    [Fact]
    public void BiosHasNoPflash()
    {
        var command = Build(Vm() with { Firmware = FirmwareType.Bios });

        Assert.DoesNotContain(Values(command, "-drive"), d => d.StartsWith("if=pflash"));
        Assert.Null(command.Firmware);
    }

    [Fact]
    public void SecureBootEnablesSmmAndSecurePflash()
    {
        var command = Build(Vm() with { SecureBoot = true }, TestQemu.LinuxWithSwtpm());

        Assert.Equal("q35,smm=on", Value(command, "-machine"));
        Assert.Contains("driver=cfi.pflash01,property=secure,value=on", Values(command, "-global"));
        Assert.True(command.Firmware!.SecureBoot);
    }

    [Fact]
    public void LinuxGetsVirtioDiskAndNic()
    {
        var devices = Values(Build(Vm(GuestOs.Linux)), "-device");

        Assert.Contains("virtio-blk-pci,drive=disk0,bootindex=0", devices);
        Assert.Contains("virtio-net-pci,netdev=net0", devices);
    }

    [Fact]
    public void WindowsGetsSataDiskAndE1000eBecauseItHasNoVirtioDrivers()
    {
        var command = Build(Vm(GuestOs.Windows11));
        var devices = Values(command, "-device");

        Assert.Contains("ide-hd,drive=disk0,bus=ide.0,bootindex=0", devices);
        Assert.Contains("e1000e,netdev=net0", devices);
        Assert.Equal("base=localtime", Value(command, "-rtc"));
    }

    [Fact]
    public void InstallMediaBootsBeforeTheDisk()
    {
        var command = Build(Vm(GuestOs.Windows11) with { InstallMedia = @"C:\isos\win11.iso" });
        var devices = Values(command, "-device");

        Assert.Contains(@"if=none,id=cd0,media=cdrom,readonly=on,file=C:\isos\win11.iso", Values(command, "-drive"));
        Assert.Contains("ide-cd,drive=cd0,id=cdrom0,bus=ide.1,bootindex=0", devices);
        Assert.Contains("ide-hd,drive=disk0,bus=ide.0,bootindex=1", devices);
    }

    [Fact]
    public void EmptyCdDriveIsAlwaysPresent()
    {
        var command = Build(Vm(GuestOs.Linux));

        Assert.Contains("if=none,id=cd0,media=cdrom,readonly=on", Values(command, "-drive"));
        Assert.Contains("ide-cd,drive=cd0,id=cdrom0,bus=ide.1", Values(command, "-device"));
    }

    [Fact]
    public void PortForwardsBindToLoopback()
    {
        var vm = Vm() with { PortForwards = [new PortForward(PortProtocol.Tcp, 2222, 22), new PortForward(PortProtocol.Udp, 5353, 53)] };

        Assert.Equal("user,id=net0,hostfwd=tcp:127.0.0.1:2222-:22,hostfwd=udp:127.0.0.1:5353-:53", Value(Build(vm), "-netdev"));
    }

    [Fact]
    public void DisabledNetworkHasNoNic()
    {
        var command = Build(Vm() with { NetworkEnabled = false });

        Assert.Empty(Values(command, "-netdev"));
        Assert.DoesNotContain(Values(command, "-device"), d => d.Contains("netdev="));
    }

    [Fact]
    public void CommasInPathsAndNamesAreEscaped()
    {
        var command = Build(Vm() with { Name = "a,b", InstallMedia = @"C:\isos\x,y.iso" });

        Assert.StartsWith("a,,b,process=sqemu-", Value(command, "-name"));
        Assert.Contains(Values(command, "-drive"), d => d.EndsWith(@"file=C:\isos\x,,y.iso"));
    }

    [Fact]
    public void Aarch64UsesVirtioScsiCdAndUsbKeyboard()
    {
        var devices = Values(Build(Vm(GuestOs.Windows11) with { Architecture = "aarch64" }), "-device");

        Assert.Contains("virtio-scsi-pci,id=scsi0", devices);
        Assert.Contains("scsi-cd,drive=cd0,id=cdrom0,bus=scsi0.0", devices);
        Assert.Contains("virtio-blk-pci,drive=disk0,bootindex=0", devices);
        Assert.Contains("usb-kbd,bus=usb.0", devices);
        Assert.Contains("virtio-gpu-pci", devices);
    }

    [Fact]
    public void DisksAndUefiVarsHaveFixedNodeNamesForSnapshots()
    {
        var drives = Values(Build(Vm()), "-drive");

        Assert.Contains(drives, d => d.StartsWith("if=none,id=disk0,") && d.EndsWith(",node-name=fmt-disk0"));
        Assert.Contains(drives, d => d.StartsWith("if=pflash,unit=1,") && d.EndsWith(",node-name=efivars"));
    }

    [Fact]
    public void StartsPausedOnlyWhenAsked()
    {
        Assert.DoesNotContain("-S", Build(Vm()).Arguments);
        Assert.Contains("-S", QemuCommandBuilder.Build(Vm(), TestQemu.Windows(), Context with { StartPaused = true }).Arguments);
    }

    [Fact]
    public void GuestCrashesPauseTheVmThroughPvpanic()
    {
        var x86 = Build(Vm());
        var arm = Build(Vm() with { Architecture = "aarch64" });

        Assert.Contains("pvpanic", Values(x86, "-device"));
        Assert.Contains("pvpanic-pci", Values(arm, "-device"));
        Assert.Equal("panic=pause", Value(x86, "-action"));
    }

    [Fact]
    public void SecureBootWithoutSecureFirmwareFails()
    {
        var qemu = TestQemu.Create("kvm", tpmBackend: false, firmware: [TestQemu.Uefi("x86_64")]);

        Assert.Throws<QemuConfigurationException>(() => Build(Vm() with { SecureBoot = true }, qemu));
    }

    [Fact]
    public void TpmWithoutARunningSwtpmFails()
    {
        Assert.Throws<QemuConfigurationException>(() => Build(Vm() with { Tpm = true }, TestQemu.LinuxWithSwtpm()));
    }

    [Theory]
    [InlineData("x86_64", "tpm-crb,tpmdev=tpm0")]
    [InlineData("aarch64", "tpm-tis-device,tpmdev=tpm0")]
    public void TpmConnectsToSwtpmsSocket(string architecture, string device)
    {
        var context = Context with { TpmSocketPath = "/vms/abc/swtpm.sock" };
        var command = QemuCommandBuilder.Build(Vm() with { Tpm = true, Architecture = architecture }, TestQemu.LinuxWithSwtpm(), context);

        Assert.Equal("socket,id=chrtpm,path=/vms/abc/swtpm.sock", Value(command, "-chardev"));
        Assert.Equal("emulator,id=tpm0,chardev=chrtpm", Value(command, "-tpmdev"));
        Assert.Contains(device, Values(command, "-device"));
    }

    [Fact]
    public void NoTpmNoTpmDevices()
    {
        var command = Build(Vm());

        Assert.Empty(Values(command, "-tpmdev"));
        Assert.Empty(Values(command, "-chardev"));
    }

    [Fact]
    public void Windows11CompatibilityAddsTheAnswerFileDriveWhileInstalling()
    {
        var vm = Vm(GuestOs.Windows11) with { Windows11Compatibility = true, InstallMedia = @"C:\isos\win11.iso" };
        var command = Build(vm);

        var blockdev = System.Text.Json.Nodes.JsonNode.Parse(Value(command, "-blockdev"))!;
        Assert.Equal("vvfat", blockdev["driver"]!.GetValue<string>());
        Assert.Equal("unattend", blockdev["node-name"]!.GetValue<string>());
        Assert.Equal(Context.UnattendDirectory, blockdev["dir"]!.GetValue<string>());
        Assert.True(blockdev["read-only"]!.GetValue<bool>());
        Assert.Contains("usb-storage,bus=usb.0,drive=unattend,removable=on", Values(command, "-device"));
    }

    [Fact]
    public void NoAnswerFileDriveOnceTheIsoIsEjected()
    {
        var command = Build(Vm(GuestOs.Windows11) with { Windows11Compatibility = true, InstallMedia = null });

        Assert.Empty(Values(command, "-blockdev"));
    }

    [Fact]
    public void NoAnswerFileDriveWithoutCompatibility()
    {
        var command = Build(Vm(GuestOs.Windows11) with { InstallMedia = @"C:\isos\win11.iso" });

        Assert.Empty(Values(command, "-blockdev"));
    }

    [Fact]
    public void SecureBootPrefersFirmwareWithEnrolledKeys()
    {
        var enrolled = TestQemu.Uefi("x86_64", secureBoot: true) with
        {
            Name = "40-ovmf-ms.json",
            VarsTemplatePath = "vars-ms.fd",
            Features = ["secure-boot", "requires-smm", "enrolled-keys"],
        };
        var qemu = TestQemu.Create("kvm", tpmBackend: true, firmware: [TestQemu.Uefi("x86_64", secureBoot: true), enrolled]);

        Assert.Equal("vars-ms.fd", QemuCommandBuilder.SelectFirmware(Vm() with { SecureBoot = true }, qemu).VarsTemplatePath);
    }
}
