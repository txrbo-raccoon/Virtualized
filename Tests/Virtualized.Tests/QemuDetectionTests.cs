using Virtualized.Qemu;

namespace Virtualized.Tests;

public sealed class QemuDetectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqemu-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Theory]
    [InlineData("QEMU emulator version 11.1.0 (v11.1.0-12130-ge470268ff4)\nCopyright (c) 2003-2026", "11.1.0")]
    [InlineData("QEMU emulator version 8.2.2 (Debian 1:8.2.2+ds-0ubuntu1)", "8.2.2")]
    [InlineData("QEMU emulator version 9.0", "9.0")]
    public void ParsesVersion(string output, string expected)
    {
        Assert.Equal(Version.Parse(expected), QemuLocator.ParseVersion(output));
    }

    [Fact]
    public void UnknownVersionOutputGivesNull()
    {
        Assert.Null(QemuLocator.ParseVersion("something else"));
    }

    [Fact]
    public void ParsesAcceleratorHelp()
    {
        var output = "Accelerators supported in QEMU binary:\r\ntcg\r\nwhpx\r\n";

        Assert.Equal(["tcg", "whpx"], QemuLocator.ParseAcceleratorHelp(output));
    }

    [Fact]
    public void BestAcceleratorPrefersHardware()
    {
        var binary = new QemuSystemBinary("x86_64", "q", [new AcceleratorInfo("tcg", true), new AcceleratorInfo("whpx", true)]);

        Assert.Equal("whpx", binary.BestAccelerator);
        Assert.True(binary.HasHardwareAcceleration);
    }

    [Fact]
    public void UnusableAcceleratorFallsBackToTcg()
    {
        var binary = new QemuSystemBinary("x86_64", "q", [new AcceleratorInfo("tcg", true), new AcceleratorInfo("kvm", false)]);

        Assert.Equal("tcg", binary.BestAccelerator);
    }

    [Fact]
    public void SecureBootNeedingSmmIsSoftwareOnlyWithWhpx()
    {
        Assert.Equal(SecureBootSupport.SoftwareEmulationOnly, TestQemu.Windows().GetSecureBootSupport("x86_64"));
        Assert.Equal(SecureBootSupport.NoFirmware, TestQemu.Windows().GetSecureBootSupport("aarch64"));
        Assert.Equal(SecureBootSupport.Available, TestQemu.LinuxWithSwtpm().GetSecureBootSupport("x86_64"));
    }

    [Fact]
    public void FirmwareWithBuildTimePathsIsResolvedNextToInstallation()
    {
        // Windows installer layout: descriptors in share\firmware pointing at /usr/local/share/..., images in share\.
        var share = Path.Combine(_root, "share");
        Directory.CreateDirectory(Path.Combine(share, "firmware"));
        File.WriteAllText(Path.Combine(share, "edk2-x86_64-code.fd"), "");
        File.WriteAllText(Path.Combine(share, "edk2-i386-vars.fd"), "");
        File.WriteAllText(Path.Combine(share, "firmware", "60-edk2-x86_64.json"), """
            {
                "description": "UEFI firmware for x86_64",
                "interface-types": ["uefi"],
                "mapping": {
                    "device": "flash",
                    "executable": { "filename": "/usr/local/share//edk2-x86_64-code.fd", "format": "raw" },
                    "nvram-template": { "filename": "/usr/local/share//edk2-i386-vars.fd", "format": "raw" }
                },
                "targets": [{ "architecture": "x86_64", "machines": ["pc-q35-*"] }],
                "features": ["acpi-s3"]
            }
            """);
        File.WriteAllText(Path.Combine(share, "firmware", "60-edk2-riscv64.json"), """
            {
                "interface-types": ["uefi"],
                "mapping": { "device": "flash", "executable": { "filename": "/nowhere/riscv.fd", "format": "raw" } },
                "targets": [{ "architecture": "riscv64", "machines": ["virt-*"] }]
            }
            """);
        File.WriteAllText(Path.Combine(share, "firmware", "50-empty.json"), "");

        var firmware = Assert.Single(FirmwareCatalog.Load(_root, QemuLocator.Architectures));

        Assert.Equal("x86_64", firmware.Architecture);
        Assert.Equal(Path.Combine(share, "edk2-x86_64-code.fd"), firmware.CodePath);
        Assert.Equal(Path.Combine(share, "edk2-i386-vars.fd"), firmware.VarsTemplatePath);
        Assert.False(firmware.SecureBoot);
    }
}
