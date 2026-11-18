using System.Buffers.Binary;
using System.IO.Compression;
using System.IO.Enumeration;
using System.Security.Cryptography;
using System.Text;

namespace Virtualized.Assets;

public sealed class AssetPackOptions
{
    public CompressionLevel Compression { get; set; } = CompressionLevel.SmallestSize;

    /// <summary>
    /// Simple wildcard patterns (<c>*</c>, <c>?</c>) matched against the name of every file and
    /// directory. A matching directory is skipped together with everything inside it.
    /// </summary>
    public List<string> Exclude { get; } = [];

    /// <summary>Receives one line per packed file; leave null for silent operation.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>
    /// When set, the archive is encrypted with AES-256-GCM under this key
    /// (<see cref="AssetPacker.KeySize"/> bytes, see <see cref="AssetPacker.GenerateKey"/>).
    /// </summary>
    public byte[]? Key { get; set; }
}

public sealed record AssetPackResult(int FileCount, long OutputSize);

/// <summary>
/// Packs a directory tree into a single .vpak archive (see <see cref="AssetFormat"/>). Paths inside
/// the archive are relative to the source directory, with '/' separators.
/// </summary>
public static class AssetPacker
{
    public const string FileExtension = AssetFormat.FileExtension;
    public const int KeySize = AssetFormat.KeySize;

    /// <summary>A new random key for <see cref="AssetPackOptions.Key"/>.</summary>
    public static byte[] GenerateKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static AssetPackResult Pack(string sourceDirectory, string outputFile, AssetPackOptions? options = null)
    {
        options ??= new AssetPackOptions();
        if (options.Key is { Length: not KeySize })
            throw new ArgumentException($"Asset archive keys are {KeySize} bytes, got {options.Key.Length}.", nameof(options));

        var root = new DirectoryInfo(sourceDirectory);
        if (!root.Exists)
            throw new DirectoryNotFoundException($"Source directory not found: {sourceDirectory}");

        var output = Path.GetFullPath(outputFile);
        var temp = output + ".tmp";
        var files = new List<(string Path, FileInfo File)>();
        _Collect(root, string.Empty, output, temp, options, files);

        var outputDirectory = Path.GetDirectoryName(output);
        if (!string.IsNullOrEmpty(outputDirectory))
            Directory.CreateDirectory(outputDirectory);

        // Written beside the target and moved into place so a failure never leaves a truncated archive.
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write))
                _Write(stream, files, options);
            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp))
                File.Delete(temp);
        }

        return new AssetPackResult(files.Count, new FileInfo(output).Length);
    }

    private static void _Collect(DirectoryInfo directory, string relative, string output, string temp,
        AssetPackOptions options, List<(string, FileInfo)> files)
    {
        foreach (var sub in directory.EnumerateDirectories().OrderBy(d => d.Name, StringComparer.Ordinal))
        {
            if (_IsExcluded(sub.Name, options))
                continue;

            // Links are not followed: one pointing back into the tree would recurse forever.
            if ((sub.Attributes & FileAttributes.ReparsePoint) != 0)
            {
                options.Log?.Invoke($"skipped link: {_Join(relative, sub.Name)}");
                continue;
            }

            _Collect(sub, _Join(relative, sub.Name), output, temp, options, files);
        }

        foreach (var file in directory.EnumerateFiles().OrderBy(f => f.Name, StringComparer.Ordinal))
        {
            // The archive being written may live inside the tree being packed.
            if (_IsExcluded(file.Name, options) || file.FullName == output || file.FullName == temp)
                continue;

            files.Add((_Join(relative, file.Name), file));
        }
    }

    private static void _Write(Stream stream, List<(string Path, FileInfo File)> files, AssetPackOptions options)
    {
        using var cipher = options.Key is null ? null : new AssetCipher(options.Key);
        var entries = new List<AssetEntry>(files.Count);
        using var blobs = new MemoryStream();

        foreach (var (path, file) in files)
        {
            var data = File.ReadAllBytes(file.FullName);
            var stored = _Deflate(data, options.Compression);
            var compressed = stored is not null;
            stored ??= data;
            // Sealed after deflating: ciphertext doesn't compress.
            if (cipher is not null)
                stored = cipher.Seal(stored, Encoding.UTF8.GetBytes(path));

            entries.Add(new AssetEntry(path, blobs.Position, stored.Length, data.Length, compressed, Crc32.Compute(data)));
            blobs.Write(stored);
            options.Log?.Invoke(compressed ? $"{path} ({data.Length} -> {stored.Length} bytes)" : $"{path} ({data.Length} bytes)");
        }

        Span<byte> header = stackalloc byte[AssetFormat.HeaderSize];
        BinaryPrimitives.WriteUInt32LittleEndian(header, AssetFormat.Magic);
        BinaryPrimitives.WriteUInt32LittleEndian(header[4..], AssetFormat.Version);
        BinaryPrimitives.WriteUInt32LittleEndian(header[8..], cipher is null ? 0 : AssetFormat.Encrypted);
        BinaryPrimitives.WriteUInt32LittleEndian(header[12..], (uint)entries.Count);

        var table = _Table(entries);
        if (cipher is not null)
            table = cipher.Seal(table, header[..AssetFormat.TableAssociatedDataSize]);

        BinaryPrimitives.WriteUInt32LittleEndian(header[16..], checked((uint)table.Length));
        BinaryPrimitives.WriteUInt32LittleEndian(header[20..], Crc32.Compute(table));
        BinaryPrimitives.WriteUInt32LittleEndian(header[24..], Crc32.Compute(header[..24]));

        stream.Write(header);
        stream.Write(table);
        blobs.WriteTo(stream);
    }

    private static byte[] _Table(List<AssetEntry> entries)
    {
        using var table = new MemoryStream();
        using (var writer = new BinaryWriter(table, Encoding.UTF8, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var path = Encoding.UTF8.GetBytes(entry.Path);
                writer.Write(checked((ushort)path.Length));
                writer.Write(path);
                writer.Write(entry.Offset);
                writer.Write(entry.StoredLength);
                writer.Write(entry.Length);
                writer.Write(entry.IsCompressed ? (byte)1 : (byte)0);
                writer.Write(entry.Crc32);
            }
        }

        return table.ToArray();
    }

    /// <summary>The deflated bytes, or null when compression is off or wouldn't make the file smaller.</summary>
    private static byte[]? _Deflate(byte[] data, CompressionLevel level)
    {
        if (level == CompressionLevel.NoCompression)
            return null;

        using var output = new MemoryStream();
        using (var deflate = new DeflateStream(output, level, leaveOpen: true))
            deflate.Write(data);
        return output.Length < data.Length ? output.ToArray() : null;
    }

    private static string _Join(string relative, string name) =>
        relative.Length == 0 ? name : $"{relative}/{name}";

    private static bool _IsExcluded(string name, AssetPackOptions options) =>
        options.Exclude.Any(pattern => FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));
}
