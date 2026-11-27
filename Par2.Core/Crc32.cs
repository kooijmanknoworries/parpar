namespace Par2.Core;

/// <summary>
/// CCITT CRC-32 (polynomial 0xEDB88320, reversed) exactly as used by PAR
/// 2.0 for block checksums. The block CRC is computed with the running
/// value inverted before and after (crc = ~0 ^ CRCUpdateBlock(~0 ^ crc,...)).
/// </summary>
public static class Crc32
{
    private static readonly uint[] Table = BuildTable(0xEDB88320);

    public static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data)
            crc = (crc >> 8) ^ Table[(crc ^ b) & 0xFF];
        return crc;
    }

    /// <summary>Update the CRC with <paramref name="count"/> zero bytes.</summary>
    public static uint UpdateZeros(uint crc, int count)
    {
        // Process zero bytes quickly.
        while (count > 0)
        {
            int n = Math.Min(count, 8);
            for (int i = 0; i < n; i++)
                crc = (crc >> 8) ^ Table[crc & 0xFF];
            count -= n;
        }
        return crc;
    }

    /// <summary>
    /// Compute the PAR 2.0 CRC for a block: the block is treated as
    /// zero-padded to blocksize and the running CRC uses the ~0 ^ ... ^ ~0
    /// convention. Returns the final (un-inverted) CRC.
    /// </summary>
    public static uint BlockCrc(ReadOnlySpan<byte> data, int blockSize)
    {
        uint crc = 0;
        int len = data.Length;
        crc = ~Update(~crc, data);
        if (len < blockSize)
            crc = ~UpdateZeros(~crc, blockSize - len);
        return crc;
    }

    private static uint[] BuildTable(uint poly)
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int k = 0; k < 8; k++)
                c = (c >> 1) ^ ((c & 1) != 0 ? poly : 0);
            table[i] = c;
        }
        return table;
    }
}
