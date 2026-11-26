namespace IPTVAnalyzer.Core.Multicast;

/// <summary>
/// Media Delivery Index (RFC 4445). The Delay Factor is computed from a virtual buffer drained at the
/// average rate of the previous interval. The Media Loss Rate is the number of lost TS packets per second.
/// </summary>
public sealed class MdiCalculator
{
    private readonly double _interval;
    private double _t0 = double.NaN;
    private double _bytes;
    private double _vbMin, _vbMax;
    private double _drainRate; // bytes/s from the previous interval
    private long _lostPackets;

    public MdiCalculator(double intervalSeconds = 1.0) => _interval = intervalSeconds;

    /// <summary>Last completed interval's Delay Factor, in milliseconds.</summary>
    public double DelayFactorMs { get; private set; }
    /// <summary>Last completed interval's Media Loss Rate, in TS packets per second.</summary>
    public double MediaLossRate { get; private set; }
    public double MaxDelayFactorMs { get; private set; }
    public double MaxMediaLossRate { get; private set; }
    public bool HasValue { get; private set; }

    /// <summary>Raised when an interval completes: (delay factor ms, media loss rate, rate bits/s).</summary>
    public event Action<double, double, double>? IntervalCompleted;

    public void OnDatagram(double t, int tsBytes)
    {
        if (double.IsNaN(_t0)) Begin(t);
        else if (t - _t0 >= _interval) Close(t);

        double drained = _drainRate * (t - _t0);
        double before = _bytes - drained;
        double after = before + tsBytes;
        if (before < _vbMin) _vbMin = before;
        if (after > _vbMax) _vbMax = after;
        _bytes += tsBytes;
    }

    public void OnLoss(long tsPackets) => _lostPackets += tsPackets;

    /// <summary>Call periodically so intervals close even when traffic stops.</summary>
    public void Tick(double t)
    {
        if (!double.IsNaN(_t0) && t - _t0 >= _interval) Close(t);
    }

    private void Begin(double t)
    {
        _t0 = t;
        _bytes = 0;
        _vbMin = 0;
        _vbMax = 0;
        _lostPackets = 0;
    }

    private void Close(double t)
    {
        double elapsed = t - _t0;
        double rate = elapsed > 0 ? _bytes / elapsed : 0;
        if (_drainRate > 0 && rate > 0)
        {
            DelayFactorMs = (_vbMax - _vbMin) / _drainRate * 1000.0;
            HasValue = true;
        }
        else DelayFactorMs = 0;
        MediaLossRate = elapsed > 0 ? _lostPackets / elapsed : 0;
        if (HasValue && DelayFactorMs > MaxDelayFactorMs) MaxDelayFactorMs = DelayFactorMs;
        if (MediaLossRate > MaxMediaLossRate) MaxMediaLossRate = MediaLossRate;
        IntervalCompleted?.Invoke(DelayFactorMs, MediaLossRate, rate * 8);
        _drainRate = rate;
        Begin(t);
    }

    public void ResetMaxima()
    {
        MaxDelayFactorMs = 0;
        MaxMediaLossRate = 0;
    }
}
