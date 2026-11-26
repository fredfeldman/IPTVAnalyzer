namespace IPTVAnalyzer.Core.Ts;

public enum EsKind { Unknown, Video, Audio, Subtitle, Teletext, Data, Scte35 }

public static class StreamTypes
{
    public static (string Name, EsKind Kind) Describe(int streamType, IReadOnlyList<int>? descriptorTags = null)
    {
        switch (streamType)
        {
            case 0x01: return ("MPEG-1 Video", EsKind.Video);
            case 0x02: return ("MPEG-2 Video", EsKind.Video);
            case 0x03: return ("MPEG-1 Audio", EsKind.Audio);
            case 0x04: return ("MPEG-2 Audio", EsKind.Audio);
            case 0x0F: return ("AAC (ADTS)", EsKind.Audio);
            case 0x11: return ("AAC (LATM)", EsKind.Audio);
            case 0x1B: return ("H.264/AVC", EsKind.Video);
            case 0x24: return ("H.265/HEVC", EsKind.Video);
            case 0x33: return ("H.266/VVC", EsKind.Video);
            case 0x81: return ("AC-3", EsKind.Audio);
            case 0x87: return ("E-AC-3", EsKind.Audio);
            case 0x86: return ("SCTE-35", EsKind.Scte35);
            case 0x05: return ("Private sections", EsKind.Data);
            case 0x0B: case 0x0C: case 0x0D: return ("DSM-CC", EsKind.Data);
            case 0x15: return ("Metadata (PES)", EsKind.Data);
            case 0x06:
                if (descriptorTags != null)
                {
                    if (descriptorTags.Contains(0x6A)) return ("AC-3", EsKind.Audio);
                    if (descriptorTags.Contains(0x7A)) return ("E-AC-3", EsKind.Audio);
                    if (descriptorTags.Contains(0x7C)) return ("AAC", EsKind.Audio);
                    if (descriptorTags.Contains(0x59)) return ("DVB Subtitles", EsKind.Subtitle);
                    if (descriptorTags.Contains(0x56) || descriptorTags.Contains(0x46)) return ("Teletext", EsKind.Teletext);
                }
                return ("Private PES", EsKind.Data);
            default: return ($"Type 0x{streamType:X2}", EsKind.Unknown);
        }
    }
}
