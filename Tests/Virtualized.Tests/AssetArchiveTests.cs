using System.IO.Compression;
using System.Text;
using Virtualized.Assets;

namespace Virtualized.Tests;

public sealed class AssetArchiveTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sqemu-tests", Guid.NewGuid().ToString("N"));

    private string Source => Path.Combine(_root, "source");
    private string Output => Path.Combine(_root, "out.vpak");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Write(string path, string content)
    {
        var full = Path.Combine(Source, path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content);
    }

    [Fact]
    public void PackedFilesRoundTrip()
    {
        Write("index.html", "<html></html>");
        Write("css/app.css", string.Concat(Enumerable.Repeat("body { margin: 0; }\n", 200)));
        Write("lib/novnc/core/rfb.js", "export default class RFB {}");

        var result = AssetPacker.Pack(Source, Output);
        using var archive = AssetArchive.Open(Output);

        Assert.Equal(3, result.FileCount);
        Assert.Equal(["css/app.css", "index.html", "lib/novnc/core/rfb.js"], archive.Entries.Select(e => e.Path).Order());
        Assert.Equal("<html></html>", Encoding.UTF8.GetString(archive.ReadAllBytes("index.html")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Source, "css", "app.css")), archive.ReadAllBytes("css/app.css"));
        Assert.True(archive.TryGetEntry("css/app.css", out var css));
        Assert.True(css.IsCompressed);
    }

    [Fact]
    public void LookupAcceptsLeadingSlashAndBackslashes()
    {
        Write("js/vm-console.js", "export {}");
        AssetPacker.Pack(Source, Output);
        using var archive = AssetArchive.Open(Output);

        Assert.True(archive.Contains("/js/vm-console.js"));
        Assert.True(archive.Contains(@"js\vm-console.js"));
        Assert.False(archive.Contains("JS/vm-console.js"));
    }

    [Fact]
    public void IncompressibleFilesAreStoredAsIs()
    {
        var noise = new byte[4096];
        new Random(42).NextBytes(noise);
        Directory.CreateDirectory(Source);
        File.WriteAllBytes(Path.Combine(Source, "noise.bin"), noise);

        AssetPacker.Pack(Source, Output);
        using var archive = AssetArchive.Open(Output);

        Assert.True(archive.TryGetEntry("noise.bin", out var entry));
        Assert.False(entry.IsCompressed);
        Assert.Equal(noise, archive.Read(entry));
    }

    [Fact]
    public void ExcludedNamesAreSkipped()
    {
        Write("index.html", "x");
        Write("notes.md", "x");
        Write("drafts/old.css", "x");
        var options = new AssetPackOptions { Exclude = { "*.md", "drafts" } };

        AssetPacker.Pack(Source, Output, options);
        using var archive = AssetArchive.Open(Output);

        Assert.Equal(["index.html"], archive.Entries.Select(e => e.Path));
    }

    [Fact]
    public void CorruptBlobIsDetected()
    {
        Write("index.html", "<html><body>hello</body></html>");
        AssetPacker.Pack(Source, Output, new AssetPackOptions { Compression = CompressionLevel.NoCompression });
        var bytes = File.ReadAllBytes(Output);
        bytes[^1] ^= 0xFF;

        using var archive = AssetArchive.Open(new MemoryStream(bytes));

        Assert.Throws<InvalidDataException>(() => archive.ReadAllBytes("index.html"));
    }

    [Fact]
    public void CorruptTableIsRejected()
    {
        Write("index.html", "x");
        AssetPacker.Pack(Source, Output);
        var bytes = File.ReadAllBytes(Output);
        bytes[30] ^= 0xFF; // Inside the first table entry's path.

        Assert.Throws<InvalidDataException>(() => AssetArchive.Open(new MemoryStream(bytes)));
    }

    [Fact]
    public void OtherFilesAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => AssetArchive.Open(new MemoryStream(new byte[64])));
    }

    [Fact]
    public void EncryptedFilesRoundTrip()
    {
        Write("index.html", "<html><body>secret page</body></html>");
        Write("css/app.css", string.Concat(Enumerable.Repeat("body { margin: 0; }\n", 200)));
        var key = AssetPacker.GenerateKey();

        AssetPacker.Pack(Source, Output, new AssetPackOptions { Key = key, Compression = CompressionLevel.NoCompression });
        using var archive = AssetArchive.Open(Output, key);

        Assert.True(archive.IsEncrypted);
        Assert.Equal("<html><body>secret page</body></html>", Encoding.UTF8.GetString(archive.ReadAllBytes("index.html")));
        Assert.Equal(File.ReadAllBytes(Path.Combine(Source, "css", "app.css")), archive.ReadAllBytes("css/app.css"));
    }

    [Fact]
    public void EncryptedArchiveHidesNamesAndContents()
    {
        Write("index.html", "<html><body>secret page</body></html>");
        AssetPacker.Pack(Source, Output, new AssetPackOptions { Key = AssetPacker.GenerateKey(), Compression = CompressionLevel.NoCompression });

        var raw = Encoding.Latin1.GetString(File.ReadAllBytes(Output));

        Assert.DoesNotContain("index.html", raw);
        Assert.DoesNotContain("secret page", raw);
    }

    [Fact]
    public void CompressedFilesAreStillCompressedWhenEncrypted()
    {
        Write("css/app.css", string.Concat(Enumerable.Repeat("body { margin: 0; }\n", 200)));
        var key = AssetPacker.GenerateKey();

        AssetPacker.Pack(Source, Output, new AssetPackOptions { Key = key });
        using var archive = AssetArchive.Open(Output, key);

        Assert.True(archive.TryGetEntry("css/app.css", out var css));
        Assert.True(css.IsCompressed);
        Assert.True(css.StoredLength < css.Length / 10);
    }

    [Fact]
    public void WrongKeyIsRejected()
    {
        Write("index.html", "x");
        AssetPacker.Pack(Source, Output, new AssetPackOptions { Key = AssetPacker.GenerateKey() });

        Assert.Throws<InvalidDataException>(() => AssetArchive.Open(Output, AssetPacker.GenerateKey()));
    }

    [Fact]
    public void MissingKeyIsRejected()
    {
        Write("index.html", "x");
        AssetPacker.Pack(Source, Output, new AssetPackOptions { Key = AssetPacker.GenerateKey() });

        Assert.Throws<InvalidDataException>(() => AssetArchive.Open(Output));
    }

    [Fact]
    public void TamperedEncryptedBlobIsDetected()
    {
        Write("index.html", "<html><body>hello</body></html>");
        var key = AssetPacker.GenerateKey();
        AssetPacker.Pack(Source, Output, new AssetPackOptions { Key = key });
        var bytes = File.ReadAllBytes(Output);
        bytes[^20] ^= 0x01; // Inside the blob's ciphertext.

        using var archive = AssetArchive.Open(new MemoryStream(bytes), key);

        Assert.Throws<InvalidDataException>(() => archive.ReadAllBytes("index.html"));
    }

    [Fact]
    public void ShortKeyIsRefused()
    {
        Write("index.html", "x");

        Assert.Throws<ArgumentException>(() => AssetPacker.Pack(Source, Output, new AssetPackOptions { Key = new byte[16] }));
    }

    [Fact]
    public void ExecutableEmbedsEncryptedWebRoot()
    {
        using var stream = typeof(AppPaths).Assembly.GetManifestResourceStream("wwwroot.vpak");
        Assert.NotNull(stream);
        using var archive = AssetArchive.Open(stream, WebRootKey.Value.ToArray());

        Assert.True(archive.IsEncrypted);
        Assert.Contains("<div id=\"app\">", Encoding.UTF8.GetString(archive.ReadAllBytes("index.html")));
        Assert.True(archive.Contains("js/vm-console.js"));
        Assert.True(archive.Contains("lib/novnc/core/rfb.js"));
        Assert.True(archive.Contains("favicon.ico"));
    }
}
