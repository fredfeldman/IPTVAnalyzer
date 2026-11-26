using System.Text;
using System.Text.Json.Serialization;
using IPTVAnalyzer.Core.Hls;
using IPTVAnalyzer.Core.Multicast;
using IPTVAnalyzer.Web.Services;

var builder = WebApplication.CreateBuilder(args);

var settings = builder.Configuration.GetSection("IPTVAnalyzer").Get<AnalyzerSettings>() ?? new AnalyzerSettings();
builder.Services.AddSingleton(settings);
builder.Services.AddSingleton<MonitorRegistry>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<MonitorRegistry>());
builder.Services.AddHostedService<PlaylistScheduler>();
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    o.SerializerOptions.NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals;
});

var app = builder.Build();
app.UseDefaultFiles();
app.UseStaticFiles();

var api = app.MapGroup("/api");

// ---------- Multicast ----------
api.MapGet("/multicast", (MonitorRegistry r) => r.Feeds.Select(f => f.Snapshot(includeDetails: false)));

api.MapGet("/multicast/{id}", (string id, MonitorRegistry r) =>
    r.Feed(id) is { } f ? Results.Ok(f.Snapshot(includeDetails: true)) : Results.NotFound());

api.MapPost("/multicast", (AddFeedRequest req, MonitorRegistry r) =>
{
    try
    {
        MulticastFeedConfig cfg;
        if (!string.IsNullOrWhiteSpace(req.Address)) cfg = MulticastFeedConfig.Parse(req.Address, req.Name);
        else cfg = new MulticastFeedConfig { Group = req.Group ?? "", Port = req.Port ?? 0, Source = req.Source, Name = req.Name ?? "" };
        if (!string.IsNullOrWhiteSpace(req.Source)) cfg.Source = req.Source;
        cfg.Interface = string.IsNullOrWhiteSpace(req.Interface) ? null : req.Interface.Trim();
        if (req.NoDataTimeoutSeconds is > 0) cfg.NoDataTimeoutSeconds = req.NoDataTimeoutSeconds.Value;
        cfg.Validate();
        var m = r.AddFeed(cfg);
        return Results.Created($"/api/multicast/{m.Id}", m.Snapshot(false));
    }
    catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

api.MapDelete("/multicast/{id}", async (string id, MonitorRegistry r) =>
    await r.RemoveFeedAsync(id) ? Results.NoContent() : Results.NotFound());

api.MapPost("/multicast/{id}/reset", (string id, MonitorRegistry r) =>
{
    if (r.Feed(id) is not { } f) return Results.NotFound();
    f.ResetCounters();
    return Results.NoContent();
});

// ---------- Playlists ----------
api.MapGet("/playlists", (MonitorRegistry r) => r.Playlists.Select(p => p.Summary()));

api.MapPost("/playlists", (PlaylistConfig cfg, MonitorRegistry r) =>
{
    if (string.IsNullOrWhiteSpace(cfg.Source)) return Results.BadRequest(new { error = "Source (URL or file path) is required" });
    cfg.Id = null;
    cfg.Source = cfg.Source.Trim();
    cfg.MaxConcurrency = Math.Clamp(cfg.MaxConcurrency <= 0 ? 8 : cfg.MaxConcurrency, 1, 64);
    cfg.TimeoutSeconds = Math.Clamp(cfg.TimeoutSeconds <= 0 ? 15 : cfg.TimeoutSeconds, 2, 120);
    var p = r.AddPlaylist(cfg);
    return Results.Created($"/api/playlists/{p.Id}", p.Summary());
});

api.MapDelete("/playlists/{id}", (string id, MonitorRegistry r) => r.RemovePlaylist(id) ? Results.NoContent() : Results.NotFound());

api.MapPost("/playlists/{id}/run", (string id, MonitorRegistry r, IHostApplicationLifetime life) =>
{
    if (r.Playlist(id) is not { } p) return Results.NotFound();
    if (p.Running) return Results.Conflict(new { error = "A check run is already in progress" });
    _ = Task.Run(() => p.RunAsync(life.ApplicationStopping));
    return Results.Accepted();
});

api.MapPost("/playlists/{id}/cancel", (string id, MonitorRegistry r) =>
{
    if (r.Playlist(id) is not { } p) return Results.NotFound();
    p.CancelRun();
    return Results.NoContent();
});

api.MapGet("/playlists/{id}/channels", (string id, MonitorRegistry r) =>
    r.Playlist(id) is { } p ? Results.Ok(p.Results()) : Results.NotFound());

api.MapPost("/playlists/{id}/channels/{index:int}/check", async (string id, int index, MonitorRegistry r, IHostApplicationLifetime life) =>
{
    if (r.Playlist(id) is not { } p) return Results.NotFound();
    if (p.Result(index) is null) return Results.NotFound();
    if (!await p.RunAsync(life.ApplicationStopping, [index])) return Results.Conflict(new { error = "A check run is already in progress" });
    return Results.Ok(p.Result(index));
});

api.MapGet("/playlists/{id}/events", (string id, MonitorRegistry r) =>
    r.Playlist(id) is { } p ? Results.Ok(p.Log.Snapshot(300)) : Results.NotFound());

api.MapGet("/playlists/{id}/report.csv", (string id, MonitorRegistry r) =>
{
    if (r.Playlist(id) is not { } p) return Results.NotFound();
    var sb = new StringBuilder();
    sb.AppendLine("index,name,group,status,kind,http,response_ms,variant_kbps,resolution,container,target_duration,live,advancing,download_ratio,media_kbps,streams,checked_at,issues,url");
    foreach (var c in p.Results())
    {
        var issues = string.Join(" | ", c.Messages.Where(m => m.Severity != IPTVAnalyzer.Core.Common.Severity.Info).Select(m => m.Text));
        sb.AppendLine(string.Join(",",
            c.Index, Csv(c.Name), Csv(c.Group), c.Status, Csv(c.Kind), c.HttpStatus, c.ResponseMs,
            c.SelectedVariant is { } v ? v.Bandwidth / 1000 : null, Csv(c.SelectedVariant?.Resolution), Csv(c.Container),
            c.TargetDuration, c.IsLive, c.PlaylistAdvancing, c.DownloadRatio,
            c.MediaBitrateBps is { } b ? Math.Round(b / 1000) : null, Csv(string.Join("; ", c.Streams)),
            c.Status == CheckStatus.Pending ? "" : c.CheckedAt.ToString("u"), Csv(issues), Csv(c.Url)));
    }
    var name = string.Concat(p.Config.Name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
    return Results.File(Encoding.UTF8.GetBytes(sb.ToString()), "text/csv", $"{name}-report.csv");
});

app.Run();

static string Csv(string? s) => s == null ? "" : "\"" + s.Replace("\"", "\"\"") + "\"";

public sealed record AddFeedRequest(string? Address, string? Name, string? Group, int? Port, string? Source, string? Interface, double? NoDataTimeoutSeconds);
