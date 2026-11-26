using System.Collections.Concurrent;
using System.Text.Json;
using IPTVAnalyzer.Core.Hls;
using IPTVAnalyzer.Core.Multicast;

namespace IPTVAnalyzer.Web.Services;

public sealed class AnalyzerSettings
{
    public List<MulticastFeedConfig> Multicast { get; set; } = [];
    public List<PlaylistConfig> Playlists { get; set; } = [];
    public double DelayFactorWarnMs { get; set; } = 50;
    /// <summary>File where feeds/playlists added from the dashboard are persisted. Relative to the content root.</summary>
    public string StateFile { get; set; } = "monitors.json";
}

public sealed class PersistedState
{
    public List<MulticastFeedConfig> Multicast { get; set; } = [];
    public List<PlaylistConfig> Playlists { get; set; } = [];
}

/// <summary>Owns all multicast and playlist monitors; persists their configuration.</summary>
public sealed class MonitorRegistry : IHostedService, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly ConcurrentDictionary<string, MulticastMonitor> _feeds = new();
    private readonly ConcurrentDictionary<string, PlaylistMonitor> _playlists = new();
    private readonly AnalyzerSettings _settings;
    private readonly string _statePath;
    private readonly ILogger<MonitorRegistry> _logger;
    private readonly object _saveGate = new();

    public MonitorRegistry(AnalyzerSettings settings, IHostEnvironment env, ILogger<MonitorRegistry> logger)
    {
        _settings = settings;
        _logger = logger;
        _statePath = Path.IsPathRooted(settings.StateFile) ? settings.StateFile : Path.Combine(env.ContentRootPath, settings.StateFile);
    }

    public IEnumerable<MulticastMonitor> Feeds => _feeds.Values.OrderBy(f => f.Config.Name, StringComparer.OrdinalIgnoreCase);
    public IEnumerable<PlaylistMonitor> Playlists => _playlists.Values.OrderBy(p => p.Config.Name, StringComparer.OrdinalIgnoreCase);
    public MulticastMonitor? Feed(string id) => _feeds.GetValueOrDefault(id);
    public PlaylistMonitor? Playlist(string id) => _playlists.GetValueOrDefault(id);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        PersistedState state;
        if (File.Exists(_statePath))
        {
            try { state = JsonSerializer.Deserialize<PersistedState>(File.ReadAllText(_statePath)) ?? new(); }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Could not read {Path}; falling back to appsettings", _statePath);
                state = new PersistedState { Multicast = _settings.Multicast, Playlists = _settings.Playlists };
            }
        }
        else state = new PersistedState { Multicast = _settings.Multicast, Playlists = _settings.Playlists };

        foreach (var f in state.Multicast)
        {
            try { AddFeed(f, save: false); }
            catch (Exception ex) { _logger.LogError("Skipping multicast feed {Name}: {Error}", f.Name, ex.Message); }
        }
        foreach (var p in state.Playlists)
        {
            try { AddPlaylist(p, save: false); }
            catch (Exception ex) { _logger.LogError("Skipping playlist {Name}: {Error}", p.Name, ex.Message); }
        }
        _logger.LogInformation("Loaded {Feeds} multicast feed(s) and {Playlists} playlist(s)", _feeds.Count, _playlists.Count);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await Task.WhenAll(_feeds.Values.Select(f => f.StopAsync()));
        foreach (var p in _playlists.Values) p.CancelRun();
    }

    public MulticastMonitor AddFeed(MulticastFeedConfig cfg, bool save = true)
    {
        if (string.IsNullOrWhiteSpace(cfg.Name)) cfg.Name = $"{cfg.Group}:{cfg.Port}";
        if (_feeds.Values.Any(f => f.Config.Address == cfg.Address && f.Config.Interface == cfg.Interface))
            throw new InvalidOperationException($"{cfg.Address} is already monitored");
        var m = new MulticastMonitor(cfg, new MulticastMonitorOptions { DelayFactorWarnMs = _settings.DelayFactorWarnMs });
        _feeds[m.Id] = m;
        m.Start();
        if (save) Save();
        return m;
    }

    public async Task<bool> RemoveFeedAsync(string id)
    {
        if (!_feeds.TryRemove(id, out var m)) return false;
        await m.StopAsync();
        Save();
        return true;
    }

    public PlaylistMonitor AddPlaylist(PlaylistConfig cfg, bool save = true)
    {
        var p = new PlaylistMonitor(cfg) { NextRun = DateTimeOffset.UtcNow };
        _playlists[p.Id] = p;
        if (save) Save();
        return p;
    }

    public bool RemovePlaylist(string id)
    {
        if (!_playlists.TryRemove(id, out var p)) return false;
        p.Dispose();
        Save();
        return true;
    }

    public void Save()
    {
        var state = new PersistedState
        {
            Multicast = _feeds.Values.Select(f => f.Config).ToList(),
            Playlists = _playlists.Values.Select(p => p.Config).ToList(),
        };
        lock (_saveGate)
        {
            try
            {
                var tmp = _statePath + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(state, JsonOpts));
                File.Move(tmp, _statePath, overwrite: true);
            }
            catch (Exception ex) { _logger.LogError(ex, "Could not save {Path}", _statePath); }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var f in _feeds.Values) await f.DisposeAsync();
        foreach (var p in _playlists.Values) p.Dispose();
    }
}
