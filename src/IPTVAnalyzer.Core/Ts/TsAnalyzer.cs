using IPTVAnalyzer.Core.Common;

namespace IPTVAnalyzer.Core.Ts;

public sealed class TsAnalyzerOptions
{
    /// <summary>Enable wall-clock based checks (PAT/PMT/PID/SDT timeouts). Use for live streams only.</summary>
    public bool LiveTimeouts { get; init; } = true;
    public double PatTimeoutSeconds { get; init; } = 0.5;
    public double PmtTimeoutSeconds { get; init; } = 0.5;
    public double PidTimeoutSeconds { get; init; } = 5.0;
    public double SdtTimeoutSeconds { get; init; } = 2.0;
    public double PcrRepetitionMaxMs { get; init; } = 40;
    public double PcrDiscontinuityMaxMs { get; init; } = 100;
    public double PtsRepetitionMaxMs { get; init; } = 700;
    public double BitrateWindowSeconds { get; init; } = 1.0;
}

/// <summary>
/// Incremental MPEG-TS analyzer: PSI (PAT/PMT/SDT), per-PID statistics, continuity, PCR/PTS checks and
/// the ETSI TR 101 290 priority 1/2 (plus SDT) indicators. Thread-safe.
/// </summary>
public sealed class TsAnalyzer
{
    private const long PcrWrap = (1L << 33) * 300;
    private const long PtsWrap = 1L << 33;

    private readonly TsAnalyzerOptions _opt;
    private readonly EventLog? _log;
    private readonly object _gate = new();

    private readonly Dictionary<int, PidState> _pids = new();
    private readonly Dictionary<string, long> _errors = new();
    private readonly SectionAssembler _patAsm = new();
    private readonly SectionAssembler _sdtAsm = new();
    private readonly Dictionary<int, SectionAssembler> _pmtAsm = new();

    private readonly Dictionary<int, int> _pmtPidToProgram = new();
    private readonly SortedDictionary<int, PmtInfo> _programs = new();
    private readonly Dictionary<int, SdtService> _services = new();
    private int? _tsId;
    private int _patVersion = -1;

    private readonly byte[] _carry = new byte[TsPacket.Size];
    private int _carryLen;
    private bool _inSync;
    private int _badSyncRun;
    private bool _everSynced;

    private long _totalPackets, _totalBytes;
    private double _now, _start = double.NaN;
    private double _lastPatSeen, _lastSdtSeen;
    private double _lastPatErrorReport, _lastSdtErrorReport;
    private double _windowStart = double.NaN;
    private long _windowBytes;
    private double _bitrate;

    // PCR based rate estimation (first PCR PID seen)
    private int _ratePcrPid = -1;
    private long _ratePcrFirst = -1, _ratePcrLast;
    private long _rateBytesAtFirst, _rateBytesAtLast;

    public TsAnalyzer(TsAnalyzerOptions? options = null, EventLog? log = null)
    {
        _opt = options ?? new TsAnalyzerOptions();
        _log = log;
        foreach (var k in Tr101290.Priority1.Concat(Tr101290.Priority2).Concat(Tr101290.Priority3)) _errors[k] = 0;
    }

    private sealed class PidState
    {
        public int Pid;
        public long Packets, CcErrors, TeiErrors, ScrambledPackets;
        public int LastCc = -1;
        public bool LastHadPayload;
        public bool DuplicateSeen;
        public double FirstSeen, LastSeen;
        public double LastErrorReport = double.NegativeInfinity;
        public long WindowBytes;
        public double Bitrate;
        public long LastPcr = -1;
        public bool HasPcr;
        public double MaxPcrIntervalMs;
        public long LastPts = -1;
        public bool ScrambledRecently;
    }

    /// <summary>Feed raw bytes (any alignment). <paramref name="nowSeconds"/> is a monotonic clock.</summary>
    public void Process(ReadOnlySpan<byte> data, double nowSeconds)
    {
        lock (_gate)
        {
            _now = nowSeconds;
            if (double.IsNaN(_start))
            {
                _start = nowSeconds;
                _windowStart = nowSeconds;
                _lastPatSeen = _lastSdtSeen = nowSeconds;
                _lastPatErrorReport = _lastSdtErrorReport = nowSeconds;
            }

            // Complete a packet that straddled the previous call.
            if (_carryLen > 0)
            {
                int need = TsPacket.Size - _carryLen;
                if (data.Length < need)
                {
                    data.CopyTo(_carry.AsSpan(_carryLen));
                    _carryLen += data.Length;
                    return;
                }
                data[..need].CopyTo(_carry.AsSpan(_carryLen));
                data = data[need..];
                _carryLen = 0;
                HandleAlignedPacket(_carry);
            }

            int i = 0;
            while (i < data.Length)
            {
                if (!_inSync)
                {
                    int found = FindSync(data[i..]);
                    if (found < 0) break; // no confirmed sync in this chunk; drop it
                    i += found;
                    _inSync = true;
                    _badSyncRun = 0;
                    if (_everSynced) Log(Severity.Info, "SYNC", "Transport stream sync regained");
                    _everSynced = true;
                }

                if (data.Length - i < TsPacket.Size)
                {
                    data[i..].CopyTo(_carry);
                    _carryLen = data.Length - i;
                    return;
                }

                HandleAlignedPacket(data.Slice(i, TsPacket.Size));
                i += TsPacket.Size;
            }

            if (_opt.LiveTimeouts) CheckTimeoutsLocked();
        }
    }

    /// <summary>Run wall-clock checks without new data (e.g. when the feed stalls).</summary>
    public void Tick(double nowSeconds)
    {
        lock (_gate)
        {
            if (double.IsNaN(_start)) return;
            _now = nowSeconds;
            UpdateBitratesLocked(force: false);
            if (_opt.LiveTimeouts) CheckTimeoutsLocked();
        }
    }

    private static int FindSync(ReadOnlySpan<byte> d)
    {
        for (int i = 0; i < d.Length; i++)
        {
            if (d[i] != TsPacket.SyncByte) continue;
            // Require at least one following sync byte; use a second one when the data is long enough.
            int j1 = i + TsPacket.Size, j2 = i + 2 * TsPacket.Size;
            if (j1 >= d.Length) return -1;
            if (d[j1] != TsPacket.SyncByte) continue;
            if (j2 < d.Length && d[j2] != TsPacket.SyncByte) continue;
            return i;
        }
        return -1;
    }

    private void HandleAlignedPacket(ReadOnlySpan<byte> raw)
    {
        if (raw[0] != TsPacket.SyncByte)
        {
            Bump(Tr101290.SyncByteError);
            _badSyncRun++;
            if (_badSyncRun >= 2 && _inSync)
            {
                _inSync = false;
                Bump(Tr101290.TsSyncLoss);
                Log(Severity.Error, Tr101290.TsSyncLoss, "Transport stream sync lost");
                ResetAssemblers();
            }
            return;
        }
        _badSyncRun = 0;

        var pkt = new TsPacket(raw);
        _totalPackets++;
        _totalBytes += TsPacket.Size;
        _windowBytes += TsPacket.Size;

        int pid = pkt.Pid;
        if (!_pids.TryGetValue(pid, out var st))
        {
            st = new PidState { Pid = pid, FirstSeen = _now };
            _pids[pid] = st;
        }
        st.Packets++;
        st.LastSeen = _now;
        st.WindowBytes += TsPacket.Size;

        if (pkt.TransportError)
        {
            st.TeiErrors++;
            Bump(Tr101290.TransportError);
            Log(Severity.Error, Tr101290.TransportError, $"Transport error indicator set on PID {Hex(pid)}");
            return; // contents are unreliable
        }

        if (pid != TsPacket.NullPid) CheckContinuity(pkt, st);

        bool scrambled = pkt.ScramblingControl != 0;
        if (scrambled) st.ScrambledPackets++;
        st.ScrambledRecently = scrambled;

        if (pkt.HasPcr) HandlePcr(pkt, st);

        if (pid == TsPacket.NullPid) { MaybeUpdateBitrates(); return; }

        if (pid == 0)
        {
            if (scrambled) { Bump(Tr101290.PatError); Log(Severity.Error, Tr101290.PatError, "PAT PID is scrambled"); }
            else _patAsm.Push(pkt.Payload, pkt.PayloadUnitStart, OnPatSection);
        }
        else if (pid == 0x11)
        {
            if (!scrambled) _sdtAsm.Push(pkt.Payload, pkt.PayloadUnitStart, OnSdtSection);
        }
        else if (_pmtPidToProgram.ContainsKey(pid))
        {
            if (scrambled) { Bump(Tr101290.PmtError); Log(Severity.Error, Tr101290.PmtError, $"PMT PID {Hex(pid)} is scrambled"); }
            else
            {
                if (!_pmtAsm.TryGetValue(pid, out var asm)) _pmtAsm[pid] = asm = new SectionAssembler();
                asm.Push(pkt.Payload, pkt.PayloadUnitStart, s => OnPmtSection(pid, s));
            }
        }
        else if (pkt.PayloadUnitStart && !scrambled)
        {
            HandlePes(pkt, st);
        }

        MaybeUpdateBitrates();
    }

    private void CheckContinuity(TsPacket pkt, PidState st)
    {
        int cc = pkt.ContinuityCounter;
        bool payload = pkt.HasPayload;
        int last = st.LastCc;
        st.LastCc = cc;
        bool lastPayload = st.LastHadPayload;
        st.LastHadPayload = payload;
        if (last < 0 || pkt.Discontinuity) { st.DuplicateSeen = false; return; }

        if (!payload)
        {
            // CC must not increment on packets without payload.
            if (cc == last) return;
        }
        else
        {
            if (cc == ((last + 1) & 0x0F)) { st.DuplicateSeen = false; return; }
            if (cc == last && lastPayload && !st.DuplicateSeen) { st.DuplicateSeen = true; return; } // one duplicate allowed
        }

        st.CcErrors++;
        st.DuplicateSeen = false;
        Bump(Tr101290.ContinuityCountError);
        int expected = payload ? (last + 1) & 0x0F : last;
        Log(Severity.Error, Tr101290.ContinuityCountError,
            $"Continuity error on PID {Hex(st.Pid)}: expected {expected}, got {cc}", "CC|" + st.Pid);

        // Packet loss breaks any section in progress on this PID.
        if (st.Pid == 0) _patAsm.Reset();
        else if (st.Pid == 0x11) _sdtAsm.Reset();
        else if (_pmtAsm.TryGetValue(st.Pid, out var asm)) asm.Reset();
    }

    private void HandlePcr(TsPacket pkt, PidState st)
    {
        long pcr = pkt.Pcr;
        st.HasPcr = true;

        if (_ratePcrPid < 0) _ratePcrPid = st.Pid;
        if (_ratePcrPid == st.Pid)
        {
            if (_ratePcrFirst < 0) { _ratePcrFirst = pcr; _ratePcrLast = pcr; _rateBytesAtFirst = _totalBytes; _rateBytesAtLast = _totalBytes; }
            else
            {
                long d = (pcr - _ratePcrLast + PcrWrap) % PcrWrap;
                if (d > 0 && d < 27_000_000L * 2) { _ratePcrLast = pcr; _rateBytesAtLast = _totalBytes; }
                else { _ratePcrFirst = pcr; _ratePcrLast = pcr; _rateBytesAtFirst = _totalBytes; _rateBytesAtLast = _totalBytes; }
            }
        }

        if (st.LastPcr >= 0 && !pkt.Discontinuity)
        {
            long raw = pcr - st.LastPcr;
            double ms;
            if (raw < 0 && raw < -PcrWrap / 2) raw += PcrWrap; // wrap-around
            ms = raw / 27_000.0;

            if (raw < 0 || ms > _opt.PcrDiscontinuityMaxMs)
            {
                Bump(Tr101290.PcrDiscontinuityError);
                Log(Severity.Warning, Tr101290.PcrDiscontinuityError,
                    $"PCR discontinuity on PID {Hex(st.Pid)}: jump of {ms:F1} ms without discontinuity flag", "PCRD|" + st.Pid);
            }
            else
            {
                if (ms > st.MaxPcrIntervalMs) st.MaxPcrIntervalMs = ms;
                if (ms > _opt.PcrRepetitionMaxMs)
                {
                    Bump(Tr101290.PcrRepetitionError);
                    Log(Severity.Warning, Tr101290.PcrRepetitionError,
                        $"PCR interval on PID {Hex(st.Pid)} is {ms:F1} ms (> {_opt.PcrRepetitionMaxMs} ms)", "PCRR|" + st.Pid);
                }
            }
        }
        st.LastPcr = pcr;
    }

    private void HandlePes(TsPacket pkt, PidState st)
    {
        var p = pkt.Payload;
        if (p.Length < 14 || p[0] != 0 || p[1] != 0 || p[2] != 1) return;
        int streamId = p[3];
        // stream ids without the optional PES header
        if (streamId is 0xBC or 0xBE or 0xBF or 0xF0 or 0xF1 or 0xF2 or 0xF8 or 0xFF) return;
        if ((p[6] & 0xC0) != 0x80) return;
        int flags = p[7] >> 6;
        if ((flags & 0x2) == 0) return;
        long pts = ((long)(p[9] & 0x0E) << 29) | ((long)p[10] << 22) | ((long)(p[11] & 0xFE) << 14) |
                   ((long)p[12] << 7) | ((long)p[13] >> 1);
        if (st.LastPts >= 0)
        {
            long d = pts - st.LastPts;
            if (d < -PtsWrap / 2) d += PtsWrap;
            double ms = d / 90.0;
            // Only forward gaps count as PTS repetition errors; big backward jumps are usually splices.
            if (ms > _opt.PtsRepetitionMaxMs && ms < 10_000)
            {
                Bump(Tr101290.PtsError);
                Log(Severity.Warning, Tr101290.PtsError,
                    $"PTS gap of {ms:F0} ms on PID {Hex(st.Pid)} (> {_opt.PtsRepetitionMaxMs} ms)", "PTS|" + st.Pid);
            }
        }
        st.LastPts = pts;
    }

    private bool ValidSection(byte[] s, string what)
    {
        if (s.Length < 12 || (s[1] & 0x80) == 0) return false; // too short / no long syntax
        if (!Crc32Mpeg.IsValidSection(s))
        {
            Bump(Tr101290.CrcError);
            Log(Severity.Error, Tr101290.CrcError, $"CRC error in {what}");
            return false;
        }
        return true;
    }

    private void OnPatSection(byte[] s)
    {
        if (s[0] != PsiParser.TableIdPat)
        {
            Bump(Tr101290.PatError);
            Log(Severity.Error, Tr101290.PatError, $"Unexpected table_id 0x{s[0]:X2} on PID 0x0000");
            return;
        }
        if (!ValidSection(s, "PAT")) return;
        _lastPatSeen = _now;
        _tsId = (s[3] << 8) | s[4];
        int version = PsiParser.SectionVersion(s);
        if (version == _patVersion) return;
        bool changed = _patVersion >= 0;
        _patVersion = version;

        var entries = PsiParser.ParsePat(s);
        var newMap = entries.ToDictionary(e => e.PmtPid, e => e.ProgramNumber);
        foreach (var old in _pmtPidToProgram.Keys.Where(k => !newMap.ContainsKey(k)).ToList())
        {
            _pmtPidToProgram.Remove(old);
            _pmtAsm.Remove(old);
        }
        foreach (var prog in _programs.Keys.Where(p => !entries.Any(e => e.ProgramNumber == p)).ToList())
            _programs.Remove(prog);
        foreach (var e in entries) _pmtPidToProgram[e.PmtPid] = e.ProgramNumber;

        Log(changed ? Severity.Warning : Severity.Info, "PAT",
            $"PAT version {version}{(changed ? " (changed)" : "")}: {entries.Count} program(s), TSID {_tsId}");
    }

    private readonly Dictionary<int, double> _pmtLastSeen = new();
    private readonly Dictionary<int, double> _pmtLastErrorReport = new();
    private readonly Dictionary<int, int> _pmtVersion = new();

    private void OnPmtSection(int pid, byte[] s)
    {
        if (s[0] != PsiParser.TableIdPmt) return; // other tables may share the PID
        if (!ValidSection(s, $"PMT on PID {Hex(pid)}")) return;
        _pmtLastSeen[pid] = _now;
        int version = PsiParser.SectionVersion(s);
        if (_pmtVersion.TryGetValue(pid, out var v) && v == version) return;
        bool changed = _pmtVersion.ContainsKey(pid);
        _pmtVersion[pid] = version;
        var info = PsiParser.ParsePmt(s);
        _programs[info.ProgramNumber] = info;
        var streams = string.Join(", ", info.Streams.Select(x => $"{Hex(x.Pid)} {x.Codec}"));
        Log(changed ? Severity.Warning : Severity.Info, "PMT",
            $"Program {info.ProgramNumber} PMT v{version}{(changed ? " (changed)" : "")}: PCR {Hex(info.PcrPid)}; {streams}");
    }

    private void OnSdtSection(byte[] s)
    {
        if (s[0] != PsiParser.TableIdSdtActual) return; // ignore SDT other / BAT
        if (!ValidSection(s, "SDT")) return;
        _lastSdtSeen = _now;
        foreach (var svc in PsiParser.ParseSdt(s))
        {
            bool isNew = !_services.TryGetValue(svc.ServiceId, out var old) || old != svc;
            _services[svc.ServiceId] = svc;
            if (isNew)
                Log(Severity.Info, "SDT", $"Service {svc.ServiceId}: \"{svc.ServiceName}\" ({svc.ProviderName})");
        }
    }

    private void CheckTimeoutsLocked()
    {
        double now = _now;
        if (now - _start < 1.0) return; // grace period after start

        if (now - _lastPatSeen > _opt.PatTimeoutSeconds && now - _lastPatErrorReport > _opt.PatTimeoutSeconds)
        {
            _lastPatErrorReport = now;
            Bump(Tr101290.PatError);
            Log(Severity.Error, Tr101290.PatError, $"PAT not received for {(now - _lastPatSeen) * 1000:F0} ms", "PATTO");
        }

        foreach (var pmtPid in _pmtPidToProgram.Keys)
        {
            double seen = _pmtLastSeen.TryGetValue(pmtPid, out var t) ? t : _start;
            double rep = _pmtLastErrorReport.TryGetValue(pmtPid, out var r) ? r : _start;
            if (now - seen > _opt.PmtTimeoutSeconds && now - rep > _opt.PmtTimeoutSeconds)
            {
                _pmtLastErrorReport[pmtPid] = now;
                Bump(Tr101290.PmtError);
                Log(Severity.Error, Tr101290.PmtError, $"PMT on PID {Hex(pmtPid)} not received for {(now - seen) * 1000:F0} ms", "PMTTO|" + pmtPid);
            }
        }

        foreach (var prog in _programs.Values)
        {
            foreach (var es in prog.Streams)
            {
                if (es.Kind is not (EsKind.Video or EsKind.Audio)) continue;
                _pids.TryGetValue(es.Pid, out var st);
                double seen = st?.LastSeen ?? _start;
                double rep = st?.LastErrorReport ?? double.NegativeInfinity;
                if (now - seen > _opt.PidTimeoutSeconds && now - rep > _opt.PidTimeoutSeconds)
                {
                    if (st == null) { st = new PidState { Pid = es.Pid, FirstSeen = now, LastSeen = _start }; _pids[es.Pid] = st; }
                    st.LastErrorReport = now;
                    Bump(Tr101290.PidError);
                    Log(Severity.Error, Tr101290.PidError,
                        $"{es.Codec} PID {Hex(es.Pid)} (program {prog.ProgramNumber}) missing for {now - seen:F1} s", "PIDTO|" + es.Pid);
                }
            }
        }

        if (now - _lastSdtSeen > _opt.SdtTimeoutSeconds && now - _lastSdtErrorReport > Math.Max(_opt.SdtTimeoutSeconds, 10))
        {
            _lastSdtErrorReport = now;
            Bump(Tr101290.SdtError);
            Log(Severity.Warning, Tr101290.SdtError, $"SDT not received for {now - _lastSdtSeen:F1} s", "SDTTO");
        }
    }

    private void MaybeUpdateBitrates() => UpdateBitratesLocked(force: false);

    private void UpdateBitratesLocked(bool force)
    {
        double elapsed = _now - _windowStart;
        if (!force && elapsed < _opt.BitrateWindowSeconds) return;
        if (elapsed <= 0) return;
        _bitrate = _windowBytes * 8 / elapsed;
        _windowBytes = 0;
        foreach (var st in _pids.Values)
        {
            st.Bitrate = st.WindowBytes * 8 / elapsed;
            st.WindowBytes = 0;
        }
        _windowStart = _now;
    }

    private void ResetAssemblers()
    {
        _patAsm.Reset();
        _sdtAsm.Reset();
        foreach (var a in _pmtAsm.Values) a.Reset();
        foreach (var st in _pids.Values) { st.LastCc = -1; st.LastPcr = -1; st.LastPts = -1; }
    }

    private void Bump(string key) => _errors[key] = _errors.GetValueOrDefault(key) + 1;

    private void Log(Severity sev, string code, string msg, string? key = null) => _log?.Add(sev, code, msg, key);

    private static string Hex(int pid) => $"0x{pid:X4}";

    /// <summary>Average bitrate over the data fed so far, derived from PCR values (independent of arrival time).</summary>
    public double? PcrBitrate
    {
        get
        {
            lock (_gate) return PcrBitrateLocked();
        }
    }

    private double? PcrBitrateLocked()
    {
        if (_ratePcrFirst < 0) return null;
        long d = (_ratePcrLast - _ratePcrFirst + PcrWrap) % PcrWrap;
        if (d < 27_000_00) return null; // need at least 100 ms
        return (_rateBytesAtLast - _rateBytesAtFirst) * 8 / (d / 27_000_000.0);
    }

    public TsSnapshot Snapshot()
    {
        lock (_gate)
        {
            var referenced = new HashSet<int> { 0, 0x10, 0x11, 0x12, 0x14, TsPacket.NullPid };
            foreach (var p in _pmtPidToProgram.Keys) referenced.Add(p);
            foreach (var prog in _programs.Values)
            {
                referenced.Add(prog.PcrPid);
                foreach (var s in prog.Streams) referenced.Add(s.Pid);
            }

            var programs = new List<ProgramSnapshot>();
            foreach (var (pmtPid, number) in _pmtPidToProgram.OrderBy(k => k.Value))
            {
                _programs.TryGetValue(number, out var info);
                _services.TryGetValue(number, out var svc);
                var streams = info?.Streams.Select(s =>
                {
                    _pids.TryGetValue(s.Pid, out var st);
                    bool present = st != null && st.Packets > 0 && _now - st.LastSeen < Math.Max(2, _opt.PidTimeoutSeconds);
                    return new StreamSnapshot(s.Pid, s.Codec, s.Kind.ToString(), s.Language, st?.Bitrate ?? 0, present);
                }).ToList() ?? [];
                bool encrypted = (info?.HasCaDescriptor ?? false) ||
                                 (info?.Streams.Any(s => _pids.TryGetValue(s.Pid, out var st) && st.ScrambledRecently) ?? false);
                programs.Add(new ProgramSnapshot(number, pmtPid, info?.PcrPid ?? -1, svc?.ServiceName, svc?.ProviderName, encrypted, streams));
            }

            var pidDesc = new Dictionary<int, (string, string)>
            {
                [0] = ("PAT", "PSI"), [0x10] = ("NIT", "SI"), [0x11] = ("SDT/BAT", "SI"), [0x12] = ("EIT", "SI"),
                [0x14] = ("TDT/TOT", "SI"), [TsPacket.NullPid] = ("Null packets", "Stuffing"),
            };
            foreach (var (pmtPid, number) in _pmtPidToProgram) pidDesc[pmtPid] = ($"PMT (program {number})", "PSI");
            foreach (var prog in _programs.Values)
                foreach (var s in prog.Streams) pidDesc[s.Pid] = (s.Codec + (s.Language != null ? $" [{s.Language}]" : ""), s.Kind.ToString());

            var pids = _pids.Values.Where(p => p.Packets > 0).OrderBy(p => p.Pid).Select(p =>
            {
                var (desc, kind) = pidDesc.TryGetValue(p.Pid, out var d) ? d : ("Unknown", "Unknown");
                return new PidSnapshot(p.Pid, desc, kind, p.Packets, p.Bitrate, p.CcErrors, p.TeiErrors,
                    p.ScrambledRecently, p.HasPcr, p.HasPcr ? Math.Round(p.MaxPcrIntervalMs, 2) : null,
                    Math.Max(0, _now - p.LastSeen), referenced.Contains(p.Pid));
            }).ToList();

            var errors = new Dictionary<string, long>(_errors);
            return new TsSnapshot(_totalPackets, _totalBytes, _bitrate, PcrBitrateLocked(), _inSync, _tsId, errors,
                Tr101290.Priority1.Sum(k => errors[k]), Tr101290.Priority2.Sum(k => errors[k]), Tr101290.Priority3.Sum(k => errors[k]),
                programs, pids);
        }
    }

    public bool InSync
    {
        get { lock (_gate) return _inSync; }
    }

    public long ErrorCount(string indicator)
    {
        lock (_gate) return _errors.GetValueOrDefault(indicator);
    }

    /// <summary>Cheap totals per TR 101 290 priority, for periodic sampling.</summary>
    public (long P1, long P2, long P3) PriorityTotals()
    {
        lock (_gate)
            return (Tr101290.Priority1.Sum(k => _errors[k]), Tr101290.Priority2.Sum(k => _errors[k]),
                Tr101290.Priority3.Sum(k => _errors[k]));
    }

    /// <summary>Reset error counters and per-PID error statistics (keeps PSI knowledge).</summary>
    public void ResetCounters()
    {
        lock (_gate)
        {
            foreach (var k in _errors.Keys.ToList()) _errors[k] = 0;
            foreach (var p in _pids.Values) { p.CcErrors = 0; p.TeiErrors = 0; p.MaxPcrIntervalMs = 0; }
        }
    }
}
