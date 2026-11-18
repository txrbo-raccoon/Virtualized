using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace Virtualized.Updates;

/// <summary>A release on GitHub newer than the running version, with its installer and checksum.</summary>
public sealed record AvailableUpdate(Version Version, string ReleaseUrl, string InstallerName, string InstallerUrl, string ChecksumUrl);

/// <summary>
/// Checks GitHub Releases for a newer Virtualized and installs it by running the new setup silently: Inno Setup
/// replaces the files of this installation (same AppId) and starts the app again. Running VMs keep running and
/// are picked up again by <see cref="Vms.VmManager.ReattachAllAsync"/>.
/// </summary>
public sealed class UpdateService
{
    private const string LatestReleaseUrl = "https://api.github.com/repos/txrbo-raccoon/Virtualized/releases/latest";

    /// <summary>Matches the OutputBaseFilename build-installer.ps1 gives the setup.</summary>
    private const string InstallerPrefix = "virtualized_setup_w64_";

    /// <summary>Major.Minor.Build of this build; the revision is not used for releases.</summary>
    public static Version CurrentVersion { get; } = _Normalize(Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0));

    // Declared after CurrentVersion, which its User-Agent uses.
    private static readonly HttpClient Http = _CreateClient();

    /// <summary>Updates are installed with the Windows setup, so there's nothing to install elsewhere.</summary>
    public static bool IsSupported => OperatingSystem.IsWindows();

    public AvailableUpdate? Available { get; private set; }

    public bool IsChecking { get; private set; }

    public bool IsInstalling { get; private set; }

    /// <summary>Set when the user chose "Later"; the next check offers the update again.</summary>
    public bool IsDismissed { get; private set; }

    public event Action? Changed;

    public void Dismiss()
    {
        IsDismissed = true;
        Changed?.Invoke();
    }

    /// <summary>Returns the newer release, or null when this is the latest one.</summary>
    public async Task<AvailableUpdate?> CheckAsync(CancellationToken ct = default)
    {
        IsChecking = true;
        Changed?.Invoke();
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUrl);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await Http.SendAsync(request, ct);
            // 404: the repository has no published release yet.
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
                return Available = null;
            response.EnsureSuccessStatusCode();

            var release = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct))!;
            var version = ParseTag(release["tag_name"]!.GetValue<string>());
            Available = version > CurrentVersion ? _FindInstaller(release, version) : null;
            IsDismissed = false;
            return Available;
        }
        finally
        {
            IsChecking = false;
            Changed?.Invoke();
        }
    }

    /// <summary>
    /// Downloads the setup, checks it against the published SHA-256 and starts it. The caller closes the app
    /// right after, so the setup can replace Virtualized.exe.
    /// </summary>
    public async Task InstallAsync(AvailableUpdate update, CancellationToken ct = default)
    {
        IsInstalling = true;
        Changed?.Invoke();
        try
        {
            var directory = Path.Combine(Path.GetTempPath(), "Virtualized", "updates");
            Directory.CreateDirectory(directory);
            var installer = Path.Combine(directory, update.InstallerName);

            await using (var source = await Http.GetStreamAsync(update.InstallerUrl, ct))
            await using (var file = File.Create(installer))
                await source.CopyToAsync(file, ct);

            var expected = ParseChecksum(await Http.GetStringAsync(update.ChecksumUrl, ct));
            string actual;
            await using (var file = File.OpenRead(installer))
                actual = Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct));
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(installer);
                throw new InvalidDataException($"The downloaded installer doesn't match its published SHA-256 ({actual}, expected {expected}).");
            }

            // Shell execute, so Windows can show the UAC prompt the setup asks for.
            Process.Start(new ProcessStartInfo(installer)
            {
                UseShellExecute = true,
                ArgumentList = { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/RELAUNCH=1" },
            });
        }
        catch
        {
            IsInstalling = false;
            throw;
        }
        finally
        {
            Changed?.Invoke();
        }
    }

    /// <summary>Accepts "v1.2.3" and "1.2.3".</summary>
    internal static Version ParseTag(string tag) => _Normalize(Version.Parse(tag.TrimStart('v', 'V')));

    /// <summary>Reads the hash from "&lt;hash&gt;  &lt;file name&gt;", the sha256sum format build-installer.ps1 writes.</summary>
    internal static string ParseChecksum(string content) =>
        content.Split((char[])[' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()
        ?? throw new InvalidDataException("The checksum file is empty.");

    private static AvailableUpdate? _FindInstaller(JsonNode release, Version version)
    {
        var assets = release["assets"]!.AsArray()
            .Select(asset => (Name: asset!["name"]!.GetValue<string>(), Url: asset["browser_download_url"]!.GetValue<string>()))
            .ToList();

        var installer = assets.FirstOrDefault(asset =>
            asset.Name.StartsWith(InstallerPrefix, StringComparison.OrdinalIgnoreCase) && asset.Name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase));
        var checksum = assets.FirstOrDefault(asset => string.Equals(asset.Name, installer.Name + ".sha256", StringComparison.OrdinalIgnoreCase));

        // A release without its installer or checksum (still uploading, or not for Windows) is not offered.
        return installer.Name is null || checksum.Name is null
            ? null
            : new AvailableUpdate(version, release["html_url"]!.GetValue<string>(), installer.Name, installer.Url, checksum.Url);
    }

    private static Version _Normalize(Version version) => new(version.Major, version.Minor, Math.Max(version.Build, 0));

    private static HttpClient _CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub's API rejects requests without a User-Agent.
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Virtualized", CurrentVersion.ToString()));
        return client;
    }
}
