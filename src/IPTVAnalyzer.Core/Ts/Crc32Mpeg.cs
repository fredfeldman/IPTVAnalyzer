namespace IPTVAnalyzer.Core.Ts;

/// <summary>CRC-32/MPEG-2 (poly 0x04C11DB7, init 0xFFFFFFFF, no reflection, no final XOR).</summary>
public static class Crc32Mpeg
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var t = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i << 24;
            for (int k = 0; k < 8; k++)
                c = (c & 0x80000000) != 0 ? (c << 1) ^ 0x04C11DB7 : c << 1;
            t[i] = c;
        }
        return t;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFF;
        foreach (var b in data)
            crc = (crc << 8) ^ Table[((crc >> 24) ^ b) & 0xFF];
        return crc;
    }

    /// <summary>A section including its trailing CRC is valid when the CRC over all of it is zero.</summary>
    public static bool IsValidSection(ReadOnlySpan<byte> sectionWithCrc) => Compute(sectionWithCrc) == 0;
}
