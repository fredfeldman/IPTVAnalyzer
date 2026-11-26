namespace IPTVAnalyzer.Core.Ts;

/// <summary>
/// Reassembles PSI/SI sections carried on one PID (handles pointer_field, sections spanning
/// packets and several sections per packet).
/// </summary>
public sealed class SectionAssembler
{
    private readonly List<byte> _buffer = new(4096);
    private bool _synced;

    /// <summary>Feed the payload of one packet. Invokes <paramref name="onSection"/> for each complete section.</summary>
    public void Push(ReadOnlySpan<byte> payload, bool payloadUnitStart, Action<byte[]> onSection)
    {
        if (payload.IsEmpty) return;

        if (payloadUnitStart)
        {
            int pointer = payload[0];
            if (1 + pointer > payload.Length) { Reset(); return; }
            // Bytes before the pointer finish the previous section.
            if (_synced && pointer > 0)
            {
                Append(payload.Slice(1, pointer));
                Drain(onSection);
            }
            _buffer.Clear();
            _synced = true;
            Append(payload[(1 + pointer)..]);
        }
        else
        {
            if (!_synced) return;
            Append(payload);
        }
        Drain(onSection);
    }

    public void Reset()
    {
        _buffer.Clear();
        _synced = false;
    }

    private void Append(ReadOnlySpan<byte> data)
    {
        foreach (var b in data) _buffer.Add(b);
    }

    private void Drain(Action<byte[]> onSection)
    {
        while (_buffer.Count >= 3)
        {
            if (_buffer[0] == 0xFF) { _buffer.Clear(); return; } // stuffing: rest of packet is padding
            int length = ((_buffer[1] & 0x0F) << 8) | _buffer[2];
            int total = 3 + length;
            if (length > 4093) { Reset(); return; }
            if (_buffer.Count < total) return;
            var section = _buffer.GetRange(0, total).ToArray();
            _buffer.RemoveRange(0, total);
            onSection(section);
        }
    }
}
