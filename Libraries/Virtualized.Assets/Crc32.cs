namespace Virtualized.Assets;

/// <summary>CRC-32 (IEEE 802.3), as used by zip and PNG.</summary>
internal static class Crc32
{
    private static readonly uint[] Table = _BuildTable();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] _BuildTable()
    {
        const uint polynomial = 0xEDB88320;
        var table = new uint[256];
        for (uint i = 0; i < table.Length; i++)
        {
            var crc = i;
            for (var bit = 0; bit < 8; bit++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
            table[i] = crc;
        }

        return table;
    }
}
