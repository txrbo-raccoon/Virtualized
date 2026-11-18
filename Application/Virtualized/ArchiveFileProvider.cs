using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Primitives;
using Virtualized.Assets;

namespace Virtualized;

/// <summary>
/// Serves the WebView's files (wwwroot) from the asset archive embedded in the executable, which the
/// build packs and encrypts from wwwroot (see PackWebRoot in Virtualized.csproj), so the files aren't
/// left loose next to the app. The key is compiled in (WebRootKey), which keeps casual readers out
/// but not someone who decompiles the app.
/// </summary>
internal sealed class ArchiveFileProvider(AssetArchive archive) : IFileProvider
{
    private const string ResourceName = "wwwroot.vpak";

    public static ArchiveFileProvider FromEmbeddedWebRoot()
    {
        var stream = typeof(ArchiveFileProvider).Assembly.GetManifestResourceStream(ResourceName)
                     ?? throw new InvalidOperationException($"{ResourceName} is missing from the executable; rebuild the project.");
        return new ArchiveFileProvider(AssetArchive.Open(stream, WebRootKey.Value.ToArray()));
    }

    public IFileInfo GetFileInfo(string subpath) =>
        archive.TryGetEntry(subpath, out var entry) ? new ArchiveFileInfo(archive, entry) : new NotFoundFileInfo(subpath);

    // Blazor WebView only asks for files by path; nothing lists directories.
    public IDirectoryContents GetDirectoryContents(string subpath) => NotFoundDirectoryContents.Singleton;

    // The archive is fixed for the lifetime of the process.
    public IChangeToken Watch(string filter) => NullChangeToken.Singleton;

    private sealed class ArchiveFileInfo(AssetArchive archive, AssetEntry entry) : IFileInfo
    {
        public bool Exists => true;
        public long Length => entry.Length;
        public string? PhysicalPath => null;
        public string Name => entry.Name;
        public DateTimeOffset LastModified => DateTimeOffset.UnixEpoch;
        public bool IsDirectory => false;

        public Stream CreateReadStream() => new MemoryStream(archive.Read(entry), writable: false);
    }
}
