using System.Text;

namespace IPTVAnalyzer.Core.Ts;

public sealed record PatEntry(int ProgramNumber, int PmtPid);

public sealed record PmtStream(int Pid, int StreamType, string Codec, EsKind Kind, string? Language);

public sealed record PmtInfo(int ProgramNumber, int PcrPid, IReadOnlyList<PmtStream> Streams, bool HasCaDescriptor);

public sealed record SdtService(int ServiceId, string ProviderName, string ServiceName, int ServiceType, bool FreeCaMode);

/// <summary>Parsers for PAT, PMT and SDT sections. Callers must verify CRC first.</summary>
public static class PsiParser
{
    static PsiParser()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public const int TableIdPat = 0x00;
    public const int TableIdPmt = 0x02;
    public const int TableIdSdtActual = 0x42;

    public static int SectionVersion(ReadOnlySpan<byte> s) => (s[5] >> 1) & 0x1F;

    public static List<PatEntry> ParsePat(ReadOnlySpan<byte> s)
    {
        var list = new List<PatEntry>();
        int end = s.Length - 4;
        for (int i = 8; i + 4 <= end; i += 4)
        {
            int prog = (s[i] << 8) | s[i + 1];
            int pid = ((s[i + 2] & 0x1F) << 8) | s[i + 3];
            if (prog != 0) list.Add(new PatEntry(prog, pid)); // program 0 = NIT
        }
        return list;
    }

    public static PmtInfo ParsePmt(ReadOnlySpan<byte> s)
    {
        int programNumber = (s[3] << 8) | s[4];
        int pcrPid = ((s[8] & 0x1F) << 8) | s[9];
        int progInfoLen = ((s[10] & 0x0F) << 8) | s[11];
        int end = s.Length - 4;
        bool hasCa = ContainsTag(s.Slice(12, Math.Min(progInfoLen, Math.Max(0, end - 12))), 0x09);
        var streams = new List<PmtStream>();
        int i = 12 + progInfoLen;
        while (i + 5 <= end)
        {
            int type = s[i];
            int pid = ((s[i + 1] & 0x1F) << 8) | s[i + 2];
            int esInfoLen = ((s[i + 3] & 0x0F) << 8) | s[i + 4];
            int dStart = i + 5;
            int dLen = Math.Min(esInfoLen, end - dStart);
            var desc = dLen > 0 ? s.Slice(dStart, dLen) : ReadOnlySpan<byte>.Empty;
            var tags = new List<int>();
            string? lang = null;
            for (int d = 0; d + 2 <= desc.Length;)
            {
                int tag = desc[d], len = desc[d + 1];
                if (d + 2 + len > desc.Length) break;
                tags.Add(tag);
                if (tag == 0x09) hasCa = true;
                if ((tag == 0x0A || tag == 0x59 || tag == 0x56) && len >= 3)
                    lang ??= Encoding.ASCII.GetString(desc.Slice(d + 2, 3));
                d += 2 + len;
            }
            var (codec, kind) = StreamTypes.Describe(type, tags);
            streams.Add(new PmtStream(pid, type, codec, kind, lang));
            i = dStart + esInfoLen;
        }
        return new PmtInfo(programNumber, pcrPid, streams, hasCa);
    }

    public static List<SdtService> ParseSdt(ReadOnlySpan<byte> s)
    {
        var list = new List<SdtService>();
        int end = s.Length - 4;
        int i = 11;
        while (i + 5 <= end)
        {
            int serviceId = (s[i] << 8) | s[i + 1];
            bool freeCa = (s[i + 3] & 0x10) != 0;
            int loopLen = ((s[i + 3] & 0x0F) << 8) | s[i + 4];
            int dStart = i + 5;
            int dEnd = Math.Min(dStart + loopLen, end);
            string provider = "", name = "";
            int serviceType = 0;
            for (int d = dStart; d + 2 <= dEnd;)
            {
                int tag = s[d], len = s[d + 1];
                if (d + 2 + len > dEnd) break;
                if (tag == 0x48 && len >= 3)
                {
                    serviceType = s[d + 2];
                    int pLen = s[d + 3];
                    if (4 + pLen <= len + 2 && d + 4 + pLen <= dEnd)
                    {
                        provider = DecodeDvbString(s.Slice(d + 4, pLen));
                        int nPos = d + 4 + pLen;
                        if (nPos < dEnd)
                        {
                            int nLen = Math.Min(s[nPos], dEnd - nPos - 1);
                            name = DecodeDvbString(s.Slice(nPos + 1, nLen));
                        }
                    }
                }
                d += 2 + len;
            }
            list.Add(new SdtService(serviceId, provider, name, serviceType, freeCa));
            i = dStart + loopLen;
        }
        return list;
    }

    /// <summary>Decodes a DVB text string (EN 300 468 Annex A), covering the common character tables.</summary>
    public static string DecodeDvbString(ReadOnlySpan<byte> b)
    {
        if (b.IsEmpty) return "";
        Encoding enc;
        int skip = 0;
        byte first = b[0];
        try
        {
            if (first >= 0x20) enc = Encoding.GetEncoding("ISO-8859-1"); // default table ~ Latin
            else if (first >= 0x01 && first <= 0x0B) { enc = Encoding.GetEncoding($"ISO-8859-{first + 4}"); skip = 1; }
            else if (first == 0x10 && b.Length >= 3) { enc = Encoding.GetEncoding($"ISO-8859-{b[2]}"); skip = 3; }
            else if (first == 0x11) { enc = Encoding.BigEndianUnicode; skip = 1; }
            else if (first == 0x15) { enc = Encoding.UTF8; skip = 1; }
            else { enc = Encoding.GetEncoding("ISO-8859-1"); skip = 1; }
        }
        catch (ArgumentException)
        {
            enc = Encoding.GetEncoding("ISO-8859-1");
            skip = first < 0x20 ? 1 : 0;
        }
        var text = enc.GetString(b[skip..]);
        // Strip DVB control codes (emphasis on/off, CR/LF placeholders).
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            if (c >= 0x20 && !(c >= 0x80 && c <= 0x9F)) sb.Append(c);
        return sb.ToString().Trim();
    }

    private static bool ContainsTag(ReadOnlySpan<byte> desc, int wanted)
    {
        for (int d = 0; d + 2 <= desc.Length;)
        {
            if (desc[d] == wanted) return true;
            d += 2 + desc[d + 1];
        }
        return false;
    }
}
