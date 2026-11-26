using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using IPTVAnalyzer.Core.Common;
using IPTVAnalyzer.Core.Ts;

namespace IPTVAnalyzer.Core.Multicast;

public enum FeedHealth { Starting, Ok, Warning, Error, NoData, Stopped }

public sealed record HistorySample(long UnixMs, double BitrateBps, double DelayFactorMs, double MediaLossRate, long P1Errors, long P2Errors);

public sealed record MulticastFeedSnapshot(
    string Id,
    string Name,
    string Address,
    string? Interface,
    FeedHealth Health,
    string? HealthReason,
    string Transport,
    long Datagrams,
    long Bytes,
    long RtpLost,
    long RtpOutOfOrder,
    double MaxInterArrivalMs,
    double DelayFactorMs,
    double MediaLossRate,
    double MaxDelayFactorMs,
    double MaxMediaLossRate,
    double? SecondsSinceData,
    double UptimeSeconds,
    TsSnapshot Ts,
    IReadOnlyList<HistorySample>? History,
    IReadOnlyList<AnalyzerEvent>? Events);

public sealed class MulticastMonitorOptions
{
    public double DelayFactorWarnMs { get; init; } = 50;
    /// <summary>How many seconds of recent history decide the health state.</summary>
    public int HealthWindowSeconds { get; init; } = 10;
    public int HistorySeconds { get; init; } = 600;
    public TsAnalyzerOptions Analyzer { get; init; } = new();
}

/// <summary>Joins one multicast group, receives UDP or RTP encapsulated MPEG-TS, and analyzes it live.</summary>
public sealed class MulticastMonitor : IAsyncDisposable
{
    private readonly MulticastMonitorOptions _opt;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly object _gate = new();
    private readonly MdiCalculator _mdi = new();
    private readonly LinkedList<HistorySample> _history = new();
    private CancellationTokenSource? _cts;
    private Task? _runTask, _tickTask;

    private long _datagrams, _bytes;
    private double _lastData = double.NaN, _startedAt;
    private string _transport = "-";
    private int _lastSeq = -1;
    private long _rtpLost, _rtpOoo;
    private double _iatMaxInterval, _iatMaxReported;
    private long _ccLast, _p1Last, _p2Last;
    private FeedHealth _health = FeedHealth.Starting;
    private string? _healthReason;
    private FeedHealth _lastLoggedHealth = FeedHealth.Starting;
    private string? _socketError;

    public MulticastMonitor(MulticastFeedConfig config, MulticastMonitorOptions? options = null)
    {
        config.Validate();
        Config = config;
        Id = string.IsNullOrWhiteSpace(config.Id) ? Guid.NewGuid().ToString("n")[..8] : config.Id!;
        config.Id = Id;
        _opt = options ?? new MulticastMonitorOptions();
        Log = new EventLog();
        Analyzer = new TsAnalyzer(_opt.Analyzer, Log);
        _mdi.IntervalCompleted += OnMdiInterval;
    }

    public string Id { get; }
    public MulticastFeedConfig Config { get; }
    public EventLog Log { get; }
    public TsAnalyzer Analyzer { get; }

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        _startedAt = Now();
        Log.Add(Severity.Info, "START", $"Monitoring {Config.Address}" + (string.IsNullOrWhiteSpace(Config.Interface) ? "" : $" on {Config.Interface}"));
        _runTask = Task.Run(() => RunAsync(_cts.Token));
        _tickTask = Task.Run(() => TickLoopAsync(_cts.Token));
    }

    public async Task StopAsync()
    {
        if (_cts == null) return;
        _cts.Cancel();
        try { await Task.WhenAll(_runTask!, _tickTask!); } catch (OperationCanceledException) { }
        _cts.Dispose();
        _cts = null;
        lock (_gate) _health = FeedHealth.Stopped;
        Log.Add(Severity.Info, "STOP", "Monitoring stopped");
    }

    public ValueTask DisposeAsync() => new(StopAsync());

    private double Now() => _clock.Elapsed.TotalSeconds;

    private async Task RunAsync(CancellationToken ct)
    {
        var buffer = new byte[65536];
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var socket = CreateSocket();
                lock (_gate) _socketError = null;
                while (!ct.IsCancellationRequested)
                {
                    int n = await socket.ReceiveAsync(buffer.AsMemory(), SocketFlags.None, ct);
                    if (n > 0) OnDatagram(buffer.AsSpan(0, n), Now());
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                lock (_gate) _socketError = ex.Message;
                Log.Add(Severity.Error, "SOCKET", $"Socket error: {ex.Message}. Retrying in 5 s");
                try { await Task.Delay(5000, ct); } catch (OperationCanceledException) { break; }
            }
        }
    }

    private Socket CreateSocket()
    {
        var group = IPAddress.Parse(Config.Group);
        var local = string.IsNullOrWhiteSpace(Config.Interface) ? IPAddress.Any : IPAddress.Parse(Config.Interface);
        var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        try
        {
            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            s.ReceiveBufferSize = 8 * 1024 * 1024;
            // Windows cannot bind to a multicast address; elsewhere binding to the group filters other groups on the port.
            s.Bind(new IPEndPoint(OperatingSystem.IsWindows() ? IPAddress.Any : group, Config.Port));

            if (!string.IsNullOrWhiteSpace(Config.Source))
            {
                var src = IPAddress.Parse(Config.Source).GetAddressBytes();
                var grp = group.GetAddressBytes();
                var itf = local.GetAddressBytes();
                var mreq = new byte[12];
                grp.CopyTo(mreq, 0);
                if (OperatingSystem.IsLinux()) { itf.CopyTo(mreq, 4); src.CopyTo(mreq, 8); }   // ip_mreq_source (Linux)
                else { src.CopyTo(mreq, 4); itf.CopyTo(mreq, 8); }                              // ip_mreq_source (Windows/BSD)
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddSourceMembership, mreq);
            }
            else
            {
                s.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(group, local));
            }
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    /// <summary>Feeds one datagram. Public so recorded captures and tests can drive the monitor.</summary>
    public void OnDatagram(ReadOnlySpan<byte> d, double t)
    {
        ReadOnlySpan<byte> payload;
        long lostTs = 0;
        lock (_gate)
        {
            if (!double.IsNaN(_lastData))
            {
                double iat = (t - _lastData) * 1000;
                if (iat > _iatMaxInterval) _iatMaxInterval = iat;
            }
            _datagrams++;
            _bytes += d.Length;
            _lastData = t;

            if (RtpHeader.TryParse(d, out var rtp))
            {
                _transport = "RTP";
                payload = d.Slice(rtp.HeaderLength, d.Length - rtp.HeaderLength - rtp.PaddingLength);
                if (_lastSeq >= 0)
                {
                    int diff = (rtp.Sequence - ((_lastSeq + 1) & 0xFFFF)) & 0xFFFF;
                    if (diff != 0)
                    {
                        if (diff < 0x8000)
                        {
                            _rtpLost += diff;
                            lostTs = diff * (long)Math.Max(1, payload.Length / TsPacket.Size);
                            Log.Add(Severity.Error, "RTP_LOSS", $"RTP sequence gap: {diff} datagram(s) lost (seq {rtp.Sequence})", "RTPLOSS");
                        }
                        else
                        {
                            _rtpOoo++;
                            Log.Add(Severity.Warning, "RTP_ORDER", $"RTP datagram out of order or duplicated (seq {rtp.Sequence})", "RTPOOO");
                        }
                    }
                }
                if (_lastSeq < 0 || ((rtp.Sequence - _lastSeq) & 0xFFFF) < 0x8000) _lastSeq = rtp.Sequence;
            }
            else
            {
                if (_transport == "-") _transport = "UDP";
                payload = d;
            }

            _mdi.OnDatagram(t, payload.Length / TsPacket.Size * TsPacket.Size);
            if (lostTs > 0) _mdi.OnLoss(lostTs);
        }
        Analyzer.Process(payload, t);
    }

    private async Task TickLoopAsync(CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(250));
        try
        {
            while (await timer.WaitForNextTickAsync(ct)) Tick(Now());
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>Periodic housekeeping. Public for tests that drive a virtual clock.</summary>
    public void Tick(double t)
    {
        bool receiving;
        lock (_gate) receiving = !double.IsNaN(_lastData) && t - _lastData <= Config.NoDataTimeoutSeconds;
        if (receiving) Analyzer.Tick(t); // timeouts while stalled would only duplicate the "no data" alarm

        lock (_gate)
        {
            if (_transport != "RTP")
            {
                long cc = Analyzer.ErrorCount(Tr101290.ContinuityCountError);
                if (cc > _ccLast) _mdi.OnLoss(cc - _ccLast);
                _ccLast = cc;
            }
            _mdi.Tick(t);
            UpdateHealthLocked(t);
        }
    }

    private void OnMdiInterval(double df, double mlr, double rateBps)
    {
        // Called under _gate from OnDatagram/Tick. The first interval has no drain rate yet, so no DF.
        if (!_mdi.HasValue) return;
        var (p1, p2, _) = Analyzer.PriorityTotals();
        var sample = new HistorySample(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rateBps, Math.Round(df, 2),
            Math.Round(mlr, 2), p1 - _p1Last, p2 - _p2Last);
        _p1Last = p1;
        _p2Last = p2;
        _iatMaxReported = _iatMaxInterval;
        _iatMaxInterval = 0;
        _history.AddLast(sample);
        while (_history.Count > _opt.HistorySeconds) _history.RemoveFirst();
        if (_opt.DelayFactorWarnMs > 0 && df > _opt.DelayFactorWarnMs)
            Log.Add(Severity.Warning, "MDI_DF", $"Delay factor {df:F1} ms exceeds {_opt.DelayFactorWarnMs} ms", "MDIDF");
    }

    private void UpdateHealthLocked(double t)
    {
        FeedHealth h;
        string? reason = null;
        if (double.IsNaN(_lastData))
        {
            if (_socketError != null) { h = FeedHealth.Error; reason = _socketError; }
            else if (t - _startedAt > Math.Max(2, Config.NoDataTimeoutSeconds)) { h = FeedHealth.NoData; reason = "No datagrams received since start"; }
            else h = FeedHealth.Starting;
        }
        else if (t - _lastData > Config.NoDataTimeoutSeconds)
        {
            h = FeedHealth.NoData;
            reason = $"No data for {t - _lastData:F1} s";
        }
        else
        {
            var recent = _history.Reverse().Take(_opt.HealthWindowSeconds).ToList();
            var snapErrors = recent.Sum(s => s.P1Errors);
            if (!Analyzer.InSync) { h = FeedHealth.Error; reason = "No TS sync"; }
            else if (snapErrors > 0) { h = FeedHealth.Error; reason = $"{snapErrors} priority 1 error(s) in last {recent.Count} s"; }
            else if (recent.Sum(s => s.P2Errors) is var p2 and > 0) { h = FeedHealth.Warning; reason = $"{p2} priority 2 error(s) in last {recent.Count} s"; }
            else if (recent.Any(s => s.MediaLossRate > 0)) { h = FeedHealth.Warning; reason = "Packet loss in last seconds"; }
            else if (_opt.DelayFactorWarnMs > 0 && recent.Any(s => s.DelayFactorMs > _opt.DelayFactorWarnMs))
            { h = FeedHealth.Warning; reason = $"Delay factor above {_opt.DelayFactorWarnMs} ms"; }
            else h = FeedHealth.Ok;
        }

        _health = h;
        _healthReason = reason;
        if (h != _lastLoggedHealth)
        {
            var sev = h switch { FeedHealth.Error or FeedHealth.NoData => Severity.Error, FeedHealth.Warning => Severity.Warning, _ => Severity.Info };
            Log.Add(sev, "HEALTH", $"Status {_lastLoggedHealth} → {h}" + (reason != null ? $": {reason}" : ""));
            _lastLoggedHealth = h;
        }
    }

    public MulticastFeedSnapshot Snapshot(bool includeDetails)
    {
        var ts = Analyzer.Snapshot();
        lock (_gate)
        {
            double now = Now();
            return new MulticastFeedSnapshot(Id, Config.Name, Config.Address, Config.Interface, _health, _healthReason, _transport,
                _datagrams, _bytes, _rtpLost, _rtpOoo, Math.Round(Math.Max(_iatMaxReported, _iatMaxInterval), 2),
                Math.Round(_mdi.DelayFactorMs, 2), Math.Round(_mdi.MediaLossRate, 2),
                Math.Round(_mdi.MaxDelayFactorMs, 2), Math.Round(_mdi.MaxMediaLossRate, 2),
                double.IsNaN(_lastData) ? null : Math.Round(now - _lastData, 2),
                Math.Round(now - _startedAt, 1), ts,
                includeDetails ? _history.ToList() : _history.Reverse().Take(60).Reverse().ToList(),
                includeDetails ? Log.Snapshot(200) : null);
        }
    }

    public void ResetCounters()
    {
        Analyzer.ResetCounters();
        lock (_gate)
        {
            _rtpLost = _rtpOoo = 0;
            _mdi.ResetMaxima();
            _ccLast = 0;
            _p1Last = _p2Last = 0;
            _history.Clear();
        }
        Log.Add(Severity.Info, "RESET", "Counters reset");
    }
}
