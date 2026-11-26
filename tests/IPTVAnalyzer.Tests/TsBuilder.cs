using System.Text;
using IPTVAnalyzer.Core.Ts;

namespace IPTVAnalyzer.Tests;

/// <summary>Builds small synthetic but standards-conformant transport streams for tests.</summary>
public sealed class TsBuilder
{
    public const int PmtPid = 0x1000, VideoPid = 0x0100, AudioPid = 0x0101;
    private readonly Dictionary<int, int> _cc = new();
    private readonly List<byte[]> _packets = [];

    public IReadOnlyList<byte[]> Packets => _packets;
    public byte[] ToBytes() => _packets.SelectMany(p => p).ToArray();

    /// <summary>
    /// Builds <paramref name="seconds"/> of a stream: PSI every 100 ms, video PID with PCR every 20 ms and a PES
    /// header (PTS) every 40 ms, audio PES every 40 ms. Bitrate ≈ packetsPerMs * 188 * 8 * 1000.
    /// </summary>
    public static TsBuilder Stream(double seconds, string serviceName = "Test Channel", int videoPacketsPer20Ms = 20)
    {
        var b = new TsBuilder();
        long pcrBase = 0;
        long pts = 90000;
        int steps = (int)(seconds * 50);
        for (int i = 0; i < steps; i++)
        {
            if (i % 5 == 0)
            {
                b.AddSection(0, Pat(1, PmtPid));
                b.AddSection(PmtPid, Pmt(1, VideoPid, (0x1B, VideoPid), (0x0F, AudioPid)));
                b.AddSection(0x11, Sdt(1, 1, "Provider", serviceName));
            }
            // PCR packet (adaptation only)
            b.AddPcrPacket(VideoPid, pcrBase * 300);
            for (int k = 0; k < videoPacketsPer20Ms; k++)
            {
                bool pesStart = k == 0 && i % 2 == 0;
                b.AddPayloadPacket(VideoPid, pesStart ? Pes(0xE0, pts + i * 1800) : Filler(184), pesStart);
            }
            if (i % 2 == 0) b.AddPayloadPacket(AudioPid, Pes(0xC0, pts + i * 1800), true);
            pcrBase += 1800; // 20 ms in 90 kHz
        }
        return b;
    }

    public void AddPayloadPacket(int pid, byte[] payload, bool pusi)
    {
        var p = new byte[188];
        p[0] = 0x47;
        p[1] = (byte)((pusi ? 0x40 : 0) | (pid >> 8));
        p[2] = (byte)pid;
        int cc = NextCc(pid);
        int len = Math.Min(payload.Length, 184);
        if (len < 184)
        {
            // pad with adaptation field stuffing
            int afLen = 184 - len - 1;
            p[3] = (byte)(0x30 | cc);
            p[4] = (byte)afLen;
            if (afLen > 0) { p[5] = 0; for (int i = 6; i < 5 + afLen; i++) p[i] = 0xFF; }
            Array.Copy(payload, 0, p, 5 + afLen, len);
        }
        else
        {
            p[3] = (byte)(0x10 | cc);
            Array.Copy(payload, 0, p, 4, 184);
        }
        _packets.Add(p);
    }

    public void AddPcrPacket(int pid, long pcr27)
    {
        var p = new byte[188];
        p[0] = 0x47;
        p[1] = (byte)(pid >> 8);
        p[2] = (byte)pid;
        int cc = _cc.GetValueOrDefault(pid, 15); // adaptation-only: CC does not increment
        p[3] = (byte)(0x20 | cc);
        p[4] = 183;
        p[5] = 0x10;
        long b = pcr27 / 300, ext = pcr27 % 300;
        p[6] = (byte)(b >> 25); p[7] = (byte)(b >> 17); p[8] = (byte)(b >> 9); p[9] = (byte)(b >> 1);
        p[10] = (byte)(((b & 1) << 7) | 0x7E | (ext >> 8)); p[11] = (byte)ext;
        for (int i = 12; i < 188; i++) p[i] = 0xFF;
        _packets.Add(p);
    }

    public void AddSection(int pid, byte[] section)
    {
        var payload = new byte[1 + section.Length];
        payload[0] = 0; // pointer field
        section.CopyTo(payload, 1);
        for (int off = 0; off < payload.Length; off += 184)
        {
            var chunk = payload.AsSpan(off, Math.Min(184, payload.Length - off)).ToArray();
            if (chunk.Length < 184) chunk = [.. chunk, .. Enumerable.Repeat((byte)0xFF, 184 - chunk.Length)];
            AddPayloadPacket(pid, chunk, off == 0);
        }
    }

    private int NextCc(int pid)
    {
        int cc = (_cc.GetValueOrDefault(pid, 15) + 1) & 0x0F;
        _cc[pid] = cc;
        return cc;
    }

    public static byte[] Filler(int n) => Enumerable.Repeat((byte)0xAB, n).ToArray();

    public static byte[] Pes(int streamId, long pts)
    {
        var p = new byte[184];
        p[0] = 0; p[1] = 0; p[2] = 1; p[3] = (byte)streamId;
        p[4] = 0; p[5] = 0;
        p[6] = 0x80; p[7] = 0x80; p[8] = 5;
        p[9] = (byte)(0x21 | ((pts >> 29) & 0x0E));
        p[10] = (byte)(pts >> 22);
        p[11] = (byte)(0x01 | ((pts >> 14) & 0xFE));
        p[12] = (byte)(pts >> 7);
        p[13] = (byte)(0x01 | ((pts << 1) & 0xFE));
        for (int i = 14; i < p.Length; i++) p[i] = 0xAB;
        return p;
    }

    public static byte[] Pat(int program, int pmtPid) =>
        LongSection(0x00, 1, [(byte)(program >> 8), (byte)program, (byte)(0xE0 | (pmtPid >> 8)), (byte)pmtPid]);

    public static byte[] Pmt(int program, int pcrPid, params (int Type, int Pid)[] streams)
    {
        var body = new List<byte> { (byte)(0xE0 | (pcrPid >> 8)), (byte)pcrPid, 0xF0, 0x00 };
        foreach (var (type, pid) in streams)
            body.AddRange([(byte)type, (byte)(0xE0 | (pid >> 8)), (byte)pid, 0xF0, 0x00]);
        return LongSection(0x02, program, body.ToArray());
    }

    public static byte[] Sdt(int tsId, int serviceId, string provider, string name)
    {
        var p = Encoding.ASCII.GetBytes(provider);
        var n = Encoding.ASCII.GetBytes(name);
        var desc = new List<byte> { 0x48, (byte)(3 + p.Length + n.Length), 0x01, (byte)p.Length };
        desc.AddRange(p);
        desc.Add((byte)n.Length);
        desc.AddRange(n);
        var body = new List<byte> { 0x00, 0x01, 0xFF }; // original_network_id, reserved
        body.AddRange([(byte)(serviceId >> 8), (byte)serviceId, 0xFC, (byte)(0x80 | (desc.Count >> 8)), (byte)desc.Count]);
        body.AddRange(desc);
        return LongSection(0x42, tsId, body.ToArray());
    }

    /// <summary>Long-form PSI section with version 0, section 0/0 and a valid CRC.</summary>
    public static byte[] LongSection(int tableId, int idExt, byte[] body)
    {
        int length = 5 + body.Length + 4;
        var s = new List<byte>
        {
            (byte)tableId, (byte)(0xB0 | (length >> 8)), (byte)length,
            (byte)(idExt >> 8), (byte)idExt, 0xC1, 0x00, 0x00,
        };
        s.AddRange(body);
        uint crc = Crc32Mpeg.Compute(s.ToArray());
        s.AddRange([(byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
        return s.ToArray();
    }
}
