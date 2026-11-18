using System.Text.Json;

namespace Virtualized.Qemu;

public sealed record FirmwareDescriptor(
    string Name,
    string Description,
    string Architecture,
    IReadOnlyList<string> Machines,
    string CodePath,
    string CodeFormat,
    string? VarsTemplatePath,
    string? VarsFormat,
    IReadOnlyList<string> Features)
{
    public bool SecureBoot => Features.Contains("secure-boot");

    public bool RequiresSmm => Features.Contains("requires-smm");

    /// <summary>The variable store template already has Secure Boot keys (usually Microsoft's) enrolled.</summary>
    public bool HasEnrolledKeys => Features.Contains("enrolled-keys");
}

/// <summary>
/// Reads QEMU's firmware descriptor files (docs/interop/firmware.json), the same metadata libvirt uses
/// to pick OVMF/AAVMF builds, and keeps the UEFI flash images for the architectures Virtualized supports.
/// </summary>
internal static class FirmwareCatalog
{
    public static IReadOnlyList<FirmwareDescriptor> Load(string qemuDirectory, IReadOnlyCollection<string> architectures)
    {
        // A file name found in an earlier directory overrides the same name in later ones, as in the spec.
        var byName = new Dictionary<string, (string Path, string Directory)>(StringComparer.Ordinal);
        foreach (var directory in _DescriptorDirectories(qemuDirectory).Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(directory, "*.json"))
                byName.TryAdd(Path.GetFileName(file), (file, directory));
        }

        var result = new List<FirmwareDescriptor>();
        foreach (var (name, (path, directory)) in byName.OrderBy(entry => entry.Key, StringComparer.Ordinal))
        {
            try
            {
                result.AddRange(_Parse(name, path, directory, qemuDirectory)
                    .Where(descriptor => architectures.Contains(descriptor.Architecture)));
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IOException)
            {
                // An empty or malformed descriptor (empty files are how distros disable one) is skipped.
            }
        }

        return result;
    }

    private static IEnumerable<string> _DescriptorDirectories(string qemuDirectory)
    {
        if (!OperatingSystem.IsWindows())
        {
            var configHome = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            yield return Path.Combine(configHome, "qemu", "firmware");
            yield return "/etc/qemu/firmware";
        }

        // Windows installer layout: <qemu>\share\firmware. Homebrew and /usr/local builds: <prefix>/share/qemu/firmware.
        yield return Path.Combine(qemuDirectory, "share", "firmware");
        yield return Path.GetFullPath(Path.Combine(qemuDirectory, "..", "share", "qemu", "firmware"));

        if (!OperatingSystem.IsWindows())
            yield return "/usr/share/qemu/firmware";
    }

    private static IEnumerable<FirmwareDescriptor> _Parse(string name, string path, string descriptorDirectory, string qemuDirectory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;

        if (!root.GetProperty("interface-types").EnumerateArray().Any(type => type.GetString() == "uefi"))
            yield break;

        var mapping = root.GetProperty("mapping");
        if (mapping.GetProperty("device").GetString() != "flash")
            yield break;

        var executable = mapping.GetProperty("executable");
        var codePath = _ResolveFile(executable.GetProperty("filename").GetString()!, descriptorDirectory, qemuDirectory);
        if (codePath is null)
            yield break;

        string? varsPath = null;
        string? varsFormat = null;
        if (mapping.TryGetProperty("nvram-template", out var template))
        {
            varsPath = _ResolveFile(template.GetProperty("filename").GetString()!, descriptorDirectory, qemuDirectory);
            if (varsPath is null)
                yield break;
            varsFormat = template.GetProperty("format").GetString();
        }

        var features = root.TryGetProperty("features", out var featureArray)
            ? featureArray.EnumerateArray().Select(feature => feature.GetString()!).ToList()
            : [];

        foreach (var target in root.GetProperty("targets").EnumerateArray())
        {
            yield return new FirmwareDescriptor(
                name,
                root.TryGetProperty("description", out var description) ? description.GetString() ?? name : name,
                target.GetProperty("architecture").GetString()!,
                target.GetProperty("machines").EnumerateArray().Select(machine => machine.GetString()!).ToList(),
                codePath,
                executable.GetProperty("format").GetString()!,
                varsPath,
                varsFormat,
                features);
        }
    }

    /// <summary>
    /// Descriptors carry the path chosen at build time (e.g. /usr/local/share/... in the Windows installer),
    /// so when it does not exist the file is looked up by name next to the installation instead.
    /// </summary>
    private static string? _ResolveFile(string fileName, string descriptorDirectory, string qemuDirectory)
    {
        if (File.Exists(fileName))
            return Path.GetFullPath(fileName);

        var bareName = Path.GetFileName(fileName);
        string[] candidates =
        [
            Path.Combine(Path.GetDirectoryName(descriptorDirectory)!, bareName),
            Path.Combine(qemuDirectory, "share", bareName),
            Path.GetFullPath(Path.Combine(qemuDirectory, "..", "share", "qemu", bareName)),
        ];

        return candidates.FirstOrDefault(File.Exists);
    }
}
