namespace IPTVAnalyzer.Core.Common;

public enum Severity { Info, Warning, Error }

public sealed record AnalyzerEvent(DateTimeOffset Time, Severity Severity, string Code, string Message, int Repeat);

/// <summary>
/// Thread-safe bounded event log. Repeated events with the same key within the throttle window are
/// folded into a single entry whose <see cref="AnalyzerEvent.Repeat"/> count grows.
/// </summary>
public sealed class EventLog
{
    private readonly int _capacity;
    private readonly TimeSpan _throttle;
    private readonly LinkedList<AnalyzerEvent> _items = new();
    private readonly Dictionary<string, (LinkedListNode<AnalyzerEvent> Node, DateTimeOffset First)> _recent = new();
    private readonly object _gate = new();
    private readonly TimeProvider _time;

    public EventLog(int capacity = 300, TimeSpan? throttle = null, TimeProvider? time = null)
    {
        _capacity = capacity;
        _throttle = throttle ?? TimeSpan.FromSeconds(1);
        _time = time ?? TimeProvider.System;
    }

    public event Action<AnalyzerEvent>? Added;

    public void Add(Severity severity, string code, string message, string? key = null)
    {
        var now = _time.GetUtcNow();
        key ??= code + "|" + message;
        AnalyzerEvent? raised = null;
        lock (_gate)
        {
            if (_recent.TryGetValue(key, out var r) && now - r.First < _throttle && r.Node.List != null)
            {
                var old = r.Node.Value;
                r.Node.Value = old with { Repeat = old.Repeat + 1, Message = message };
                return;
            }
            var ev = new AnalyzerEvent(now, severity, code, message, 1);
            var node = _items.AddFirst(ev);
            _recent[key] = (node, now);
            while (_items.Count > _capacity) _items.RemoveLast();
            if (_recent.Count > _capacity * 4)
            {
                foreach (var k in _recent.Where(kv => now - kv.Value.First >= _throttle).Select(kv => kv.Key).ToList())
                    _recent.Remove(k);
            }
            raised = ev;
        }
        Added?.Invoke(raised);
    }

    public IReadOnlyList<AnalyzerEvent> Snapshot(int max = 100)
    {
        lock (_gate) return _items.Take(max).ToList();
    }

    public void Clear()
    {
        lock (_gate) { _items.Clear(); _recent.Clear(); }
    }
}
