using Virtualized.Qemu;

namespace Virtualized.Tests;

/// <summary>Builds <see cref="QemuInstallation"/> values for tests without running QEMU.</summary>
internal static class TestQemu
{
    public static QemuInstallation Windows() => Create(
        accelerator: "whpx",
        tpmBackend: false,
        firmware:
        [
            Uefi("x86_64"),
            Uefi("x86_64", secureBoot: true),
            Uefi("aarch64"),
        ]);

    public static QemuInstallation LinuxWithSwtpm() => Create(
        accelerator: "kvm",
        tpmBackend: true,
        swtpm: "/usr/bin/swtpm",
        firmware: [Uefi("x86_64"), Uefi("x86_64", secureBoot: true), Uefi("aarch64")]);

    public static QemuInstallation Create(
        string accelerator, bool tpmBackend, IReadOnlyList<FirmwareDescriptor> firmware, string? swtpm = null, bool qemuImg = true) =>
        new(
            "qemu",
            new Version(11, 1, 0),
            [
                new QemuSystemBinary("x86_64", "qemu-system-x86_64", [new AcceleratorInfo("tcg", true), new AcceleratorInfo(accelerator, true)]),
                new QemuSystemBinary("aarch64", "qemu-system-aarch64", [new AcceleratorInfo("tcg", true)]),
            ],
            qemuImg ? "qemu-img" : null,
            firmware,
            tpmBackend,
            swtpm);

    public static FirmwareDescriptor Uefi(string architecture, bool secureBoot = false) => new(
        $"{architecture}{(secureBoot ? "-secure" : "")}.json",
        "UEFI",
        architecture,
        ["pc-q35-*"],
        "code.fd",
        "raw",
        "vars.fd",
        "raw",
        secureBoot ? ["secure-boot", "requires-smm"] : []);
}
