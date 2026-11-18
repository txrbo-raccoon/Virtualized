using System.Security.Cryptography;

namespace Virtualized.Assets;

/// <summary>AES-256-GCM sealing of archive parts as nonce | ciphertext | tag (see <see cref="AssetFormat"/>).</summary>
internal sealed class AssetCipher : IDisposable
{
    private readonly AesGcm _aes;

    public AssetCipher(byte[] key)
    {
        if (key.Length != AssetFormat.KeySize)
            throw new ArgumentException($"Asset archive keys are {AssetFormat.KeySize} bytes, got {key.Length}.", nameof(key));
        _aes = new AesGcm(key, AssetFormat.TagSize);
    }

    public byte[] Seal(ReadOnlySpan<byte> plaintext, ReadOnlySpan<byte> associatedData)
    {
        var sealedData = new byte[plaintext.Length + AssetFormat.SealOverhead];
        var nonce = sealedData.AsSpan(0, AssetFormat.NonceSize);
        RandomNumberGenerator.Fill(nonce);
        _aes.Encrypt(nonce, plaintext, sealedData.AsSpan(AssetFormat.NonceSize, plaintext.Length),
            sealedData.AsSpan(AssetFormat.NonceSize + plaintext.Length), associatedData);
        return sealedData;
    }

    /// <summary>Throws <see cref="AuthenticationTagMismatchException"/> for a wrong key or tampered data.</summary>
    public byte[] Open(ReadOnlySpan<byte> sealedData, ReadOnlySpan<byte> associatedData)
    {
        if (sealedData.Length < AssetFormat.SealOverhead)
            throw new InvalidDataException("Sealed asset data is truncated.");

        var plaintext = new byte[sealedData.Length - AssetFormat.SealOverhead];
        _aes.Decrypt(sealedData[..AssetFormat.NonceSize], sealedData.Slice(AssetFormat.NonceSize, plaintext.Length),
            sealedData[^AssetFormat.TagSize..], plaintext, associatedData);
        return plaintext;
    }

    public void Dispose() => _aes.Dispose();
}
