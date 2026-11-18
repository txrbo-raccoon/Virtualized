using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Virtualized.Qemu;

public sealed partial class QemuLocator
{
    /// <summary>Guest architectures Virtualized creates VMs for.</summary>
    public static readonly string[] Architectures = ["x86_64", "aarch64"];

    /// <summary>This computer's architecture in QEMU's naming.</summary>
    public static string HostArchitecture => RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "aarch64" : "x86_64";

    /// <summary>
    /// Generous because the first run of a fresh install is slow on Windows: the antivirus scans
    /// qemu-system and every DLL it loads before letting it start.
    /// </summary>
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Finds a QEMU installation. A configured directory is the only place looked at, so a wrong
    /// setting is reported instead of silently falling back to another installation.
    /// </summary>
    public async Task<QemuDetection> DetectAsync(string? configuredDirectory, CancellationToken ct = default)
    {
        var searched = new List<string>();
        var directories = string.IsNullOrWhiteSpace(configuredDirectory)
            ? _CandidateDirectories()
            : [configuredDirectory.Trim()];

        foreach (var directory in directories)
        {
            searched.Add(directory);

            var binaries = Architectures
                .Select(arch => (Architecture: arch, Path: Path.Combine(directory, _Executable($"qemu-system-{arch}"))))
                .Where(candidate => File.Exists(candidate.Path))
                .ToList();
            if (binaries.Count == 0)
                continue;

            try
            {
                return new QemuDetection(await _InspectAsync(directory, binaries, ct), searched);
            }
            catch (Exception ex) when (ex is TimeoutException or System.ComponentModel.Win32Exception or FormatException)
            {
                return new QemuDetection(null, searched, $"QEMU was found in {directory} but could not be run: {ex.Message}");
            }
        }

        return new QemuDetection(null, searched);
    }

    private static async Task<QemuInstallation> _InspectAsync(
        string directory, List<(string Architecture, string Path)> binaries, CancellationToken ct)
    {
        var versionOutput = await ProcessRunner.RunAsync(binaries[0].Path, ["--version"], ToolTimeout, ct);
        var version = ParseVersion(versionOutput.StdOut)
                      ?? throw new FormatException($"Unrecognized version output: {versionOutput.StdOut.Trim()}");

        var systemBinaries = new List<QemuSystemBinary>();
        foreach (var (architecture, path) in binaries)
        {
            var help = await ProcessRunner.RunAsync(path, ["-accel", "help"], ToolTimeout, ct);
            var accelerators = ParseAcceleratorHelp(help.StdOut)
                .Where(AcceleratorProbe.Preference.Contains)
                .Select(name => new AcceleratorInfo(name, AcceleratorProbe.IsUsable(name)))
                .ToList();
            systemBinaries.Add(new QemuSystemBinary(architecture, path, accelerators));
        }

        // "-tpmdev help" is rejected as an unknown option on builds without TPM support (all Windows builds).
        var tpm = await ProcessRunner.RunAsync(binaries[0].Path, ["-tpmdev", "help"], ToolTimeout, ct);

        var qemuImg = Path.Combine(directory, _Executable("qemu-img"));

        return new QemuInstallation(
            directory,
            version,
            systemBinaries,
            File.Exists(qemuImg) ? qemuImg : null,
            FirmwareCatalog.Load(directory, Architectures),
            tpm.ExitCode == 0,
            _FindExecutable("swtpm", [directory, .. _CandidateDirectories()]));
    }

    internal static Version? ParseVersion(string output)
    {
        var match = VersionRegex().Match(output);
        return match.Success ? Version.Parse(match.Groups[1].Value) : null;
    }

    /// <summary>Parses "Accelerators supported in QEMU binary:" followed by one name per line.</summary>
    internal static IEnumerable<string> ParseAcceleratorHelp(string output) =>
        output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Skip(1)
            .Where(line => !line.Contains(' '));

    private static List<string> _CandidateDirectories()
    {
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (OperatingSystem.IsWindows())
        {
            directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "qemu"));
        }
        else
        {
            // Homebrew (Apple Silicon and Intel), MacPorts and the usual Linux locations; a GUI app on macOS
            // does not inherit the shell's PATH, so these have to be listed explicitly.
            directories.AddRange(["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin", "/usr/bin"]);
        }

        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        return directories.Distinct(comparer).ToList();
    }

    private static string? _FindExecutable(string name, IEnumerable<string> directories) =>
        directories.Select(directory => Path.Combine(directory, _Executable(name))).FirstOrDefault(File.Exists);

    private static string _Executable(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    [GeneratedRegex(@"version (\d+\.\d+(?:\.\d+)?)")]
    private static partial Regex VersionRegex();
}
