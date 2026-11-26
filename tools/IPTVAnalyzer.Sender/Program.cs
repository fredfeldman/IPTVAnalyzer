using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using IPTVAnalyzer.Core.Ts;

// Replays a .ts file to a multicast group, paced at the stream's PCR bitrate.
// Usage: IPTVAnalyzer.Sender <file.ts> <group:port> [--rate 8M] [--rtp] [--loop] [--ttl 1] [--interface 192.168.1.10] [--drop 0.001]

if (args.Length < 2 || args.Contains("-h") || args.Contains("--help"))
{
    Console.WriteLine("""
        IPTVAnalyzer.Sender - replay an MPEG-TS file to UDP/RTP multicast for testing

        Usage: IPTVAnalyzer.Sender <file.ts> <group:port> [options]
          --rate <bps>       Send rate, e.g. 6M or 3500k (default: PCR bitrate of the file)
          --rtp              Wrap in RTP (payload type 33) instead of raw UDP
          --loop             Repeat the file forever
          --ttl <n>          Multicast TTL (default 1)
          --interface <ip>   Local interface to send from
          --drop <p>         Randomly drop this fraction of datagrams (simulates loss)
        """);
    return 1;
}

var file = args[0];
var target = args[1];
double? rate = null;
bool rtp = false, loop = false;
int ttl = 1;
string? itf = null;
double drop = 0;
for (int i = 2; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--rate": rate = ParseRate(args[++i]); break;
        case "--rtp": rtp = true; break;
        case "--loop": loop = true; break;
        case "--ttl": ttl = int.Parse(args[++i]); break;
        case "--interface": itf = args[++i]; break;
        case "--drop": drop = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
        default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 1;
    }
}

int colon = target.LastIndexOf(':');
var ep = new IPEndPoint(IPAddress.Parse(target[..colon]), int.Parse(target[(colon + 1)..]));
var data = File.ReadAllBytes(file);
int start = Array.IndexOf(data, TsPacket.SyncByte);
if (start < 0) { Console.Error.WriteLine("No TS sync byte found"); return 1; }

if (rate == null)
{
    var a = new TsAnalyzer(new TsAnalyzerOptions { LiveTimeouts = false });
    a.Process(data, 0);
    rate = a.PcrBitrate;
    if (rate == null) { Console.Error.WriteLine("Could not determine PCR bitrate; pass --rate"); return 1; }
}

using var sock = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, ttl);
sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastLoopback, true);
if (itf != null) sock.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, IPAddress.Parse(itf).GetAddressBytes());

const int perDatagram = 7;
int payloadLen = perDatagram * TsPacket.Size;
var buf = new byte[12 + payloadLen];
ushort seq = (ushort)Random.Shared.Next();
uint ssrc = (uint)Random.Shared.Next();
double secondsPerDatagram = payloadLen * 8 / rate.Value;
var rnd = new Random();
var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

// When looping, continuity counters and PCRs are rewritten so the replay stays a continuous, valid stream
// (otherwise every wrap would show up as CC and PCR discontinuity errors).
const long PcrWrap = (1L << 33) * 300;
var scratch = new byte[payloadLen];
var ccState = new Dictionary<int, int>();
long pcrOffset = 0;
long loopDuration = PcrLoopDuration(data, start);

void Rewrite(Span<byte> p)
{
    if (p[0] != TsPacket.SyncByte) return;
    int pid = ((p[1] & 0x1F) << 8) | p[2];
    if (pid == TsPacket.NullPid) return;
    bool payload = (p[3] & 0x10) != 0;
    int cc = ccState.TryGetValue(pid, out var last) ? (payload ? (last + 1) & 0x0F : last) : p[3] & 0x0F;
    ccState[pid] = cc;
    p[3] = (byte)((p[3] & 0xF0) | cc);
    if (pcrOffset != 0 && (p[3] & 0x20) != 0 && p[4] >= 7 && (p[5] & 0x10) != 0)
    {
        long b = ((long)p[6] << 25) | ((long)p[7] << 17) | ((long)p[8] << 9) | ((long)p[9] << 1) | ((long)p[10] >> 7);
        long pcr = (b * 300 + (((p[10] & 1) << 8) | p[11]) + pcrOffset) % PcrWrap;
        b = pcr / 300;
        long ext = pcr % 300;
        p[6] = (byte)(b >> 25); p[7] = (byte)(b >> 17); p[8] = (byte)(b >> 9); p[9] = (byte)(b >> 1);
        p[10] = (byte)(((b & 1) << 7) | 0x7E | (ext >> 8)); p[11] = (byte)ext;
    }
}

Console.WriteLine($"Sending {file} to {ep} as {(rtp ? "RTP" : "UDP")} at {rate / 1e6:F2} Mb/s{(loop ? " (looping)" : "")}{(drop > 0 ? $", dropping {drop:P2}" : "")}. Ctrl+C to stop.");
var sw = Stopwatch.StartNew();
long sent = 0, dropped = 0;
var lastReport = sw.Elapsed;
do
{
    for (int off = start; off + payloadLen <= data.Length && !cts.IsCancellationRequested; off += payloadLen)
    {
        // Pace: wait until this datagram's scheduled time.
        double due = (sent + dropped) * secondsPerDatagram;
        while (sw.Elapsed.TotalSeconds < due)
        {
            double wait = due - sw.Elapsed.TotalSeconds;
            if (wait > 0.002) Thread.Sleep(1); else Thread.SpinWait(50);
        }
        int len;
        Buffer.BlockCopy(data, off, scratch, 0, payloadLen);
        for (int k = 0; k < perDatagram; k++) Rewrite(scratch.AsSpan(k * TsPacket.Size, TsPacket.Size));
        if (rtp)
        {
            buf[0] = 0x80; buf[1] = 33;
            buf[2] = (byte)(seq >> 8); buf[3] = (byte)seq;
            uint ts90 = (uint)(sw.Elapsed.TotalSeconds * 90000);
            buf[4] = (byte)(ts90 >> 24); buf[5] = (byte)(ts90 >> 16); buf[6] = (byte)(ts90 >> 8); buf[7] = (byte)ts90;
            buf[8] = (byte)(ssrc >> 24); buf[9] = (byte)(ssrc >> 16); buf[10] = (byte)(ssrc >> 8); buf[11] = (byte)ssrc;
            Buffer.BlockCopy(scratch, 0, buf, 12, payloadLen);
            len = 12 + payloadLen;
        }
        else
        {
            Buffer.BlockCopy(scratch, 0, buf, 0, payloadLen);
            len = payloadLen;
        }
        seq++;
        if (drop > 0 && rnd.NextDouble() < drop) { dropped++; continue; }
        sock.SendTo(buf, 0, len, SocketFlags.None, ep);
        sent++;
        if (sw.Elapsed - lastReport > TimeSpan.FromSeconds(5))
        {
            lastReport = sw.Elapsed;
            Console.WriteLine($"{sw.Elapsed:hh\\:mm\\:ss}  sent {sent:N0} datagrams, dropped {dropped:N0}");
        }
    }
    pcrOffset = (pcrOffset + loopDuration) % PcrWrap;
} while (loop && !cts.IsCancellationRequested);
Console.WriteLine($"Done: {sent:N0} datagrams sent, {dropped:N0} dropped.");
return 0;

// Duration of one pass in 27 MHz units: last PCR - first PCR on the first PCR PID, plus one average PCR interval.
static long PcrLoopDuration(byte[] data, int start)
{
    int pcrPid = -1;
    long first = -1, last = -1;
    int count = 0;
    for (int off = start; off + TsPacket.Size <= data.Length; off += TsPacket.Size)
    {
        var p = new TsPacket(data.AsSpan(off, TsPacket.Size));
        if (!p.HasSync || !p.HasPcr) continue;
        if (pcrPid < 0) pcrPid = p.Pid;
        if (p.Pid != pcrPid) continue;
        if (first < 0) first = p.Pcr;
        last = p.Pcr;
        count++;
    }
    if (count < 2) return 0;
    long span = last - first;
    return span + span / (count - 1);
}

static double ParseRate(string s)
{
    s = s.Trim().ToUpperInvariant();
    double mul = 1;
    if (s.EndsWith('M')) { mul = 1e6; s = s[..^1]; }
    else if (s.EndsWith('K')) { mul = 1e3; s = s[..^1]; }
    return double.Parse(s, CultureInfo.InvariantCulture) * mul;
}
