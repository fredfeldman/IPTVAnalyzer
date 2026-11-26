using System.Net;
using System.Text;
using IPTVAnalyzer.Core.Hls;

namespace IPTVAnalyzer.Tests;

public class HlsTests
{
    [Fact]
    public void M3u_ParsesAttributesAndTitlesWithCommas()
    {
        var text = """
            #EXTM3U x-tvg-url="http://epg"
            #EXTINF:-1 tvg-id="news.uk" tvg-name="News" tvg-logo="http://logo/n.png" group-title="News, UK",BBC News, HD
            http://example.com/news/index.m3u8
            #EXTINF:-1,Plain Channel
            #EXTGRP:Sports
            relative/sport.m3u8
            """;
        var ch = M3uParser.Parse(text, new Uri("http://host/lists/all.m3u"));
        Assert.Equal(2, ch.Count);
        Assert.Equal("BBC News, HD", ch[0].Name);
        Assert.Equal("News, UK", ch[0].Group);
        Assert.Equal("news.uk", ch[0].TvgId);
        Assert.Equal("http://logo/n.png", ch[0].TvgLogo);
        Assert.Equal("Plain Channel", ch[1].Name);
        Assert.Equal("Sports", ch[1].Group);
        Assert.Equal("http://host/lists/relative/sport.m3u8", ch[1].Url);
    }

    [Fact]
    public void Hls_ParsesMasterAndMedia()
    {
        var master = HlsPlaylistParser.ParseMaster("""
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=5000000,RESOLUTION=1920x1080,CODECS="avc1.640028,mp4a.40.2",FRAME-RATE=50
            hd/index.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=1200000,RESOLUTION=640x360
            sd/index.m3u8
            """, new Uri("http://cdn/ch1/master.m3u8"));
        Assert.Equal(2, master.Variants.Count);
        Assert.Equal("avc1.640028,mp4a.40.2", master.Variants[0].Codecs);
        Assert.Equal(50, master.Variants[0].FrameRate);
        Assert.Equal("http://cdn/ch1/hd/index.m3u8", master.Variants[0].Uri);

        var media = HlsPlaylistParser.ParseMedia("""
            #EXTM3U
            #EXT-X-VERSION:3
            #EXT-X-TARGETDURATION:6
            #EXT-X-MEDIA-SEQUENCE:100
            #EXTINF:6.000,
            s100.ts
            #EXT-X-DISCONTINUITY
            #EXTINF:5.960,
            s101.ts
            """, new Uri("http://cdn/ch1/hd/index.m3u8"));
        Assert.Equal(6, media.TargetDuration);
        Assert.Equal(100, media.MediaSequence);
        Assert.True(media.IsLive);
        Assert.Equal(2, media.Segments.Count);
        Assert.True(media.Segments[1].Discontinuity);
        Assert.Equal(101, media.Segments[1].Sequence);
        Assert.Equal("http://cdn/ch1/hd/s101.ts", media.Segments[1].Uri);
    }

    private sealed class FakeServer : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new();
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Interlocked.Increment(ref Requests);
            var resp = Routes.TryGetValue(request.RequestUri!.ToString(), out var f) ? f() : new HttpResponseMessage(HttpStatusCode.NotFound);
            resp.RequestMessage = request;
            return Task.FromResult(resp);
        }

        public void Text(string url, Func<string> body) =>
            Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body(), Encoding.UTF8, "application/vnd.apple.mpegurl") };

        public void Bytes(string url, byte[] body) =>
            Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) };
    }

    private static string Media(long seq) => $"""
        #EXTM3U
        #EXT-X-TARGETDURATION:1
        #EXT-X-MEDIA-SEQUENCE:{seq}
        #EXTINF:1.0,
        http://cdn/ch/s{seq}.ts
        #EXTINF:1.0,
        http://cdn/ch/s{seq + 1}.ts
        #EXTINF:1.0,
        http://cdn/ch/s{seq + 2}.ts
        """;

    private static (FakeServer, HlsChecker) Setup(bool advancing, HlsCheckOptions? opt = null)
    {
        var srv = new FakeServer();
        srv.Text("http://cdn/ch/master.m3u8", () => """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=2000000,RESOLUTION=1280x720
            http://cdn/ch/720.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=800000,RESOLUTION=640x360
            http://cdn/ch/360.m3u8
            """);
        long seq = 10;
        srv.Text("http://cdn/ch/720.m3u8", () => Media(advancing ? seq++ : seq));
        var segment = TsBuilder.Stream(1.0, "Channel One").ToBytes();
        for (int s = 10; s < 20; s++) srv.Bytes($"http://cdn/ch/s{s}.ts", segment);
        var http = new HttpClient(srv);
        return (srv, new HlsChecker(http, opt ?? new HlsCheckOptions { MaxLivenessWaitSeconds = 1 }));
    }

    private static M3uChannel Channel(string url) => new(0, "Ch", url, "Group", null, null, null, new Dictionary<string, string>());

    [Fact]
    public async Task Checker_HealthyLiveChannel_IsOk()
    {
        var (_, checker) = Setup(advancing: true);
        var r = await checker.CheckAsync(Channel("http://cdn/ch/master.m3u8"), CancellationToken.None);
        Assert.True(r.Status == CheckStatus.Ok, string.Join("\n", r.Messages.Select(m => $"{m.Severity}: {m.Text}")));
        Assert.Equal("HLS master playlist", r.Kind);
        Assert.Equal(2, r.Variants.Count);
        Assert.Equal(2000000, r.SelectedVariant!.Bandwidth);
        Assert.Equal("MPEG-TS", r.Container);
        Assert.Equal("Channel One", r.ServiceName);
        Assert.True(r.PlaylistAdvancing);
        Assert.Contains(r.Streams, s => s.StartsWith("H.264"));
        Assert.Equal(0, r.TsErrors);
    }

    [Fact]
    public async Task Checker_StalePlaylist_IsError()
    {
        var (_, checker) = Setup(advancing: false);
        var r = await checker.CheckAsync(Channel("http://cdn/ch/master.m3u8"), CancellationToken.None);
        Assert.Equal(CheckStatus.Error, r.Status);
        Assert.False(r.PlaylistAdvancing);
        Assert.Contains(r.Messages, m => m.Text.Contains("did not advance"));
    }

    [Fact]
    public async Task Checker_Http404_IsError()
    {
        var (_, checker) = Setup(advancing: true);
        var r = await checker.CheckAsync(Channel("http://cdn/missing.m3u8"), CancellationToken.None);
        Assert.Equal(CheckStatus.Error, r.Status);
        Assert.Equal(404, r.HttpStatus);
    }

    [Fact]
    public async Task Checker_RawTsOverHttp_IsAnalyzed()
    {
        var srv = new FakeServer();
        srv.Bytes("http://iptv/stream.ts", TsBuilder.Stream(2.0, "Raw TS").ToBytes());
        var checker = new HlsChecker(new HttpClient(srv));
        var r = await checker.CheckAsync(Channel("http://iptv/stream.ts"), CancellationToken.None);
        Assert.Equal("MPEG-TS over HTTP", r.Kind);
        Assert.Equal("Raw TS", r.ServiceName);
        Assert.NotNull(r.MediaBitrateBps);
    }

    [Fact]
    public async Task PlaylistMonitor_ChecksAllChannelsFromFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"iptvanalyzer-{Guid.NewGuid():n}.m3u");
        await File.WriteAllTextAsync(path, "#EXTM3U\n#EXTINF:-1 group-title=\"G\",A\nftp://unsupported/a\n#EXTINF:-1,B\nnot a url\n");
        try
        {
            using var mon = new PlaylistMonitor(new PlaylistConfig { Source = path });
            Assert.True(await mon.RunAsync(CancellationToken.None));
            var res = mon.Results();
            Assert.Equal(2, res.Count);
            Assert.All(res, r => Assert.Equal(CheckStatus.Error, r.Status));
            Assert.Equal(2, mon.Summary().Error);
        }
        finally { File.Delete(path); }
    }
}
