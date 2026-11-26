using System.Text.RegularExpressions;

namespace IPTVAnalyzer.Core.Hls;

public sealed record M3uChannel(
    int Index,
    string Name,
    string Url,
    string? Group,
    string? TvgId,
    string? TvgName,
    string? TvgLogo,
    IReadOnlyDictionary<string, string> Attributes);

/// <summary>Parses IPTV style M3U/M3U8 channel lists (#EXTINF with tvg-* attributes).</summary>
public static partial class M3uParser
{
    [GeneratedRegex("""([A-Za-z0-9_\-]+)\s*=\s*"([^"]*)"|([A-Za-z0-9_\-]+)\s*=\s*([^\s,"]+)""")]
    private static partial Regex AttributeRegex();

    /// <summary>True when the text is itself an HLS playlist rather than a channel list.</summary>
    public static bool IsHlsPlaylist(string text) =>
        text.Contains("#EXT-X-TARGETDURATION", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("#EXT-X-STREAM-INF", StringComparison.OrdinalIgnoreCase);

    public static List<M3uChannel> Parse(string text, Uri? baseUri = null)
    {
        var result = new List<M3uChannel>();
        string? pendingTitle = null, pendingGroup = null;
        Dictionary<string, string>? pendingAttrs = null;

        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim().TrimStart((char)0xFEFF);
            if (line.Length == 0) continue;

            if (line.StartsWith("#EXTINF", StringComparison.OrdinalIgnoreCase))
            {
                (pendingTitle, pendingAttrs) = ParseExtInf(line);
                continue;
            }
            if (line.StartsWith("#EXTGRP:", StringComparison.OrdinalIgnoreCase))
            {
                pendingGroup = line[8..].Trim();
                continue;
            }
            if (line.StartsWith('#')) continue; // #EXTM3U, #EXTVLCOPT, comments

            var url = Resolve(line, baseUri);
            var attrs = pendingAttrs ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            attrs.TryGetValue("group-title", out var group);
            group ??= pendingGroup;
            attrs.TryGetValue("tvg-id", out var tvgId);
            attrs.TryGetValue("tvg-name", out var tvgName);
            attrs.TryGetValue("tvg-logo", out var logo);
            var name = !string.IsNullOrWhiteSpace(pendingTitle) ? pendingTitle! : tvgName ?? url;
            result.Add(new M3uChannel(result.Count, name, url, NullIfEmpty(group), NullIfEmpty(tvgId), NullIfEmpty(tvgName), NullIfEmpty(logo), attrs));
            pendingTitle = null;
            pendingAttrs = null;
            pendingGroup = null;
        }
        return result;
    }

    private static (string? Title, Dictionary<string, string> Attrs) ParseExtInf(string line)
    {
        var attrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int colon = line.IndexOf(':');
        if (colon < 0) return (null, attrs);
        var body = line[(colon + 1)..];

        // The title follows the first comma that is outside quotes.
        int titleComma = -1;
        bool inQuotes = false;
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '"') inQuotes = !inQuotes;
            else if (body[i] == ',' && !inQuotes) { titleComma = i; break; }
        }
        var head = titleComma >= 0 ? body[..titleComma] : body;
        var title = titleComma >= 0 ? body[(titleComma + 1)..].Trim() : null;

        foreach (Match m in AttributeRegex().Matches(head))
        {
            if (m.Groups[1].Success) attrs[m.Groups[1].Value] = m.Groups[2].Value;
            else attrs[m.Groups[3].Value] = m.Groups[4].Value;
        }
        return (title, attrs);
    }

    internal static string Resolve(string uri, Uri? baseUri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out var abs) && abs.Scheme != "file") return abs.ToString();
        if (baseUri != null && Uri.TryCreate(baseUri, uri, out var rel)) return rel.ToString();
        return uri;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
