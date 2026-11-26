namespace IPTVAnalyzer.Core.Ts;

/// <summary>ETSI TR 101 290 indicator names used as error counter keys.</summary>
public static class Tr101290
{
    // Priority 1
    public const string TsSyncLoss = "TS_sync_loss";
    public const string SyncByteError = "Sync_byte_error";
    public const string PatError = "PAT_error";
    public const string ContinuityCountError = "Continuity_count_error";
    public const string PmtError = "PMT_error";
    public const string PidError = "PID_error";

    // Priority 2
    public const string TransportError = "Transport_error";
    public const string CrcError = "CRC_error";
    public const string PcrRepetitionError = "PCR_repetition_error";
    public const string PcrDiscontinuityError = "PCR_discontinuity_indicator_error";
    public const string PtsError = "PTS_error";

    // Priority 3 (subset)
    public const string SdtError = "SDT_error";

    public static readonly string[] Priority1 = [TsSyncLoss, SyncByteError, PatError, ContinuityCountError, PmtError, PidError];
    public static readonly string[] Priority2 = [TransportError, CrcError, PcrRepetitionError, PcrDiscontinuityError, PtsError];
    public static readonly string[] Priority3 = [SdtError];

    public static int PriorityOf(string indicator) =>
        Priority1.Contains(indicator) ? 1 : Priority2.Contains(indicator) ? 2 : 3;
}
