using System.Collections.Concurrent;
using IPTVAnalyzer.Core.Common;

namespace IPTVAnalyzer.Core.Hls;

public sealed class PlaylistConfig
{
    public string? Id { get; set; }
    public string Name { get; set; } = "";
    /// <summary>http(s) URL or local file path of the M3U channel list (or a single HLS URL).</summary>
    public string Source { get; set; } = "";
    public double IntervalMinutes { get; set; } = 15;
    public int MaxConcurrency { get; set; } = 8;
    public string? UserAgent { get; set; }
    public VariantSelection VariantSelection { get; set; } = VariantSelection.Highest;
    public bool CheckLiveness { get; set; } = true;
    public double TimeoutSeconds { get; set; } = 15;
    /// <summary>Optional limit on the number of channels checked (0 = all).</summary>
    public int MaxChannels { get; set; }
}

public sealed record PlaylistSummary(
    string Id,
    string Name,
    string Source,
    int Channels,
    int Ok,
    int Warning,
    int Error,
    int Pending,
    bool Running,
    int Progress,
    DateTimeOffset? LastRunStarted,
    DateTimeOffset? LastRunFinished,
    DateTimeOffset? NextRun,
    string? LoadError,
    double IntervalMinutes);

/// <summary>Loads an M3U channel list and periodically checks every channel.</summary>
public sealed class PlaylistMonitor : IDisposable
{
    private readonly HttpClient _http;
    private readonly ConcurrentDictionary<int, ChannelCheckResult> _results = new();
    private readonly SemaphoreSlim _runLock = new(1, 1);
    private List<M3uChannel> _channels = [];
    private CancellationTokenSource? _runCts;
    private int _progress;
    private volatile bool _running;

    public PlaylistMonitor(PlaylistConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.Source)) throw new ArgumentException("Playlist source is required");
        Config = config;
        Id = string.IsNullOrWhiteSpace(config.Id) ? Guid.NewGuid().ToString("n")[..8] : config.Id!;
        config.Id = Id;
        if (string.IsNullOrWhiteSpace(config.Name)) config.Name = Path.GetFileName(config.Source.TrimEnd('/')) is { Length: > 0 } f ? f : config.Source;
        _http = HlsChecker.CreateHttpClient(config.UserAgent);
        Log = new EventLog(500, TimeSpan.Zero);
    }

    public string Id { get; }
    public PlaylistConfig Config { get; }
    public EventLog Log { get; }
    public DateTimeOffset? LastRunStarted { get; private set; }
    public DateTimeOffset? LastRunFinished { get; private set; }
    public DateTimeOffset? NextRun { get; set; }
    public string? LoadError { get; private set; }
    public bool Running => _running;

    public async Task LoadAsync(CancellationToken ct)
    {
        string text;
        Uri? baseUri = null;
        if (Uri.TryCreate(Config.Source, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https"))
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, Config.TimeoutSeconds)));
            using var resp = await _http.GetAsync(u, cts.Token);
            resp.EnsureSuccessStatusCode();
            text = await resp.Content.ReadAsStringAsync(cts.Token);
            baseUri = resp.RequestMessage?.RequestUri ?? u;
        }
        else
        {
            var path = u?.IsFile == true ? u.LocalPath : Config.Source;
            text = await File.ReadAllTextAsync(path, ct);
            baseUri = new Uri(Path.GetFullPath(path));
        }

        List<M3uChannel> channels;
        if (M3uParser.IsHlsPlaylist(text))
            channels = [new M3uChannel(0, Config.Name, Config.Source, null, null, null, null, new Dictionary<string, string>())];
        else
            channels = M3uParser.Parse(text, baseUri);
        if (Config.MaxChannels > 0) channels = channels.Take(Config.MaxChannels).ToList();

        // Keep results of channels that still exist (matched by URL), so a reload does not blank the table.
        var oldByUrl = _results.Values.GroupBy(r => r.Url).ToDictionary(g => g.Key, g => g.First());
        _results.Clear();
        foreach (var ch in channels)
        {
            _results[ch.Index] = oldByUrl.TryGetValue(ch.Url, out var old)
                ? old with { Index = ch.Index, Name = ch.Name, Group = ch.Group, Logo = ch.TvgLogo }
                : new ChannelCheckResult { Index = ch.Index, Name = ch.Name, Group = ch.Group, Logo = ch.TvgLogo, Url = ch.Url, Status = CheckStatus.Pending };
        }
        _channels = channels;
        LoadError = null;
        Log.Add(Severity.Info, "LOAD", $"Loaded {channels.Count} channel(s) from {Config.Source}");
    }

    /// <summary>Reloads the list and checks all channels (or the given subset). Returns false if a run is already active.</summary>
    public async Task<bool> RunAsync(CancellationToken ct, IReadOnlyCollection<int>? only = null)
    {
        if (!await _runLock.WaitAsync(0, ct)) return false;
        try
        {
            _running = true;
            _progress = 0;
            LastRunStarted = DateTimeOffset.UtcNow;
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var token = _runCts.Token;
            if (only == null)
            {
                try { await LoadAsync(token); }
                catch (Exception ex) when (ex is not OperationCanceledException || !token.IsCancellationRequested)
                {
                    LoadError = ex.Message;
                    Log.Add(Severity.Error, "LOAD", $"Failed to load playlist: {ex.Message}");
                    if (_channels.Count == 0) return true;
                }
            }

            var checker = new HlsChecker(_http, new HlsCheckOptions
            {
                RequestTimeout = TimeSpan.FromSeconds(Config.TimeoutSeconds),
                VariantSelection = Config.VariantSelection,
                CheckLiveness = Config.CheckLiveness,
            });
            var targets = only == null ? _channels : _channels.Where(c => only.Contains(c.Index)).ToList();
            var start = DateTimeOffset.UtcNow;
            await Parallel.ForEachAsync(targets, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, Config.MaxConcurrency), CancellationToken = token },
                async (ch, c) =>
                {
                    var prev = _results.GetValueOrDefault(ch.Index);
                    var res = await checker.CheckAsync(ch, c);
                    _results[ch.Index] = res;
                    Interlocked.Increment(ref _progress);
                    if (prev != null && prev.Status != CheckStatus.Pending && prev.Status != res.Status)
                    {
                        var sev = res.Status == CheckStatus.Error ? Severity.Error : res.Status == CheckStatus.Warning ? Severity.Warning : Severity.Info;
                        Log.Add(sev, "CHANGE", $"{ch.Name}: {prev.Status} → {res.Status}" +
                            (res.Status != CheckStatus.Ok ? $" ({res.Messages.FirstOrDefault(m => m.Severity != Severity.Info)?.Text})" : ""));
                    }
                });
            var all = _results.Values.ToList();
            Log.Add(Severity.Info, "RUN",
                $"Checked {targets.Count} channel(s) in {(DateTimeOffset.UtcNow - start).TotalSeconds:F0} s: " +
                $"{all.Count(r => r.Status == CheckStatus.Ok)} OK, {all.Count(r => r.Status == CheckStatus.Warning)} warning, {all.Count(r => r.Status == CheckStatus.Error)} error");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log.Add(Severity.Warning, "RUN", "Check run cancelled");
            return true;
        }
        finally
        {
            LastRunFinished = DateTimeOffset.UtcNow;
            _running = false;
            _runCts?.Dispose();
            _runCts = null;
            _runLock.Release();
        }
    }

    public void CancelRun() => _runCts?.Cancel();

    public IReadOnlyList<ChannelCheckResult> Results() => _results.Values.OrderBy(r => r.Index).ToList();

    public ChannelCheckResult? Result(int index) => _results.GetValueOrDefault(index);

    public PlaylistSummary Summary()
    {
        var all = _results.Values.ToList();
        return new PlaylistSummary(Id, Config.Name, Config.Source, all.Count,
            all.Count(r => r.Status == CheckStatus.Ok), all.Count(r => r.Status == CheckStatus.Warning),
            all.Count(r => r.Status == CheckStatus.Error), all.Count(r => r.Status == CheckStatus.Pending),
            _running, _progress, LastRunStarted, LastRunFinished, NextRun, LoadError, Config.IntervalMinutes);
    }

    public void Dispose()
    {
        _runCts?.Cancel();
        _http.Dispose();
    }
}
