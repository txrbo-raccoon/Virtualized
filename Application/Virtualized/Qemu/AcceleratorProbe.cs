using System.Runtime.InteropServices;

namespace Virtualized.Qemu;

/// <summary>
/// Checks whether an accelerator QEMU was built with can actually be used on this host
/// (the hypervisor feature is enabled and the current user has access to it).
/// </summary>
internal static class AcceleratorProbe
{
    /// <summary>Accelerators Virtualized knows how to use, best first.</summary>
    public static readonly string[] Preference = ["kvm", "hvf", "whpx", "tcg"];

    public static bool IsUsable(string accelerator) => accelerator switch
    {
        "tcg" => true,
        "whpx" => OperatingSystem.IsWindows() && _IsWhpxPresent(),
        "kvm" => OperatingSystem.IsLinux() && _CanOpenReadWrite("/dev/kvm"),
        "hvf" => OperatingSystem.IsMacOS() && _IsHvfSupported(),
        _ => false,
    };

    /// <summary>Accelerators that can emulate System Management Mode, which the Secure Boot builds of OVMF need.</summary>
    public static bool SupportsSmm(string accelerator) => accelerator is "kvm" or "tcg";

    private static bool _IsWhpxPresent()
    {
        try
        {
            const int hypervisorPresent = 0;
            return WHvGetCapability(hypervisorPresent, out var present, sizeof(int), out _) >= 0 && present != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // WinHvPlatform.dll only exists when the "Windows Hypervisor Platform" feature is installed.
            return false;
        }
    }

    private static bool _CanOpenReadWrite(string path)
    {
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.ReadWrite);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool _IsHvfSupported()
    {
        var length = (nint)sizeof(int);
        return sysctlbyname("kern.hv_support", out var value, ref length, IntPtr.Zero, 0) == 0 && value == 1;
    }

    [DllImport("WinHvPlatform.dll")]
    private static extern int WHvGetCapability(int capabilityCode, out int capabilityBuffer, uint capabilityBufferSizeInBytes, out uint writtenSizeInBytes);

    [DllImport("libc")]
    private static extern int sysctlbyname(string name, out int oldValue, ref nint oldLength, IntPtr newValue, nint newLength);
}
