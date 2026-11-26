using System.Diagnostics;
using System.Net;
using System.Text;
using IPTVAnalyzer.Core.Common;
using IPTVAnalyzer.Core.Ts;

namespace IPTVAnalyzer.Core.Hls;

public enum CheckStatus { Pending, Ok, Warning, Error }

public sealed record CheckMessage(Severity Severity, string Text);

public sealed record VariantInfo(long Bandwidth, string? Resolution, string? Codecs, double? FrameRate, string Uri);

public sealed record ChannelCheckResult
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public string? Group { get; init; }
    public string? Logo { get; init; }
    public string Url { get; init; } = "";
    public CheckStatus Status { get; init; }
    public DateTimeOffset CheckedAt { get; init; }
    public double DurationMs { get; init; }
    public string Kind { get; init; } = "Unknown";
    public int? HttpStatus { get; init; }
    public double? ResponseMs { get; init; }
    public IReadOnlyList<VariantInfo> Variants { get; init; } = [];
    public VariantInfo? SelectedVariant { get; init; }
    public double? TargetDuration { get; init; }
    public int? SegmentCount { get; init; }
    public long? MediaSequence { get; init; }
    public bool? IsLive { get; init; }
    public string? Encryption { get; init; }
    public string? Container { get; init; }
    public double? SegmentDuration { get; init; }
    public long? SegmentBytes { get; init; }
    public double? SegmentDownloadMs { get; init; }
    /// <summary>Download time divided by media duration. Above 1 the source cannot be played in real time.</summary>
    public double? DownloadRatio { get; init; }
    public double? ThroughputBps { get; init; }
    public double? MediaBitrateBps { get; init; }
    public bool? PlaylistAdvancing { get; init; }
    public IReadOnlyList<string> Streams { get; init; } = [];
    public string? ServiceName { get; init; }
    public long? TsErrors { get; init; }
    public IReadOnlyList<CheckMessage> Messages { get; init; } = [];
}

public enum VariantSelection { Highest, Lowest, All }

public sealed class HlsCheckOptions
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public VariantSelection VariantSelection { get; init; } = VariantSelection.Highest;
    /// <summary>Re-fetch live media playlists to verify that they advance (adds about one target duration per check).</summary>
    public bool CheckLiveness { get; init; } = true;
    public double MaxLivenessWaitSeconds { get; init; } = 12;
    /// <summary>For direct MPEG-TS over HTTP: how much to read.</summary>
    public int TsSampleBytes { get; init; } = 2 * 1024 * 1024;
    public TimeSpan TsSampleTime { get; init; } = TimeSpan.FromSeconds(5);
    public int MaxSegmentBytes { get; init; } = 32 * 1024 * 1024;
    public double DownloadRatioWarn { get; init; } = 0.5;
    public double DownloadRatioError { get; init; } = 1.0;
}

/// <summary>Checks a single IPTV channel URL: HLS master/media playlists, segment delivery and content, or raw TS over HTTP.</summary>
public sealed class HlsChecker
{
    private readonly HttpClient _http;
    private readonly HlsCheckOptions _opt;

    public HlsChecker(HttpClient http, HlsCheckOptions? options = null)
    {
        _http = http;
        _opt = options ?? new HlsCheckOptions();
    }

    public static HttpClient CreateHttpClient(string? userAgent = null)
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 16,
        };
        var http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(string.IsNullOrWhiteSpace(userAgent) ? "IPTVAnalyzer/1.0" : userAgent);
        return http;
    }

    private sealed class Ctx
    {
        public readonly List<CheckMessage> Messages = [];
        public void Info(string t) => Messages.Add(new(Severity.Info, t));
        public void Warn(string t) => Messages.Add(new(Severity.Warning, t));
        public void Error(string t) => Messages.Add(new(Severity.Error, t));
    }

    public async Task<ChannelCheckResult> CheckAsync(M3uChannel channel, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var ctx = new Ctx();
        var r = new ChannelCheckResult
        {
            Index = channel.Index, Name = channel.Name, Group = channel.Group, Logo = channel.TvgLogo, Url = channel.Url,
            CheckedAt = DateTimeOffset.UtcNow,
        };
        try
        {
            if (!Uri.TryCreate(channel.Url, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https"))
            {
                ctx.Error($"Unsupported URL scheme (only http/https can be checked): {channel.Url}");
                return Finish(r, ctx, sw);
            }
            r = await CheckUrlAsync(r, uri, ctx, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { ctx.Error($"Timeout after {_opt.RequestTimeout.TotalSeconds:F0} s"); }
        catch (HttpRequestException ex) { ctx.Error($"HTTP failure: {ex.Message}"); }
        catch (Exception ex) { ctx.Error($"{ex.GetType().Name}: {ex.Message}"); }
        return Finish(r, ctx, sw);
    }

    private static ChannelCheckResult Finish(ChannelCheckResult r, Ctx ctx, Stopwatch sw)
    {
        var status = ctx.Messages.Any(m => m.Severity == Severity.Error) ? CheckStatus.Error
            : ctx.Messages.Any(m => m.Severity == Severity.Warning) ? CheckStatus.Warning : CheckStatus.Ok;
        return r with { Status = status, Messages = ctx.Messages.ToList(), DurationMs = Math.Round(sw.Elapsed.TotalMilliseconds) };
    }

    private CancellationTokenSource WithTimeout(CancellationToken ct)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_opt.RequestTimeout);
        return cts;
    }

    private async Task<ChannelCheckResult> CheckUrlAsync(ChannelCheckResult r, Uri uri, Ctx ctx, CancellationToken ct)
    {
        using var cts = WithTimeout(ct);
        var sw = Stopwatch.StartNew();
        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        double headersMs = sw.Elapsed.TotalMilliseconds;
        r = r with { HttpStatus = (int)resp.StatusCode, ResponseMs = Math.Round(headersMs) };
        if (!resp.IsSuccessStatusCode)
        {
            ctx.Error($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");
            return r;
        }
        var finalUri = resp.RequestMessage?.RequestUri ?? uri;
        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        await using var stream = await resp.Content.ReadAsStreamAsync(cts.Token);

        var head = new byte[512];
        int headLen = await ReadAtLeast(stream, head, head.Length, cts.Token);
        var headSpan = head.AsSpan(0, headLen);

        if (LooksLikeM3u(headSpan) || contentType.Contains("mpegurl", StringComparison.OrdinalIgnoreCase))
        {
            var text = await ReadText(stream, 4 * 1024 * 1024, cts.Token, head.AsMemory(0, headLen));
            if (!text.TrimStart((char)0xFEFF,' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                ctx.Error("Response is not a valid M3U8 playlist (missing #EXTM3U)");
                return r;
            }
            if (HlsPlaylistParser.IsMaster(text)) return await HandleMaster(r, text, finalUri, ctx, ct);
            return await HandleMedia(r with { Kind = "HLS media playlist" }, text, finalUri, ctx, ct);
        }

        if ((headLen > 0 && head[0] == TsPacket.SyncByte) || contentType.Contains("mp2t", StringComparison.OrdinalIgnoreCase))
            return await HandleRawTs(r, headSpan.ToArray(), stream, ctx, sw, ct);

        ctx.Error($"Unrecognized content (Content-Type '{contentType}', first bytes {Convert.ToHexString(headSpan[..Math.Min(8, headLen)])})");
        return r;
    }

    private async Task<ChannelCheckResult> HandleMaster(ChannelCheckResult r, string text, Uri baseUri, Ctx ctx, CancellationToken ct)
    {
        var master = HlsPlaylistParser.ParseMaster(text, baseUri);
        var variants = master.Variants.Select(v => new VariantInfo(v.Bandwidth, v.Resolution, v.Codecs, v.FrameRate, v.Uri)).ToList();
        r = r with { Kind = "HLS master playlist", Variants = variants };
        if (variants.Count == 0)
        {
            ctx.Error("Master playlist contains no variants");
            return r;
        }
        ctx.Info($"{variants.Count} variant(s): " + string.Join(", ", variants.OrderByDescending(v => v.Bandwidth)
            .Select(v => $"{v.Bandwidth / 1000} kb/s{(v.Resolution != null ? " " + v.Resolution : "")}")));
        if (master.Renditions.Count > 0)
            ctx.Info("Renditions: " + string.Join(", ", master.Renditions.Select(x => $"{x.Type} {x.Name ?? x.Language ?? x.GroupId}")));

        var ordered = variants.OrderByDescending(v => v.Bandwidth).ToList();
        if (_opt.VariantSelection == VariantSelection.All)
        {
            // Check every variant's playlist and segment quickly (without liveness wait), report the top one in detail.
            ChannelCheckResult? best = null;
            foreach (var v in ordered)
            {
                var sub = new Ctx();
                var vr = await CheckVariant(r with { SelectedVariant = v }, v, sub, ct, liveness: best == null);
                var label = $"[{v.Bandwidth / 1000} kb/s] ";
                foreach (var m in sub.Messages) ctx.Messages.Add(m with { Text = label + m.Text });
                best ??= vr;
            }
            return best!;
        }
        var chosen = _opt.VariantSelection == VariantSelection.Lowest ? ordered[^1] : ordered[0];
        return await CheckVariant(r with { SelectedVariant = chosen }, chosen, ctx, ct, liveness: true);
    }

    private async Task<ChannelCheckResult> CheckVariant(ChannelCheckResult r, VariantInfo v, Ctx ctx, CancellationToken ct, bool liveness)
    {
        var (status, text, finalUri) = await FetchText(new Uri(v.Uri), ct);
        if (status is < 200 or > 299 || text == null)
        {
            ctx.Error($"Variant playlist HTTP {status}: {v.Uri}");
            return r;
        }
        return await HandleMedia(r, text, finalUri, ctx, ct, liveness);
    }

    private async Task<ChannelCheckResult> HandleMedia(ChannelCheckResult r, string text, Uri baseUri, Ctx ctx, CancellationToken ct, bool liveness = true)
    {
        var media = HlsPlaylistParser.ParseMedia(text, baseUri);
        r = r with
        {
            TargetDuration = media.TargetDuration, SegmentCount = media.Segments.Count, MediaSequence = media.MediaSequence,
            IsLive = media.IsLive, Encryption = media.KeyMethod,
        };
        if (media.Segments.Count == 0)
        {
            ctx.Error("Media playlist has no segments");
            return r;
        }
        if (media.TargetDuration <= 0) ctx.Warn("Missing #EXT-X-TARGETDURATION");
        var tooLong = media.Segments.Where(s => Math.Round(s.Duration) > media.TargetDuration).ToList();
        if (media.TargetDuration > 0 && tooLong.Count > 0)
            ctx.Warn($"{tooLong.Count} segment(s) longer than target duration {media.TargetDuration} s (max {tooLong.Max(s => s.Duration):F2} s)");
        if (media.IsLive && media.TargetDuration > 0 && media.TotalDuration < 3 * media.TargetDuration)
            ctx.Warn($"Live window only {media.TotalDuration:F1} s (< 3 x target duration)");
        if (media.DiscontinuityCount > 0) ctx.Info($"{media.DiscontinuityCount} discontinuity marker(s) in playlist");
        ctx.Info($"{(media.IsLive ? "Live" : "VOD")}, {media.Segments.Count} segments, target {media.TargetDuration} s, sequence {media.MediaSequence}");

        // Live: newest segment shows whether the encoder is still producing; VOD: first segment.
        var seg = media.IsLive ? media.Segments[^1] : media.Segments[0];
        r = await CheckSegment(r, seg, media, ctx, ct);

        if (liveness && media.IsLive && _opt.CheckLiveness && media.TargetDuration > 0)
            r = await CheckAdvancing(r, baseUri, media, ctx, ct);
        return r;
    }

    private async Task<ChannelCheckResult> CheckSegment(ChannelCheckResult r, HlsSegment seg, HlsMediaPlaylist media, Ctx ctx, CancellationToken ct)
    {
        using var cts = WithTimeout(ct);
        var sw = Stopwatch.StartNew();
        using var resp = await _http.GetAsync(seg.Uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        if (!resp.IsSuccessStatusCode)
        {
            ctx.Error($"Segment HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {seg.Uri}");
            return r;
        }
        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
        var ms = new MemoryStream();
        var buf = new byte[81920];
        int n;
        while ((n = await s.ReadAsync(buf, cts.Token)) > 0)
        {
            ms.Write(buf, 0, n);
            if (ms.Length > _opt.MaxSegmentBytes) { ctx.Warn($"Segment larger than {_opt.MaxSegmentBytes / 1048576} MB, truncated"); break; }
        }
        double dlMs = sw.Elapsed.TotalMilliseconds;
        var data = ms.ToArray();
        double ratio = seg.Duration > 0 ? dlMs / 1000.0 / seg.Duration : 0;
        r = r with
        {
            SegmentDuration = seg.Duration, SegmentBytes = data.Length, SegmentDownloadMs = Math.Round(dlMs),
            DownloadRatio = seg.Duration > 0 ? Math.Round(ratio, 3) : null,
            ThroughputBps = dlMs > 0 ? Math.Round(data.Length * 8 / (dlMs / 1000.0)) : null,
            MediaBitrateBps = seg.Duration > 0 ? Math.Round(data.Length * 8 / seg.Duration) : null,
        };
        if (data.Length == 0) { ctx.Error("Segment is empty"); return r; }
        if (seg.Duration > 0)
        {
            if (ratio >= _opt.DownloadRatioError)
                ctx.Error($"Segment download took {dlMs / 1000:F2} s for {seg.Duration:F2} s of media (ratio {ratio:F2}); not real-time");
            else if (ratio >= _opt.DownloadRatioWarn)
                ctx.Warn($"Slow segment delivery: ratio {ratio:F2} ({dlMs / 1000:F2} s for {seg.Duration:F2} s)");
        }

        if (media.KeyMethod != null)
        {
            r = r with { Container = "Encrypted" };
            ctx.Info($"Segments encrypted ({media.KeyMethod}); content not inspected");
            return r;
        }

        if (data[0] == TsPacket.SyncByte)
        {
            r = r with { Container = "MPEG-TS" };
            return AnalyzeTs(r, data, ctx, isSegment: true);
        }
        if (media.MapUri != null || IsIsoBmff(data))
        {
            ctx.Info("Fragmented MP4 (CMAF) segments; TS-level analysis not applicable");
            return r with { Container = "fMP4" };
        }
        if (data.Length > 3 && data[0] == 'I' && data[1] == 'D' && data[2] == '3')
            return r with { Container = "Packed audio (ID3)" };
        if (data.Length > 1 && data[0] == 0xFF && (data[1] & 0xF0) == 0xF0)
            return r with { Container = "AAC (ADTS)" };
        ctx.Warn($"Unknown segment format (first bytes {Convert.ToHexString(data.AsSpan(0, Math.Min(8, data.Length)))})");
        return r;
    }

    private static ChannelCheckResult AnalyzeTs(ChannelCheckResult r, byte[] data, Ctx ctx, bool isSegment) =>
        AnalyzeTs(r, data, ctx, isSegment, out _);

    private static ChannelCheckResult AnalyzeTs(ChannelCheckResult r, byte[] data, Ctx ctx, bool isSegment, out double? pcrBitrate)
    {
        var analyzer = new TsAnalyzer(new TsAnalyzerOptions { LiveTimeouts = false });
        analyzer.Process(data, 0);
        var snap = analyzer.Snapshot();
        pcrBitrate = snap.PcrBitrateBps;
        var streams = snap.Programs.SelectMany(p => p.Streams)
            .Select(s => $"{s.Codec}{(s.Language != null ? $" [{s.Language}]" : "")} (PID {s.Pid})").ToList();
        var service = snap.Programs.Select(p => p.ServiceName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n));
        // Within one segment, only errors inside the data matter; the first packet per PID cannot be judged.
        long errs = snap.Errors[Tr101290.ContinuityCountError] + snap.Errors[Tr101290.TransportError] +
                    snap.Errors[Tr101290.CrcError] + snap.Errors[Tr101290.SyncByteError];
        r = r with { Streams = streams, ServiceName = service, TsErrors = errs };

        if (snap.TotalPackets == 0) { ctx.Error("No TS packets found"); return r; }
        if (data.Length % TsPacket.Size != 0 && isSegment) ctx.Warn($"Segment size {data.Length} is not a multiple of 188 bytes");
        if (snap.Programs.Count == 0) ctx.Error("No PAT found in TS data");
        else if (snap.Programs.All(p => p.Streams.Count == 0)) ctx.Error("No PMT found in TS data");
        if (!snap.Programs.SelectMany(p => p.Streams).Any(s => s.Kind == nameof(EsKind.Video))) ctx.Warn("No video stream");
        if (!snap.Programs.SelectMany(p => p.Streams).Any(s => s.Kind == nameof(EsKind.Audio))) ctx.Warn("No audio stream");
        if (snap.Programs.Any(p => p.Encrypted)) ctx.Warn("Content is scrambled (CA)");
        if (snap.Errors[Tr101290.ContinuityCountError] > 0) ctx.Error($"{snap.Errors[Tr101290.ContinuityCountError]} continuity error(s) inside TS data");
        if (snap.Errors[Tr101290.TransportError] > 0) ctx.Error($"{snap.Errors[Tr101290.TransportError]} packet(s) with transport error indicator");
        if (snap.Errors[Tr101290.CrcError] > 0) ctx.Error($"{snap.Errors[Tr101290.CrcError]} PSI CRC error(s)");
        if (snap.Errors[Tr101290.PcrDiscontinuityError] > 0) ctx.Warn($"{snap.Errors[Tr101290.PcrDiscontinuityError]} PCR discontinuity(ies)");
        if (snap.Errors[Tr101290.PcrRepetitionError] > 0) ctx.Warn($"PCR interval above 40 ms ({snap.Errors[Tr101290.PcrRepetitionError]}x)");
        if (streams.Count > 0) ctx.Info("Streams: " + string.Join(", ", streams));
        return r;
    }

    private async Task<ChannelCheckResult> CheckAdvancing(ChannelCheckResult r, Uri playlistUri, HlsMediaPlaylist first, Ctx ctx, CancellationToken ct)
    {
        double wait = Math.Min(_opt.MaxLivenessWaitSeconds, Math.Max(1, first.TargetDuration * 1.5));
        await Task.Delay(TimeSpan.FromSeconds(wait), ct);
        var (status, text, finalUri) = await FetchText(playlistUri, ct);
        if (status is < 200 or > 299 || text == null)
        {
            ctx.Error($"Playlist re-fetch failed with HTTP {status}");
            return r with { PlaylistAdvancing = false };
        }
        var second = HlsPlaylistParser.ParseMedia(text, finalUri);
        bool advanced = second.MediaSequence > first.MediaSequence ||
                        (second.Segments.Count > 0 && first.Segments.Count > 0 && second.Segments[^1].Uri != first.Segments[^1].Uri);
        if (!advanced)
            ctx.Error($"Live playlist did not advance within {wait:F1} s (media sequence stuck at {first.MediaSequence}); encoder may be stalled");
        else
            ctx.Info($"Playlist advancing (sequence {first.MediaSequence} -> {second.MediaSequence})");
        return r with { PlaylistAdvancing = advanced };
    }

    private async Task<ChannelCheckResult> HandleRawTs(ChannelCheckResult r, byte[] head, Stream stream, Ctx ctx, Stopwatch sw, CancellationToken ct)
    {
        r = r with { Kind = "MPEG-TS over HTTP", Container = "MPEG-TS" };
        var ms = new MemoryStream();
        ms.Write(head);
        var buf = new byte[65536];
        var readSw = Stopwatch.StartNew();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(_opt.TsSampleTime);
        try
        {
            int n;
            while (ms.Length < _opt.TsSampleBytes && (n = await stream.ReadAsync(buf, cts.Token)) > 0) ms.Write(buf, 0, n);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { /* sample time elapsed */ }
        double secs = readSw.Elapsed.TotalSeconds;
        var data = ms.ToArray();
        r = r with { SegmentBytes = data.Length, ThroughputBps = secs > 0 ? Math.Round(data.Length * 8 / secs) : null };
        if (data.Length < TsPacket.Size * 10) { ctx.Error($"Only {data.Length} bytes received in {secs:F1} s"); return r; }
        r = AnalyzeTs(r, data, ctx, isSegment: false, out var pcrRate);
        if (pcrRate is > 0)
        {
            r = r with { MediaBitrateBps = Math.Round(pcrRate.Value) };
            double ratio = (r.ThroughputBps ?? 0) / pcrRate.Value;
            // A live TS stream is delivered at its own rate, so throughput below the media rate means starvation.
            if (ratio < 0.9 && readSw.Elapsed >= _opt.TsSampleTime * 0.9)
                ctx.Warn($"Throughput {r.ThroughputBps / 1000:F0} kb/s is below the stream's PCR bitrate {pcrRate / 1000:F0} kb/s");
        }
        return r;
    }

    private async Task<(int Status, string? Text, Uri FinalUri)> FetchText(Uri uri, CancellationToken ct)
    {
        using var cts = WithTimeout(ct);
        using var resp = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var final = resp.RequestMessage?.RequestUri ?? uri;
        if (!resp.IsSuccessStatusCode) return ((int)resp.StatusCode, null, final);
        await using var s = await resp.Content.ReadAsStreamAsync(cts.Token);
        return ((int)resp.StatusCode, await ReadText(s, 4 * 1024 * 1024, cts.Token), final);
    }

    private static async Task<string> ReadText(Stream s, int max, CancellationToken ct, ReadOnlyMemory<byte> prefix = default)
    {
        using var ms = new MemoryStream();
        ms.Write(prefix.Span);
        var buf = new byte[16384];
        int n;
        while (ms.Length < max && (n = await s.ReadAsync(buf, ct)) > 0) ms.Write(buf, 0, n);
        return Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
    }

    private static async Task<int> ReadAtLeast(Stream s, byte[] buf, int want, CancellationToken ct)
    {
        int total = 0;
        while (total < want)
        {
            int n = await s.ReadAsync(buf.AsMemory(total, want - total), ct);
            if (n == 0) break;
            total += n;
            if (total >= 8 && (buf[0] == TsPacket.SyncByte || buf[0] == '#')) break; // enough to classify
        }
        return total;
    }

    private static bool LooksLikeM3u(ReadOnlySpan<byte> head)
    {
        int i = 0;
        if (head.Length >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF) i = 3;
        while (i < head.Length && (head[i] == ' ' || head[i] == '\r' || head[i] == '\n' || head[i] == '\t')) i++;
        return head.Length - i >= 7 && head.Slice(i, 7).SequenceEqual("#EXTM3U"u8);
    }

    private static bool IsIsoBmff(byte[] d)
    {
        if (d.Length < 8) return false;
        var type = Encoding.ASCII.GetString(d, 4, 4);
        return type is "ftyp" or "styp" or "moof" or "sidx" or "moov";
    }
}
