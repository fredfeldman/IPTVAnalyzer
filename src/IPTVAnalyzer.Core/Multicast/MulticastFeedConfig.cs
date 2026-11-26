using System.Net;

namespace IPTVAnalyzer.Core.Multicast;

public sealed class MulticastFeedConfig
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>Multicast group, e.g. 239.1.1.1.</summary>
    public string Group { get; set; } = "";
    public int Port { get; set; }
    /// <summary>Optional source address for source-specific multicast (IGMPv3 SSM).</summary>
    public string? Source { get; set; }
    /// <summary>Optional local interface IP to join on. Empty = OS default.</summary>
    public string? Interface { get; set; }
    /// <summary>Seconds without datagrams before the feed is flagged as "no data".</summary>
    public double NoDataTimeoutSeconds { get; set; } = 1.0;

    public static MulticastFeedConfig Parse(string address, string? name = null)
    {
        // Accepts "239.1.1.1:1234", "udp://239.1.1.1:1234", "rtp://10.0.0.1@239.1.1.1:1234"
        var s = address.Trim();
        int scheme = s.IndexOf("://", StringComparison.Ordinal);
        if (scheme >= 0) s = s[(scheme + 3)..];
        string? source = null;
        int at = s.IndexOf('@');
        if (at >= 0)
        {
            source = at > 0 ? s[..at] : null;
            s = s[(at + 1)..];
        }
        int colon = s.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(s[(colon + 1)..].TrimEnd('/'), out var port))
            throw new FormatException($"Expected group:port, got '{address}'");
        var cfg = new MulticastFeedConfig { Group = s[..colon], Port = port, Source = source, Name = name ?? "" };
        cfg.Validate();
        if (string.IsNullOrWhiteSpace(cfg.Name)) cfg.Name = $"{cfg.Group}:{cfg.Port}";
        return cfg;
    }

    public void Validate()
    {
        if (!IPAddress.TryParse(Group, out var g) || g.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
            throw new FormatException($"Invalid IPv4 multicast group '{Group}'");
        var first = g.GetAddressBytes()[0];
        if (first < 224 || first > 239) throw new FormatException($"'{Group}' is not a multicast address (224.0.0.0/4)");
        if (Port is < 1 or > 65535) throw new FormatException($"Invalid port {Port}");
        if (!string.IsNullOrWhiteSpace(Source) && !IPAddress.TryParse(Source, out _))
            throw new FormatException($"Invalid source address '{Source}'");
        if (!string.IsNullOrWhiteSpace(Interface) && !IPAddress.TryParse(Interface, out _))
            throw new FormatException($"Invalid interface address '{Interface}'");
    }

    public string Address => string.IsNullOrWhiteSpace(Source) ? $"{Group}:{Port}" : $"{Source}@{Group}:{Port}";
}
