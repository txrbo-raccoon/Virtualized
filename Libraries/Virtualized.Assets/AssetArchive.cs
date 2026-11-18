using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;

namespace Virtualized.Assets;

/// <summary>
/// Read access to a .vpak archive (see <see cref="AssetFormat"/>). The file table is read up front;
/// file contents are read, decrypted, inflated and checked on demand. Safe to use from several threads.
/// </summary>
public sealed class AssetArchive : IDisposable
{
    private readonly Stream _stream;
    private readonly long _blobStart;
    private readonly Dictionary<string, AssetEntry> _entries;
    private readonly AssetCipher? _cipher;
    private readonly Lock _lock = new();

    private AssetArchive(Stream stream, long blobStart, Dictionary<string, AssetEntry> entries, AssetCipher? cipher)
    {
        _stream = stream;
        _blobStart = blobStart;
        _entries = entries;
        _cipher = cipher;
    }

    public IReadOnlyCollection<AssetEntry> Entries => _entries.Values;

    public bool IsEncrypted => _cipher is not null;

    public static AssetArchive Open(string path, byte[]? key = null) =>
        Open(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), key);

    /// <summary>
    /// Takes ownership of <paramref name="stream"/>, which must be seekable. <paramref name="key"/> is
    /// required for an encrypted archive and ignored otherwise.
    /// </summary>
    public static AssetArchive Open(Stream stream, byte[]? key = null)
    {
        AssetCipher? cipher = null;
        try
        {
            var header = new byte[AssetFormat.HeaderSize];
            stream.Position = 0;
            stream.ReadExactly(header);

            if (BinaryPrimitives.ReadUInt32LittleEndian(header) != AssetFormat.Magic)
                throw new InvalidDataException("Not an asset archive.");
            var version = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (version != AssetFormat.Version)
                throw new InvalidDataException($"Asset archive version {version} isn't supported (expected {AssetFormat.Version}).");
            if (Crc32.Compute(header.AsSpan(0, 24)) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(24)))
                throw new InvalidDataException("Asset archive header is corrupt.");

            var flags = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            var fileCount = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(12));
            var table = new byte[BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(16))];
            stream.ReadExactly(table);
            if (Crc32.Compute(table) != BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)))
                throw new InvalidDataException("Asset archive file table is corrupt.");

            var blobStart = AssetFormat.HeaderSize + table.Length;
            if ((flags & AssetFormat.Encrypted) != 0)
            {
                if (key is null)
                    throw new InvalidDataException("Asset archive is encrypted and no key was given.");
                cipher = new AssetCipher(key);
                table = _Unseal(cipher, table, header.AsSpan(0, AssetFormat.TableAssociatedDataSize), "file table");
            }

            var entries = new Dictionary<string, AssetEntry>((int)fileCount, StringComparer.Ordinal);
            using var reader = new BinaryReader(new MemoryStream(table), Encoding.UTF8);
            for (var i = 0; i < fileCount; i++)
            {
                var path = Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadUInt16()));
                var entry = new AssetEntry(path, reader.ReadInt64(), reader.ReadInt64(), reader.ReadInt64(),
                    reader.ReadByte() != 0, reader.ReadUInt32());
                entries[path] = entry;
            }

            return new AssetArchive(stream, blobStart, entries, cipher);
        }
        catch
        {
            cipher?.Dispose();
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Looks up a file; leading slashes and '\' separators are accepted.</summary>
    public bool TryGetEntry(string path, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out AssetEntry? entry) =>
        _entries.TryGetValue(NormalizePath(path), out entry);

    public bool Contains(string path) => TryGetEntry(path, out _);

    public byte[] ReadAllBytes(string path) =>
        TryGetEntry(path, out var entry) ? Read(entry) : throw new FileNotFoundException($"'{path}' is not in the asset archive.", path);

    public byte[] Read(AssetEntry entry)
    {
        var data = new byte[entry.StoredLength];
        // AesGcm isn't documented as safe for concurrent use, so decryption shares the stream's lock.
        lock (_lock)
        {
            _stream.Position = _blobStart + entry.Offset;
            _stream.ReadExactly(data);
            if (_cipher is not null)
                data = _Unseal(_cipher, data, Encoding.UTF8.GetBytes(entry.Path), $"'{entry.Path}'");
        }

        if (entry.IsCompressed)
        {
            var inflated = new byte[entry.Length];
            using var deflate = new DeflateStream(new MemoryStream(data), CompressionMode.Decompress);
            deflate.ReadExactly(inflated);
            data = inflated;
        }

        if (Crc32.Compute(data) != entry.Crc32)
            throw new InvalidDataException($"'{entry.Path}' in the asset archive is corrupt.");
        return data;
    }

    public void Dispose()
    {
        _cipher?.Dispose();
        _stream.Dispose();
    }

    internal static string NormalizePath(string path) => path.Replace('\\', '/').Trim('/');

    private static byte[] _Unseal(AssetCipher cipher, byte[] sealedData, ReadOnlySpan<byte> associatedData, string what)
    {
        try
        {
            return cipher.Open(sealedData, associatedData);
        }
        catch (AuthenticationTagMismatchException)
        {
            throw new InvalidDataException($"Couldn't decrypt the asset archive's {what}: wrong key, or the archive was modified.");
        }
    }
}
