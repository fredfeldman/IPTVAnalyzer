using System.Text;
using IPTVAnalyzer.Core.Common;
using IPTVAnalyzer.Core.Multicast;
using IPTVAnalyzer.Core.Ts;

namespace IPTVAnalyzer.Tests;

public class TsAnalyzerTests
{
    private static TsAnalyzer Offline(EventLog? log = null) => new(new TsAnalyzerOptions { LiveTimeouts = false }, log);

    [Fact]
    public void Crc32Mpeg_MatchesCheckValue()
    {
        Assert.Equal(0x0376E6E7u, Crc32Mpeg.Compute(Encoding.ASCII.GetBytes("123456789")));
    }

    [Fact]
    public void CleanStream_ParsesPsiAndHasNoErrors()
    {
        var ts = TsBuilder.Stream(2.0, "News HD").ToBytes();
        var a = Offline();
        a.Process(ts, 0);
        var s = a.Snapshot();

        Assert.True(s.InSync);
        Assert.Equal(1, s.TransportStreamId);
        var prog = Assert.Single(s.Programs);
        Assert.Equal(1, prog.ProgramNumber);
        Assert.Equal(TsBuilder.PmtPid, prog.PmtPid);
        Assert.Equal(TsBuilder.VideoPid, prog.PcrPid);
        Assert.Equal("News HD", prog.ServiceName);
        Assert.Equal("Provider", prog.ProviderName);
        Assert.Collection(prog.Streams,
            v => { Assert.Equal("H.264/AVC", v.Codec); Assert.Equal("Video", v.Kind); },
            au => { Assert.Equal("AAC (ADTS)", au.Codec); Assert.Equal("Audio", au.Kind); });
        Assert.All(s.Errors, kv => Assert.True(kv.Value == 0, $"{kv.Key} = {kv.Value}"));

        var pid = s.Pids.Single(p => p.Pid == TsBuilder.VideoPid);
        Assert.True(pid.HasPcr);
        Assert.Equal(20, pid.MaxPcrIntervalMs!.Value, 1);
    }

    [Fact]
    public void PcrBitrate_MatchesPacketRate()
    {
        var b = TsBuilder.Stream(3.0);
        var a = Offline();
        a.Process(b.ToBytes(), 0);
        double expected = b.Packets.Count * 188 * 8 / 3.0;
        Assert.InRange(a.PcrBitrate!.Value, expected * 0.97, expected * 1.03);
    }

    [Fact]
    public void DroppedPacket_IsContinuityError()
    {
        var b = TsBuilder.Stream(1.0);
        var packets = b.Packets.ToList();
        // remove a video payload packet in the middle
        int idx = packets.FindIndex(200, p => new TsPacket(p).Pid == TsBuilder.VideoPid && new TsPacket(p).HasPayload);
        packets.RemoveAt(idx);
        var log = new EventLog();
        var a = Offline(log);
        a.Process(packets.SelectMany(p => p).ToArray(), 0);
        var s = a.Snapshot();
        Assert.Equal(1, s.Errors[Tr101290.ContinuityCountError]);
        Assert.Equal(1, s.Pids.Single(p => p.Pid == TsBuilder.VideoPid).CcErrors);
        Assert.Contains(log.Snapshot(), e => e.Code == Tr101290.ContinuityCountError);
    }

    [Fact]
    public void DuplicatePacket_IsAllowedOnce()
    {
        var packets = TsBuilder.Stream(1.0).Packets.ToList();
        int idx = packets.FindIndex(200, p => new TsPacket(p).Pid == TsBuilder.VideoPid && new TsPacket(p).HasPayload);
        packets.Insert(idx, packets[idx]);
        var a = Offline();
        a.Process(packets.SelectMany(p => p).ToArray(), 0);
        Assert.Equal(0, a.Snapshot().Errors[Tr101290.ContinuityCountError]);
    }

    [Fact]
    public void ArbitraryChunking_GivesSameResult()
    {
        var ts = TsBuilder.Stream(1.0).ToBytes();
        var whole = Offline();
        whole.Process(ts, 0);

        var chunked = Offline();
        var rnd = new Random(42);
        for (int off = 0; off < ts.Length;)
        {
            int n = Math.Min(rnd.Next(1, 1000), ts.Length - off);
            chunked.Process(ts.AsSpan(off, n), 0);
            off += n;
        }
        var a = whole.Snapshot();
        var b = chunked.Snapshot();
        Assert.Equal(a.TotalPackets, b.TotalPackets);
        Assert.Equal(a.Programs.Single().ServiceName, b.Programs.Single().ServiceName);
        Assert.Equal(0, b.Priority1Errors + b.Priority2Errors);
    }

    [Fact]
    public void GarbagePrefix_ResyncsAndAnalyzes()
    {
        var garbage = new byte[1000];
        new Random(1).NextBytes(garbage);
        for (int i = 0; i < garbage.Length; i++) if (garbage[i] == 0x47) garbage[i] = 0;
        var ts = garbage.Concat(TsBuilder.Stream(1.0).ToBytes()).ToArray();
        var a = Offline();
        a.Process(ts, 0);
        var s = a.Snapshot();
        Assert.True(s.InSync);
        Assert.Single(s.Programs);
    }

    [Fact]
    public void CorruptedSyncBytes_CauseSyncLoss()
    {
        var packets = TsBuilder.Stream(1.0).Packets.Select(p => (byte[])p.Clone()).ToList();
        packets[100][0] = 0x00;
        packets[101][0] = 0x00;
        var a = Offline();
        a.Process(packets.SelectMany(p => p).ToArray(), 0);
        var s = a.Snapshot();
        Assert.Equal(2, s.Errors[Tr101290.SyncByteError]);
        Assert.Equal(1, s.Errors[Tr101290.TsSyncLoss]);
        Assert.True(s.InSync); // regained afterwards
    }

    [Fact]
    public void CorruptedPat_IsCrcError()
    {
        var packets = TsBuilder.Stream(1.0).Packets.Select(p => (byte[])p.Clone()).ToList();
        var pat = packets.First(p => new TsPacket(p).Pid == 0);
        pat[10] ^= 0xFF;
        var a = Offline();
        a.Process(packets.SelectMany(p => p).ToArray(), 0);
        Assert.Equal(1, a.Snapshot().Errors[Tr101290.CrcError]);
    }

    [Fact]
    public void TransportErrorIndicator_IsCounted()
    {
        var packets = TsBuilder.Stream(1.0).Packets.Select(p => (byte[])p.Clone()).ToList();
        packets[50][1] |= 0x80;
        var a = Offline();
        a.Process(packets.SelectMany(p => p).ToArray(), 0);
        Assert.Equal(1, a.Snapshot().Errors[Tr101290.TransportError]);
    }

    [Fact]
    public void PcrGap_IsRepetitionError()
    {
        var packets = TsBuilder.Stream(1.0).Packets.ToList();
        // Remove two consecutive PCR packets -> 60 ms interval
        var pcrIdx = packets.Select((p, i) => (p, i)).Where(x => new TsPacket(x.p).HasPcr).Select(x => x.i).Skip(10).Take(2).ToList();
        for (int k = pcrIdx.Count - 1; k >= 0; k--) packets.RemoveAt(pcrIdx[k]);
        var a = Offline();
        a.Process(packets.SelectMany(p => p).ToArray(), 0);
        var s = a.Snapshot();
        Assert.Equal(1, s.Errors[Tr101290.PcrRepetitionError]);
        Assert.Equal(0, s.Errors[Tr101290.ContinuityCountError]); // adaptation-only packets do not affect CC
    }

    [Fact]
    public void LiveTimeouts_FlagMissingPat()
    {
        var a = new TsAnalyzer(new TsAnalyzerOptions { LiveTimeouts = true });
        a.Process(TsBuilder.Stream(0.5).ToBytes(), 0);
        a.Tick(1.2); // PAT last seen at t=0, more than 0.5 s ago
        Assert.True(a.Snapshot().Errors[Tr101290.PatError] >= 1);
    }

    [Fact]
    public void Rtp_IsDetectedAndParsed()
    {
        var payload = TsBuilder.Stream(0.1).Packets.Take(7).SelectMany(p => p).ToArray();
        var d = new byte[12 + payload.Length];
        d[0] = 0x80; d[1] = 33; d[2] = 0x12; d[3] = 0x34;
        payload.CopyTo(d, 12);
        Assert.True(RtpHeader.TryParse(d, out var h));
        Assert.Equal(33, h.PayloadType);
        Assert.Equal(0x1234, h.Sequence);
        Assert.Equal(12, h.HeaderLength);
        Assert.False(RtpHeader.TryParse(payload, out _)); // raw UDP TS
    }

    [Fact]
    public void Monitor_CountsRtpLoss_FromDatagrams()
    {
        var mon = new MulticastMonitor(new MulticastFeedConfig { Group = "239.1.1.1", Port = 1234 });
        var packets = TsBuilder.Stream(1.0).Packets;
        int seq = 0;
        double t = 0;
        for (int i = 0; i + 7 <= packets.Count; i += 7, seq++, t += 0.005)
        {
            if (seq == 20) continue; // lose one datagram
            var d = new byte[12 + 7 * 188];
            d[0] = 0x80; d[1] = 33; d[2] = (byte)(seq >> 8); d[3] = (byte)seq;
            for (int k = 0; k < 7; k++) packets[i + k].CopyTo(d, 12 + k * 188);
            mon.OnDatagram(d, t);
        }
        var s = mon.Snapshot(true);
        Assert.Equal("RTP", s.Transport);
        Assert.Equal(1, s.RtpLost);
        Assert.True(s.Ts.Errors[Tr101290.ContinuityCountError] >= 1);
    }

    [Fact]
    public void Mdi_DelayFactor_SmallForSmoothAndLargeForBursty()
    {
        var smooth = new MdiCalculator();
        for (int i = 0; i < 400; i++) smooth.OnDatagram(i * 0.01, 1316); // 100 datagrams/s for 4 s
        Assert.True(smooth.HasValue);
        Assert.InRange(smooth.DelayFactorMs, 0, 15);

        var bursty = new MdiCalculator();
        for (int sec = 0; sec < 4; sec++)
            for (int k = 0; k < 100; k++) bursty.OnDatagram(sec + (k < 50 ? 0.0001 * k : 0.5 + 0.0001 * k), 1316); // two bursts per second
        Assert.True(bursty.DelayFactorMs > 200, $"DF {bursty.DelayFactorMs}");
    }

    [Theory]
    [InlineData("239.1.1.1:1234", "239.1.1.1", 1234, null)]
    [InlineData("udp://239.2.2.2:5000", "239.2.2.2", 5000, null)]
    [InlineData("rtp://10.0.0.5@232.1.1.1:5004", "232.1.1.1", 5004, "10.0.0.5")]
    [InlineData("udp://@239.3.3.3:1234", "239.3.3.3", 1234, null)]
    public void FeedConfig_ParsesAddresses(string input, string group, int port, string? source)
    {
        var c = MulticastFeedConfig.Parse(input);
        Assert.Equal(group, c.Group);
        Assert.Equal(port, c.Port);
        Assert.Equal(source, c.Source);
    }

    [Fact]
    public void FeedConfig_RejectsUnicast()
    {
        Assert.Throws<FormatException>(() => MulticastFeedConfig.Parse("192.168.1.1:1234"));
    }
}
