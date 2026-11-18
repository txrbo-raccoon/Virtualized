namespace Virtualized.Assets;

/// <summary>
/// Layout of a .vpak archive, all little-endian. Derived from Vessel's VFS images, without the
/// directory table: directories are implied by the '/'-separated paths of the files.
/// <code>
/// header   magic u32 "VPAK" | version u32 | flags u32 | file count u32 | table size u32 | table crc32 u32 | header crc32 u32
/// table    per file: path length u16 | path utf-8 | offset i64 | stored length i64 | length i64 | compressed u8 | crc32 u32
/// blobs    every file's bytes, deflated when that made them smaller; offsets are relative to the first blob
/// </code>
/// With <see cref="Encrypted"/> set, the table and every blob are sealed with AES-256-GCM as
/// nonce | ciphertext | tag. The table is bound to the first 16 header bytes and each blob to its
/// path (associated data), so neither can be swapped for another. Blobs are deflated before sealing.
/// The table crc32 covers the table as stored; file crc32s cover the original files.
/// </summary>
internal static class AssetFormat
{
    public const uint Magic = 0x4B415056; // "VPAK"
    public const uint Version = 2;
    public const int HeaderSize = 28;
    public const string FileExtension = ".vpak";

    public const uint Encrypted = 1;

    public const int KeySize = 32;
    public const int NonceSize = 12;
    public const int TagSize = 16;
    public const int SealOverhead = NonceSize + TagSize;

    /// <summary>Header bytes the encrypted table is bound to: magic, version, flags and file count.</summary>
    public const int TableAssociatedDataSize = 16;
}
