namespace IPTVAnalyzer.Core.Ts;

/// <summary>Zero-copy view over one 188-byte MPEG transport stream packet.</summary>
public readonly ref struct TsPacket
{
    public const int Size = 188;
    public const byte SyncByte = 0x47;
    public const int NullPid = 0x1FFF;

    private readonly ReadOnlySpan<byte> _data;

    public TsPacket(ReadOnlySpan<byte> data)
    {
        if (data.Length < Size) throw new ArgumentException("TS packet must be 188 bytes", nameof(data));
        _data = data[..Size];
    }

    public ReadOnlySpan<byte> Raw => _data;
    public bool HasSync => _data[0] == SyncByte;
    public bool TransportError => (_data[1] & 0x80) != 0;
    public bool PayloadUnitStart => (_data[1] & 0x40) != 0;
    public int Pid => ((_data[1] & 0x1F) << 8) | _data[2];
    public int ScramblingControl => (_data[3] >> 6) & 0x03;
    public bool HasAdaptationField => (_data[3] & 0x20) != 0;
    public bool HasPayload => (_data[3] & 0x10) != 0;
    public int ContinuityCounter => _data[3] & 0x0F;

    public int AdaptationFieldLength => HasAdaptationField ? _data[4] : 0;

    /// <summary>Discontinuity indicator from the adaptation field.</summary>
    public bool Discontinuity => HasAdaptationField && _data[4] > 0 && (_data[5] & 0x80) != 0;

    public bool HasPcr => HasAdaptationField && _data[4] >= 7 && (_data[5] & 0x10) != 0;

    /// <summary>PCR in 27 MHz units (base*300 + ext). Only valid when <see cref="HasPcr"/>.</summary>
    public long Pcr
    {
        get
        {
            long b = ((long)_data[6] << 25) | ((long)_data[7] << 17) | ((long)_data[8] << 9) |
                     ((long)_data[9] << 1) | ((long)_data[10] >> 7);
            long ext = ((_data[10] & 0x01) << 8) | _data[11];
            return b * 300 + ext;
        }
    }

    public ReadOnlySpan<byte> Payload
    {
        get
        {
            if (!HasPayload) return ReadOnlySpan<byte>.Empty;
            int offset = 4;
            if (HasAdaptationField) offset += 1 + _data[4];
            return offset >= Size ? ReadOnlySpan<byte>.Empty : _data[offset..];
        }
    }
}
