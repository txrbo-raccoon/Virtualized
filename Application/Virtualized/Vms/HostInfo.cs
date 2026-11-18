using System.Runtime.Intrinsics.X86;
using System.Text;

namespace Virtualized.Vms;

/// <summary>Limits of this computer that bound what a VM may be given.</summary>
public static class HostInfo
{
    public static int ProcessorCount => Environment.ProcessorCount;

    public static int TotalMemoryMiB => (int)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024));

    /// <summary>Leaves 1 GiB for the host so a single VM can't starve it completely.</summary>
    public static int MaxGuestMemoryMiB => Math.Max(VmValidator.MinMemoryMiB, TotalMemoryMiB - 1024);

    /// <summary>The CPUID vendor string ("GenuineIntel", "AuthenticAMD"), or "" on non-x86 hosts.</summary>
    public static string CpuVendor { get; } = _ReadCpuVendor();

    private static string _ReadCpuVendor()
    {
        if (!X86Base.IsSupported)
            return "";

        // Leaf 0 returns the vendor as 12 ASCII characters in EBX, EDX, ECX (in that order).
        var (_, ebx, ecx, edx) = X86Base.CpuId(0, 0);
        Span<byte> bytes = stackalloc byte[12];
        BitConverter.TryWriteBytes(bytes, ebx);
        BitConverter.TryWriteBytes(bytes[4..], edx);
        BitConverter.TryWriteBytes(bytes[8..], ecx);
        return Encoding.ASCII.GetString(bytes);
    }
}
