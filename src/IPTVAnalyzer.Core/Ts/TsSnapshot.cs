namespace IPTVAnalyzer.Core.Ts;

public sealed record PidSnapshot(
    int Pid,
    string Description,
    string Kind,
    long Packets,
    double BitrateBps,
    long CcErrors,
    long TransportErrors,
    bool Scrambled,
    bool HasPcr,
    double? MaxPcrIntervalMs,
    double SecondsSinceSeen,
    bool Referenced);

public sealed record StreamSnapshot(int Pid, string Codec, string Kind, string? Language, double BitrateBps, bool Present);

public sealed record ProgramSnapshot(
    int ProgramNumber,
    int PmtPid,
    int PcrPid,
    string? ServiceName,
    string? ProviderName,
    bool Encrypted,
    IReadOnlyList<StreamSnapshot> Streams);

public sealed record TsSnapshot(
    long TotalPackets,
    long TotalBytes,
    double BitrateBps,
    double? PcrBitrateBps,
    bool InSync,
    int? TransportStreamId,
    IReadOnlyDictionary<string, long> Errors,
    long Priority1Errors,
    long Priority2Errors,
    long Priority3Errors,
    IReadOnlyList<ProgramSnapshot> Programs,
    IReadOnlyList<PidSnapshot> Pids);
