namespace IPTVAnalyzer.Core.Multicast;

public readonly record struct RtpHeader(int PayloadType, int Sequence, uint Timestamp, uint Ssrc, int HeaderLength, int PaddingLength)
{
    /// <summary>
    /// Detects whether a datagram is RTP carrying MPEG-TS. Returns false for raw UDP TS
    /// (datagram starts with 0x47 and is a multiple of 188 bytes).
    /// </summary>
    public static bool TryParse(ReadOnlySpan<byte> d, out RtpHeader header)
    {
        header = default;
        if (d.Length < 12) return false;
        if (d[0] == 0x47 && d.Length % 188 == 0) return false;
        if ((d[0] >> 6) != 2) return false;
        int csrc = d[0] & 0x0F;
        int len = 12 + 4 * csrc;
        if ((d[0] & 0x10) != 0)
        {
            if (d.Length < len + 4) return false;
            len += 4 + ((d[len + 2] << 8) | d[len + 3]) * 4;
        }
        int pad = (d[0] & 0x20) != 0 ? d[^1] : 0;
        if (len + pad >= d.Length) return false;
        if (d[len] != 0x47) return false; // payload must be TS
        header = new RtpHeader(d[1] & 0x7F, (d[2] << 8) | d[3],
            (uint)((d[4] << 24) | (d[5] << 16) | (d[6] << 8) | d[7]),
            (uint)((d[8] << 24) | (d[9] << 16) | (d[10] << 8) | d[11]), len, pad);
        return true;
    }
}
