using System.Globalization;
using System.Text.RegularExpressions;

namespace IPTVAnalyzer.Core.Hls;

public sealed record HlsVariant(string Uri, long Bandwidth, long? AverageBandwidth, string? Resolution, string? Codecs, double? FrameRate, string? Audio);

public sealed record HlsRendition(string Type, string? GroupId, string? Name, string? Language, string? Uri, bool Default);

public sealed record HlsSegment(string Uri, double Duration, long Sequence, bool Discontinuity, string? Title);

public sealed class HlsMasterPlaylist
{
    public List<HlsVariant> Variants { get; } = [];
    public List<HlsRendition> Renditions { get; } = [];
}

public sealed class HlsMediaPlaylist
{
    public int Version { get; set; } = 1;
    public double TargetDuration { get; set; }
    public long MediaSequence { get; set; }
    public bool EndList { get; set; }
    public string? PlaylistType { get; set; }
    public string? KeyMethod { get; set; }
    public string? MapUri { get; set; }
    public int DiscontinuityCount { get; set; }
    public List<HlsSegment> Segments { get; } = [];
    public bool IsLive => !EndList && !string.Equals(PlaylistType, "VOD", StringComparison.OrdinalIgnoreCase);
    public double TotalDuration => Segments.Sum(s => s.Duration);
}

/// <summary>Parser for HLS (RFC 8216) master and media playlists.</summary>
public static partial class HlsPlaylistParser
{
    [GeneratedRegex("""([A-Z0-9\-]+)=("[^"]*"|[^,]*)""")]
    private static partial Regex AttrRegex();

    public static bool IsMaster(string text) => text.Contains("#EXT-X-STREAM-INF", StringComparison.Ordinal);

    public static Dictionary<string, string> ParseAttributes(string list)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in AttrRegex().Matches(list))
            d[m.Groups[1].Value] = m.Groups[2].Value.Trim('"');
        return d;
    }

    public static HlsMasterPlaylist ParseMaster(string text, Uri? baseUri)
    {
        var p = new HlsMasterPlaylist();
        Dictionary<string, string>? pending = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
            {
                pending = ParseAttributes(line[18..]);
            }
            else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal))
            {
                var a = ParseAttributes(line[13..]);
                a.TryGetValue("URI", out var uri);
                p.Renditions.Add(new HlsRendition(a.GetValueOrDefault("TYPE", "?"), a.GetValueOrDefault("GROUP-ID"),
                    a.GetValueOrDefault("NAME"), a.GetValueOrDefault("LANGUAGE"),
                    uri != null ? M3uParser.Resolve(uri, baseUri) : null,
                    string.Equals(a.GetValueOrDefault("DEFAULT"), "YES", StringComparison.OrdinalIgnoreCase)));
            }
            else if (!line.StartsWith('#') && pending != null)
            {
                long.TryParse(pending.GetValueOrDefault("BANDWIDTH"), out var bw);
                long? avg = long.TryParse(pending.GetValueOrDefault("AVERAGE-BANDWIDTH"), out var a) ? a : null;
                double? fr = double.TryParse(pending.GetValueOrDefault("FRAME-RATE"), NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : null;
                p.Variants.Add(new HlsVariant(M3uParser.Resolve(line, baseUri), bw, avg, pending.GetValueOrDefault("RESOLUTION"),
                    pending.GetValueOrDefault("CODECS"), fr, pending.GetValueOrDefault("AUDIO")));
                pending = null;
            }
        }
        return p;
    }

    public static HlsMediaPlaylist ParseMedia(string text, Uri? baseUri)
    {
        var p = new HlsMediaPlaylist();
        double? pendingDuration = null;
        string? pendingTitle = null;
        bool pendingDisc = false;
        long seq = 0;
        bool seqSet = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-VERSION:", StringComparison.Ordinal))
                p.Version = int.TryParse(line[15..], out var v) ? v : 1;
            else if (line.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal))
                p.TargetDuration = ParseDouble(line[22..]);
            else if (line.StartsWith("#EXT-X-MEDIA-SEQUENCE:", StringComparison.Ordinal))
            {
                p.MediaSequence = long.TryParse(line[22..], out var s) ? s : 0;
                if (!seqSet) { seq = p.MediaSequence; seqSet = true; }
            }
            else if (line.StartsWith("#EXT-X-ENDLIST", StringComparison.Ordinal)) p.EndList = true;
            else if (line.StartsWith("#EXT-X-PLAYLIST-TYPE:", StringComparison.Ordinal)) p.PlaylistType = line[21..].Trim();
            else if (line.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal))
            {
                var method = ParseAttributes(line[11..]).GetValueOrDefault("METHOD");
                p.KeyMethod = string.Equals(method, "NONE", StringComparison.OrdinalIgnoreCase) ? null : method;
            }
            else if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var uri = ParseAttributes(line[11..]).GetValueOrDefault("URI");
                if (uri != null) p.MapUri = M3uParser.Resolve(uri, baseUri);
            }
            else if (line.StartsWith("#EXT-X-DISCONTINUITY", StringComparison.Ordinal) && !line.StartsWith("#EXT-X-DISCONTINUITY-SEQUENCE", StringComparison.Ordinal))
            {
                pendingDisc = true;
                p.DiscontinuityCount++;
            }
            else if (line.StartsWith("#EXTINF:", StringComparison.Ordinal))
            {
                var body = line[8..];
                int comma = body.IndexOf(',');
                pendingDuration = ParseDouble(comma >= 0 ? body[..comma] : body);
                pendingTitle = comma >= 0 ? body[(comma + 1)..].Trim() : null;
            }
            else if (!line.StartsWith('#'))
            {
                p.Segments.Add(new HlsSegment(M3uParser.Resolve(line, baseUri), pendingDuration ?? 0, seq++, pendingDisc,
                    string.IsNullOrEmpty(pendingTitle) ? null : pendingTitle));
                pendingDuration = null;
                pendingTitle = null;
                pendingDisc = false;
            }
        }
        return p;
    }

    private static double ParseDouble(string s) =>
        double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
}
