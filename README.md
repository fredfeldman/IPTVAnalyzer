# IPTV Analyzer

[![CI](https://github.com/fredfeldman/IPTVAnalyzer/actions/workflows/ci.yml/badge.svg)](https://github.com/fredfeldman/IPTVAnalyzer/actions/workflows/ci.yml)

Live monitoring of IPTV **multicast feeds** and health checks for **M3U playlists of HLS channels**, with a web dashboard.
.NET 10, no external dependencies (the dashboard is plain HTML/JS and works on isolated networks).

![Multicast feeds overview](docs/images/multicast-overview.png)

<details>
<summary>More screenshots: feed detail and HLS playlist check</summary>

Feed detail with TR 101 290 counters, MDI and bitrate history, PIDs and events (here an RTP feed with packet loss):

![Multicast feed detail](docs/images/multicast-detail.png)

M3U playlist check with one channel expanded (stalled encoder, 404 and unreachable host flagged as errors):

![HLS playlist check](docs/images/hls-channels.png)

</details>

## Download (Windows)

Get `IPTVAnalyzer-<version>-win-x64.zip` from [Releases](https://github.com/fredfeldman/IPTVAnalyzer/releases), unzip
it anywhere and run `IPTVAnalyzer.Web.exe` (no .NET install needed), then open http://localhost:5080.
The zip also contains the test sender, `IPTVAnalyzer.Sender.exe`. Settings and monitors are stored next to the exe.

## Run from source

```powershell
dotnet run --project src/IPTVAnalyzer.Web
```

Open http://localhost:5080. Add feeds and playlists in the dashboard; they are saved to
`src/IPTVAnalyzer.Web/monitors.json`. To reach the dashboard from other machines, set `"Urls": "http://0.0.0.0:5080"`
in `appsettings.json` (and open the port in the firewall). Feeds can also be seeded in `appsettings.json`.

## Multicast monitoring

Address formats: `239.1.1.1:1234`, `udp://239.1.1.1:1234`, `rtp://10.0.0.5@232.1.1.1:5004` (source-specific / IGMPv3).
Set **Interface IP** when the host has several NICs. Raw UDP and RTP encapsulation are detected automatically.

Per feed:

| Area | What is measured |
|---|---|
| ETSI TR 101 290 P1 | TS sync loss, sync byte error, PAT error (missing > 0.5 s, wrong table id, scrambled), continuity count error, PMT error, PID error (video/audio PID missing > 5 s) |
| ETSI TR 101 290 P2 | Transport error indicator, CRC error (PAT/PMT/SDT), PCR repetition (> 40 ms), PCR discontinuity (> 100 ms without flag), PTS error (> 700 ms) |
| P3 | SDT missing |
| Network | MDI delay factor and media loss rate (RFC 4445), RTP sequence loss/reordering, max packet inter-arrival time, arrival and PCR bitrate |
| Content | Programs, service/provider names (SDT), codecs and languages per PID, scrambling, per-PID bitrate, unreferenced PIDs |

Health: **Error** on any P1 error in the last 10 s, missing sync or a socket error; **Warning** on P2 errors, loss, or
delay factor above `DelayFactorWarnMs` (default 50 ms); **No data** after 1 s without datagrams.

## M3U / HLS checks

The playlist (URL or local file) is reloaded and every channel checked each interval, `MaxConcurrency` at a time:

* HTTP status and response time; redirects followed; configurable User-Agent.
* Master playlist: variants (bandwidth, resolution, codecs), renditions; checks the highest (default), lowest or all variants.
* Media playlist: target duration compliance, live window size, discontinuities, encryption.
* Downloads the newest segment (live) or first segment (VOD): **download ratio** = download time / segment duration
  (≥ 0.5 warning, ≥ 1.0 error: not real-time), throughput, media bitrate.
* TS segments are analyzed (PAT/PMT/SDT, codecs, CC/CRC/TEI errors, PCR). fMP4/CMAF and packed audio are recognized.
* Live playlists are re-fetched after 1.5 × target duration; if the media sequence does not move the channel is
  flagged as a **stalled encoder**.
* Direct MPEG-TS over HTTP streams are sampled for 5 s / 2 MB and analyzed.

Results can be exported as CSV.

## Test sender

Replays a `.ts` file to multicast, paced at the file's PCR bitrate. When looping, CC and PCR values are rewritten so
the stream stays continuous; `--drop` simulates packet loss.

```powershell
dotnet run --project tools/IPTVAnalyzer.Sender -- test.ts 239.10.10.1:1234 --loop
```

```powershell
dotnet run --project tools/IPTVAnalyzer.Sender -- test.ts 239.10.10.2:5004 --rtp --loop --drop 0.002
```

A test file can be made with ffmpeg:

```powershell
ffmpeg -f lavfi -i testsrc2=size=1280x720:rate=25 -f lavfi -i sine -t 30 -c:v libx264 -b:v 3M -g 50 -c:a aac -f mpegts -muxrate 4M -metadata service_name="Test" test.ts
```

## Layout

```
src/IPTVAnalyzer.Core      TS parser + TR 101 290 analyzer, multicast receiver (RTP, MDI), M3U/HLS checker
src/IPTVAnalyzer.Web       ASP.NET Core host: JSON API (/api/...) + dashboard (wwwroot)
tools/IPTVAnalyzer.Sender  Multicast test sender
tests/IPTVAnalyzer.Tests   xUnit tests (synthetic TS streams, fake HTTP server)
```

## Releasing

Push a version tag; the Release workflow tests, publishes self-contained single-file Windows x64 exes, smoke-tests
them and attaches the zip (plus a SHA-256 file) to a GitHub release with generated notes:

```powershell
git tag v1.0.0
```

```powershell
git push origin v1.0.0
```

Tags with a suffix (e.g. `v1.1.0-rc.1`) become pre-releases. Running the workflow manually builds the zip as a
workflow artifact without creating a release.

## Notes and limits

* IPv4 multicast only. PCR accuracy (jitter against arrival time) is not measured: software timestamps are too coarse.
* AES-128/SAMPLE-AES segments are not decrypted, so their content is not inspected.
* On Windows, allow `dotnet.exe` / the app through the firewall for inbound UDP when receiving from other hosts.

## License

[MIT](LICENSE)
