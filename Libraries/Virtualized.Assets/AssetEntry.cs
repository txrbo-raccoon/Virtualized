namespace Virtualized.Assets;

/// <summary>A file inside an <see cref="AssetArchive"/>.</summary>
/// <param name="Path">Relative to the packed directory, with '/' separators and no leading slash.</param>
/// <param name="Offset">Start of the stored bytes, relative to the first blob.</param>
/// <param name="StoredLength">Bytes in the archive: the deflated size when compressed.</param>
/// <param name="Length">Size of the original file.</param>
/// <param name="Crc32">Of the original file, checked on every read.</param>
public sealed record AssetEntry(string Path, long Offset, long StoredLength, long Length, bool IsCompressed, uint Crc32)
{
    public string Name => System.IO.Path.GetFileName(Path);
}
