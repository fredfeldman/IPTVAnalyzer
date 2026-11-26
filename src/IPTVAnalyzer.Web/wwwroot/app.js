"use strict";
// IPTV Analyzer dashboard: plain JS, polls the JSON API. All untrusted strings (channel names, URLs,
// service names from the stream) pass through esc() before being placed in HTML.

const $ = (s, el = document) => el.querySelector(s);
const $$ = (s, el = document) => [...el.querySelectorAll(s)];
const esc = v => String(v ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));

const state = {
  tab: localStorage.getItem("tab") || "mc",
  feeds: [], detailId: null, detail: null,
  playlists: [], plId: localStorage.getItem("plId"), channels: [], expanded: new Set(),
  sort: { key: "status", asc: true },
};

// ---------- formatting ----------
function fmtRate(bps) {
  if (bps == null || isNaN(bps)) return "–";
  if (bps >= 1e9) return (bps / 1e9).toFixed(2) + " Gb/s";
  if (bps >= 1e6) return (bps / 1e6).toFixed(2) + " Mb/s";
  if (bps >= 1e3) return (bps / 1e3).toFixed(0) + " kb/s";
  return Math.round(bps) + " b/s";
}
const fmtInt = n => n == null ? "–" : Number(n).toLocaleString();
const fmtMs = n => n == null ? "–" : Number(n).toFixed(n < 10 ? 1 : 0);
const hex = pid => "0x" + pid.toString(16).toUpperCase().padStart(4, "0");
function fmtDur(s) {
  if (s == null) return "–";
  if (s < 60) return Math.round(s) + " s";
  if (s < 3600) return Math.floor(s / 60) + " min";
  return Math.floor(s / 3600) + " h " + Math.floor((s % 3600) / 60) + " min";
}
function fmtAgo(iso) {
  if (!iso) return "–";
  const s = (Date.now() - new Date(iso).getTime()) / 1000;
  return s < 5 ? "just now" : fmtDur(s) + " ago";
}
const fmtTime = iso => new Date(iso).toLocaleTimeString();

const ICONS = { Ok: "✓", Warning: "!", Error: "✕", NoData: "–", Stopped: "■", Pending: "…", Starting: "…" };
const LABELS = { Ok: "OK", Warning: "Warning", Error: "Error", NoData: "No data", Stopped: "Stopped", Pending: "Pending", Starting: "Starting" };
const badge = s => `<span class="status st-${esc(s)}"><span class="ico">${ICONS[s] ?? "?"}</span>${LABELS[s] ?? esc(s)}</span>`;
const sevBadge = s => badge(s === "Error" ? "Error" : s === "Warning" ? "Warning" : "Ok").replace(">OK<", ">Info<");

// ---------- API ----------
async function api(path, opts = {}) {
  const res = await fetch("/api" + path, {
    headers: opts.body ? { "Content-Type": "application/json" } : {}, ...opts,
    body: opts.body ? JSON.stringify(opts.body) : undefined,
  });
  if (!res.ok) {
    let msg = res.status + " " + res.statusText;
    try { const j = await res.json(); if (j.error) msg = j.error; } catch { }
    throw new Error(msg);
  }
  return res.status === 204 || res.status === 202 ? null : res.json();
}
function setConn(ok) {
  const el = $("#conn");
  el.textContent = ok ? "live · " + new Date().toLocaleTimeString() : "connection lost";
  el.classList.toggle("down", !ok);
}

// ---------- tooltip ----------
const tip = $("#tooltip");
function showTip(html, x, y) {
  tip.innerHTML = html;
  tip.classList.remove("hidden");
  const r = tip.getBoundingClientRect();
  let left = x + 14, top = y + 14;
  if (left + r.width > innerWidth - 8) left = x - r.width - 14;
  if (top + r.height > innerHeight - 8) top = y - r.height - 14;
  tip.style.left = left + "px";
  tip.style.top = top + "px";
}
const hideTip = () => tip.classList.add("hidden");

// ---------- line chart (single series, one axis, crosshair + tooltip) ----------
function lineChart(el, samples, { value, format, color, compact = false, label }) {
  const w = el.clientWidth || 300, h = el.clientHeight || (compact ? 42 : 160);
  const pad = compact ? { l: 2, r: 2, t: 4, b: 4 } : { l: 44, r: 10, t: 8, b: 20 };
  const pts = samples.map(s => ({ t: s.unixMs, v: value(s), s }));
  if (pts.length < 2) {
    el.innerHTML = compact ? "" : `<svg viewBox="0 0 ${w} ${h}"><text x="${w / 2}" y="${h / 2}" text-anchor="middle" class="axis" fill="currentColor" style="fill:var(--text-muted);font-size:12px">collecting data…</text></svg>`;
    return;
  }
  const t0 = pts[0].t, t1 = pts[pts.length - 1].t;
  let vmax = Math.max(...pts.map(p => p.v));
  vmax = vmax <= 0 ? 1 : niceMax(vmax);
  const x = t => pad.l + (t - t0) / Math.max(1, t1 - t0) * (w - pad.l - pad.r);
  const y = v => h - pad.b - v / vmax * (h - pad.t - pad.b);
  const path = pts.map((p, i) => (i ? "L" : "M") + x(p.t).toFixed(1) + "," + y(p.v).toFixed(1)).join("");
  let axis = "";
  if (!compact) {
    for (let i = 0; i <= 4; i++) {
      const v = vmax * i / 4, yy = y(v).toFixed(1);
      axis += `<line class="gridline" x1="${pad.l}" x2="${w - pad.r}" y1="${yy}" y2="${yy}"/>` +
        `<text x="${pad.l - 6}" y="${+yy + 3}" text-anchor="end">${format(v, true)}</text>`;
    }
    const mins = Math.round((t1 - t0) / 60000);
    axis += `<text x="${pad.l}" y="${h - 4}">${mins >= 1 ? "−" + mins + " min" : "−" + Math.round((t1 - t0) / 1000) + " s"}</text>` +
      `<text x="${w - pad.r}" y="${h - 4}" text-anchor="end">now</text>`;
  }
  el.innerHTML = `<svg viewBox="0 0 ${w} ${h}" preserveAspectRatio="none">
    <g class="axis">${axis}</g>
    <path d="${path}" fill="none" stroke="${color}" stroke-width="2" stroke-linejoin="round" stroke-linecap="round" vector-effect="non-scaling-stroke"/>
    <line class="crosshair hidden" y1="${pad.t}" y2="${h - pad.b}"/>
    <circle class="hidden" r="4" fill="${color}" stroke="var(--surface-1)" stroke-width="2"/>
    <rect x="0" y="0" width="${w}" height="${h}" fill="transparent"/></svg>`;
  const svg = el.firstElementChild, ch = $("line.crosshair", svg), dot = $("circle", svg), hit = $("rect", svg);
  hit.addEventListener("mousemove", e => {
    const r = svg.getBoundingClientRect();
    const mx = (e.clientX - r.left) / r.width * w;
    let best = pts[0];
    for (const p of pts) if (Math.abs(x(p.t) - mx) < Math.abs(x(best.t) - mx)) best = p;
    const bx = x(best.t), by = y(best.v);
    ch.setAttribute("x1", bx); ch.setAttribute("x2", bx); ch.classList.remove("hidden");
    dot.setAttribute("cx", bx); dot.setAttribute("cy", by); dot.classList.remove("hidden");
    showTip(`<b>${format(best.v)}</b>${label ? " " + esc(label) : ""}<div class="t-sub">${new Date(best.t).toLocaleTimeString()}` +
      (best.s.p1Errors ? ` · ${best.s.p1Errors} P1 err` : "") + (best.s.p2Errors ? ` · ${best.s.p2Errors} P2 err` : "") + `</div>`, e.clientX, e.clientY);
  });
  hit.addEventListener("mouseleave", () => { ch.classList.add("hidden"); dot.classList.add("hidden"); hideTip(); });
}
function niceMax(v) {
  const p = Math.pow(10, Math.floor(Math.log10(v)));
  for (const m of [1, 1.2, 1.5, 2, 2.5, 3, 4, 5, 6, 8, 10]) if (m * p >= v * 1.05) return m * p;
  return 10 * p;
}
const css = name => getComputedStyle(document.documentElement).getPropertyValue(name).trim();
const mbps = (v, axis) => axis ? (v / 1e6).toFixed(v >= 1e7 ? 0 : 1) : fmtRate(v);
const msFmt = (v, axis) => axis ? v.toFixed(v >= 10 ? 0 : 1) : v.toFixed(1) + " ms";

// ---------- tabs / theme ----------
function setTab(t) {
  state.tab = t;
  localStorage.setItem("tab", t);
  $$(".tab").forEach(b => b.classList.toggle("active", b.dataset.tab === t));
  $$(".tab-panel").forEach(p => p.classList.toggle("active", p.id === "tab-" + t));
  if (t === "hls") refreshPlaylists(); else refreshFeeds();
}
$$(".tab").forEach(b => b.addEventListener("click", () => setTab(b.dataset.tab)));
function applyTheme(t) { document.documentElement.dataset.theme = t; localStorage.setItem("theme", t); }
applyTheme(localStorage.getItem("theme") || (matchMedia("(prefers-color-scheme: light)").matches ? "light" : "dark"));
$("#theme-toggle").addEventListener("click", () => {
  applyTheme(document.documentElement.dataset.theme === "light" ? "dark" : "light");
  renderFeeds(); if (state.detail) renderDetail();
});

// =====================================================================
// Multicast
// =====================================================================
async function refreshFeeds() {
  try {
    state.feeds = await api("/multicast");
    setConn(true);
    renderFeeds();
  } catch { setConn(false); }
}

function renderFeeds() {
  const feeds = state.feeds;
  const by = h => feeds.filter(f => f.health === h).length;
  $("#mc-count").textContent = feeds.length ? `${feeds.length}` : "";
  $("#mc-empty").classList.toggle("hidden", feeds.length > 0);
  $("#mc-summary").innerHTML = feeds.length ? ["Ok", "Warning", "Error", "NoData"].map(h => `<span class="pill">${badge(h)} ${by(h)}</span>`).join("") : "";
  const grid = $("#mc-grid");
  // Keep card elements keyed by id so sparklines don't flicker.
  const existing = new Map($$(".card", grid).map(c => [c.dataset.id, c]));
  for (const f of feeds) {
    let card = existing.get(f.id);
    if (!card) {
      card = document.createElement("div");
      card.className = "card";
      card.dataset.id = f.id;
      card.innerHTML = `<div class="card-head"><div style="min-width:0"><div class="card-name"></div><div class="card-addr"></div></div><div class="st"></div></div>
        <div class="card-rate"></div><div class="card-reason"></div><div class="spark"></div><div class="card-metrics"></div><div class="card-services"></div>`;
      card.addEventListener("click", () => openDetail(f.id));
      grid.appendChild(card);
    }
    existing.delete(f.id);
    card.className = "card h-" + f.health;
    $(".card-name", card).textContent = f.name;
    $(".card-name", card).title = f.name;
    $(".card-addr", card).textContent = f.address + (f.interface ? " via " + f.interface : "") + " · " + f.transport;
    $(".st", card).innerHTML = badge(f.health);
    const rate = f.health === "NoData" ? 0 : f.ts.bitrateBps;
    $(".card-rate", card).innerHTML = `${esc(fmtRate(rate).split(" ")[0])} <small>${esc(fmtRate(rate).split(" ")[1] ?? "")}</small>`;
    $(".card-reason", card).textContent = f.healthReason ?? "";
    lineChart($(".spark", card), f.history ?? [], { value: s => s.bitrateBps, format: mbps, color: css("--series-1"), compact: true, label: "" });
    const m = (k, v, bad) => `<div class="metric">${k}<b class="${bad ? "bad" : ""}">${v}</b></div>`;
    $(".card-metrics", card).innerHTML =
      m("P1 errors", fmtInt(f.ts.priority1Errors), f.ts.priority1Errors > 0) +
      m("P2 errors", fmtInt(f.ts.priority2Errors), f.ts.priority2Errors > 0) +
      m("MDI DF", fmtMs(f.delayFactorMs) + " ms", false) +
      m("MLR", fmtMs(f.mediaLossRate), f.mediaLossRate > 0);
    const svc = f.ts.programs.map(p => p.serviceName || "Program " + p.programNumber);
    $(".card-services", card).textContent = svc.length ? svc.join(" · ") : (f.datagrams ? "Waiting for PSI…" : "");
  }
  for (const c of existing.values()) c.remove();
}

$("#mc-add").addEventListener("submit", async e => {
  e.preventDefault();
  const fd = new FormData(e.target);
  $("#mc-add-error").textContent = "";
  try {
    await api("/multicast", { method: "POST", body: { address: fd.get("address"), name: fd.get("name") || null, interface: fd.get("interface") || null } });
    e.target.reset();
    refreshFeeds();
  } catch (err) { $("#mc-add-error").textContent = err.message; }
});

// ---------- detail drawer ----------
async function openDetail(id) {
  state.detailId = id;
  $("#drawer").classList.remove("hidden");
  await refreshDetail();
}
function closeDetail() { state.detailId = null; state.detail = null; $("#drawer").classList.add("hidden"); hideTip(); }
$("#d-close").addEventListener("click", closeDetail);
document.addEventListener("keydown", e => { if (e.key === "Escape") closeDetail(); });
$("#d-reset").addEventListener("click", async () => { await api(`/multicast/${state.detailId}/reset`, { method: "POST" }); refreshDetail(); });
$("#d-delete").addEventListener("click", async () => {
  if (!state.detail || !confirm(`Stop monitoring ${state.detail.name}?`)) return;
  await api(`/multicast/${state.detailId}`, { method: "DELETE" });
  closeDetail(); refreshFeeds();
});

async function refreshDetail() {
  if (!state.detailId) return;
  try { state.detail = await api(`/multicast/${state.detailId}`); renderDetail(); }
  catch { closeDetail(); }
}

const TR = {
  1: ["TS_sync_loss", "Sync_byte_error", "PAT_error", "Continuity_count_error", "PMT_error", "PID_error"],
  2: ["Transport_error", "CRC_error", "PCR_repetition_error", "PCR_discontinuity_indicator_error", "PTS_error"],
  3: ["SDT_error"],
};

function renderDetail() {
  const d = state.detail, ts = d.ts;
  $("#d-title").innerHTML = `${esc(d.name)} ${badge(d.health)}`;
  $("#d-sub").textContent = `${d.address}${d.interface ? " via " + d.interface : ""} · ${d.transport}` +
    (ts.transportStreamId != null ? ` · TSID ${ts.transportStreamId}` : "") + (d.healthReason ? " · " + d.healthReason : "");
  const k = (label, v) => `<div class="kpi"><div class="k">${label}</div><div class="v">${v}</div></div>`;
  $("#d-kpis").innerHTML =
    k("Bitrate (arrival)", fmtRate(d.health === "NoData" ? 0 : ts.bitrateBps)) +
    k("Bitrate (PCR)", fmtRate(ts.pcrBitrateBps)) +
    k("MDI DF now / max", `${fmtMs(d.delayFactorMs)} / ${fmtMs(d.maxDelayFactorMs)} ms`) +
    k("MLR now / max", `${fmtMs(d.mediaLossRate)} / ${fmtMs(d.maxMediaLossRate)}`) +
    k("Max inter-arrival", fmtMs(d.maxInterArrivalMs) + " ms") +
    (d.transport === "RTP" ? k("RTP lost / reordered", `${fmtInt(d.rtpLost)} / ${fmtInt(d.rtpOutOfOrder)}`) : "") +
    k("TS packets", fmtInt(ts.totalPackets)) +
    k("Datagrams", fmtInt(d.datagrams)) +
    k("Uptime", fmtDur(d.uptimeSeconds));

  lineChart($("#d-chart-rate"), d.history, { value: s => s.bitrateBps, format: mbps, color: css("--series-1") });
  lineChart($("#d-chart-df"), d.history, { value: s => s.delayFactorMs, format: msFmt, color: css("--series-2"), label: "delay factor" });

  $("#d-tr").innerHTML = [1, 2, 3].map(p => `<div class="tr-col"><h4>Priority ${p}</h4>` +
    TR[p].map(key => {
      const n = ts.errors[key] ?? 0;
      return `<div class="tr-row"><span>${key.replace(/_/g, " ")}</span><span class="cnt ${n ? "bad" : ""}">${n ? badge("Error").replace("Error<", fmtInt(n) + "<") : badge("Ok").replace("OK<", "0<")}</span></div>`;
    }).join("") + "</div>").join("");

  $("#d-programs").innerHTML = ts.programs.length ? ts.programs.map(p => `<div class="prog">
      <div class="prog-head"><b>${esc(p.serviceName || "Program " + p.programNumber)}</b>
        <span class="muted small">${p.providerName ? esc(p.providerName) + " · " : ""}program ${p.programNumber} · PMT ${hex(p.pmtPid)} · PCR ${p.pcrPid >= 0 ? hex(p.pcrPid) : "–"}</span>
        ${p.encrypted ? '<span class="tag">scrambled</span>' : ""}</div>
      <div class="prog-streams">${p.streams.map(s => `<span class="es ${s.present ? "" : "missing"}" title="${s.present ? "" : "missing"}">
        ${hex(s.pid)} ${esc(s.codec)}${s.language ? " [" + esc(s.language) + "]" : ""} · ${fmtRate(s.bitrateBps)}${s.present ? "" : " · missing"}</span>`).join("") || '<span class="muted small">waiting for PMT…</span>'}</div>
    </div>`).join("") : `<div class="muted small">${d.datagrams ? "Waiting for PAT…" : "No data received yet."}</div>`;

  $("#d-pids").innerHTML = ts.pids.map(p => `<tr>
      <td class="num">${hex(p.pid)}</td><td>${esc(p.description)}</td><td class="num">${fmtInt(p.packets)}</td>
      <td class="num">${fmtRate(p.bitrateBps)}</td><td class="num">${p.ccErrors || ""}</td><td class="num">${p.transportErrors || ""}</td>
      <td class="num">${p.maxPcrIntervalMs != null ? fmtMs(p.maxPcrIntervalMs) + " ms" : ""}</td>
      <td>${p.hasPcr ? '<span class="tag">PCR</span> ' : ""}${p.scrambled ? '<span class="tag">scrambled</span> ' : ""}${p.referenced ? "" : '<span class="tag">unreferenced</span>'}${p.secondsSinceSeen > 2 ? ` <span class="tag">idle ${Math.round(p.secondsSinceSeen)} s</span>` : ""}</td>
    </tr>`).join("");

  renderEvents($("#d-events"), d.events);
}

function renderEvents(ul, events) {
  ul.innerHTML = (events ?? []).map(e => `<li><span class="time">${fmtTime(e.time)}</span><span>${sevBadge(e.severity)}</span>
    <span>${esc(e.message)}${e.repeat > 1 ? ` <span class="rep">×${e.repeat}</span>` : ""}</span></li>`).join("") || '<li><span></span><span></span><span class="muted">No events</span></li>';
}

// =====================================================================
// HLS / M3U
// =====================================================================
async function refreshPlaylists() {
  try {
    state.playlists = await api("/playlists");
    setConn(true);
  } catch { setConn(false); return; }
  if (!state.playlists.find(p => p.id === state.plId)) state.plId = state.playlists[0]?.id ?? null;
  renderPlaylistList();
  if (state.plId) await refreshChannels();
  else { $("#pl-view").classList.add("hidden"); $("#pl-none").classList.remove("hidden"); }
}

function renderPlaylistList() {
  const total = state.playlists.reduce((a, p) => a + p.error, 0);
  $("#hls-count").textContent = state.playlists.length ? (total ? `${total} err` : `${state.playlists.length}`) : "";
  $("#pl-list").innerHTML = state.playlists.map(p => `<li data-id="${esc(p.id)}" class="${p.id === state.plId ? "active" : ""}">
      <div class="pl-name">${esc(p.name)}</div>
      <div class="pl-counts"><span>${p.channels} ch</span><span>${p.ok} ok</span><span>${p.warning} warn</span><span>${p.error} err</span>${p.running ? "<span>checking…</span>" : ""}</div></li>`).join("");
  $$("#pl-list li").forEach(li => li.addEventListener("click", () => {
    state.plId = li.dataset.id; localStorage.setItem("plId", state.plId); state.expanded.clear(); refreshPlaylists();
  }));
}

async function refreshChannels() {
  const p = state.playlists.find(x => x.id === state.plId);
  if (!p) return;
  try {
    const [chs, events] = await Promise.all([api(`/playlists/${p.id}/channels`), api(`/playlists/${p.id}/events`)]);
    state.channels = chs;
    renderPlaylist(p, events);
  } catch { setConn(false); }
}

function renderPlaylist(p, events) {
  $("#pl-none").classList.add("hidden");
  $("#pl-view").classList.remove("hidden");
  $("#pl-title").textContent = p.name;
  $("#pl-sub").textContent = `${p.source} · every ${p.intervalMinutes || "–"} min · last run ${fmtAgo(p.lastRunFinished)}` +
    (p.nextRun && !p.running ? ` · next in ${fmtDur(Math.max(0, (new Date(p.nextRun) - Date.now()) / 1000))}` : "") +
    (p.loadError ? ` · load error: ${p.loadError}` : "");
  $("#pl-csv").href = `/api/playlists/${p.id}/report.csv`;
  $("#pl-run").disabled = p.running;
  $("#pl-cancel").classList.toggle("hidden", !p.running);
  const prog = $("#pl-progress");
  prog.classList.toggle("hidden", !p.running);
  if (p.running) {
    const pct = p.channels ? Math.round(p.progress / p.channels * 100) : 0;
    $(".bar", prog).style.width = pct + "%";
    $("span", prog).textContent = `Checking… ${p.progress} / ${p.channels}`;
  }
  $("#pl-stats").innerHTML = [["Ok", p.ok], ["Warning", p.warning], ["Error", p.error], ["Pending", p.pending]]
    .map(([s, n]) => `<span class="pill">${badge(s)} ${n}</span>`).join("");

  // group filter options
  const groups = [...new Set(state.channels.map(c => c.group).filter(Boolean))].sort();
  const gSel = $("#f-group"), cur = gSel.value;
  gSel.innerHTML = '<option value="">All groups</option>' + groups.map(g => `<option ${g === cur ? "selected" : ""}>${esc(g)}</option>`).join("");
  renderChannels();
  renderEvents($("#pl-events"), events);
}

const statusRank = { Error: 0, Warning: 1, Pending: 2, Ok: 3 };
const sorters = {
  status: c => statusRank[c.status], name: c => c.name.toLowerCase(), group: c => (c.group ?? "").toLowerCase(), kind: c => c.kind,
  http: c => c.httpStatus ?? 999, resp: c => c.responseMs ?? 1e9, ratio: c => c.downloadRatio ?? -1, rate: c => c.mediaBitrateBps ?? -1,
  checked: c => c.status === "Pending" ? 0 : new Date(c.checkedAt).getTime(),
};

function renderChannels() {
  const text = $("#f-text").value.trim().toLowerCase(), st = $("#f-status").value, grp = $("#f-group").value;
  let rows = state.channels.filter(c =>
    (!st || c.status === st) && (!grp || c.group === grp) &&
    (!text || c.name.toLowerCase().includes(text) || (c.group ?? "").toLowerCase().includes(text) || c.url.toLowerCase().includes(text)));
  const { key, asc } = state.sort, f = sorters[key];
  rows.sort((a, b) => { const x = f(a), y = f(b); return (x < y ? -1 : x > y ? 1 : a.index - b.index) * (asc ? 1 : -1); });
  $$(".ch-table th[data-sort]").forEach(th => { th.classList.toggle("sorted", th.dataset.sort === key); th.classList.toggle("asc", th.dataset.sort === key && asc); });
  $("#f-count").textContent = `${rows.length} of ${state.channels.length}`;

  const html = [];
  for (const c of rows.slice(0, 2000)) {
    const issue = c.messages.find(m => m.severity === "Error") ?? c.messages.find(m => m.severity === "Warning");
    const v = c.selectedVariant;
    html.push(`<tr class="ch-row" data-i="${c.index}">
      <td>${badge(c.status)}</td><td class="name" title="${esc(c.name)}">${esc(c.name)}</td><td>${esc(c.group ?? "")}</td>
      <td>${esc(c.status === "Pending" ? "" : (c.container ? c.container : c.kind))}</td>
      <td class="num">${c.httpStatus ?? ""}</td><td class="num">${c.responseMs ?? ""}</td>
      <td>${v ? esc((v.resolution ?? "") + " " + Math.round(v.bandwidth / 1000) + "k") : ""}${c.variants.length > 1 ? ` <span class="muted small">(${c.variants.length})</span>` : ""}</td>
      <td class="num">${c.downloadRatio != null ? c.downloadRatio.toFixed(2) : ""}</td>
      <td class="num">${c.mediaBitrateBps != null ? fmtRate(c.mediaBitrateBps) : ""}</td>
      <td>${c.isLive == null ? "" : c.isLive ? (c.playlistAdvancing === false ? "stalled" : c.playlistAdvancing ? "live ✓" : "live") : "VOD"}</td>
      <td class="issue" title="${esc(issue?.text ?? "")}">${esc(issue?.text ?? "")}</td>
      <td class="muted small">${c.status === "Pending" ? "" : fmtAgo(c.checkedAt)}</td>
      <td><button class="btn ghost small recheck" data-i="${c.index}" title="Re-check this channel">↻</button></td></tr>`);
    if (state.expanded.has(c.index)) html.push(`<tr class="ch-detail"><td colspan="13">${channelDetail(c)}</td></tr>`);
  }
  $("#ch-body").innerHTML = html.join("") || '<tr><td colspan="13" class="muted">No channels match.</td></tr>';
}

function channelDetail(c) {
  const dl = pairs => "<dl>" + pairs.filter(([, v]) => v != null && v !== "").map(([k, v]) => `<dt>${k}</dt><dd>${esc(v)}</dd>`).join("") + "</dl>";
  return `<div class="detail-grid"><div><ul class="msgs">${c.messages.map(m => `<li>${sevBadge(m.severity)}<span>${esc(m.text)}</span></li>`).join("")}</ul></div><div>${dl([
    ["URL", c.url], ["Type", c.kind], ["Container", c.container], ["Encryption", c.encryption],
    ["Service", c.serviceName], ["Target duration", c.targetDuration != null ? c.targetDuration + " s" : null],
    ["Segments", c.segmentCount], ["Media sequence", c.mediaSequence],
    ["Segment", c.segmentBytes != null ? `${fmtInt(c.segmentBytes)} bytes, ${c.segmentDuration ?? "?"} s, downloaded in ${c.segmentDownloadMs ?? "?"} ms` : null],
    ["Throughput", c.throughputBps != null ? fmtRate(c.throughputBps) : null],
    ["Streams", c.streams.join(", ")],
    ["Variants", c.variants.map(v => `${Math.round(v.bandwidth / 1000)}k ${v.resolution ?? ""} ${v.codecs ?? ""}`.trim()).join("; ")],
    ["Check took", c.durationMs + " ms"],
  ])}</div></div>`;
}

$("#ch-body").addEventListener("click", async e => {
  const btn = e.target.closest(".recheck");
  if (btn) {
    e.stopPropagation();
    btn.disabled = true; btn.textContent = "…";
    try { await api(`/playlists/${state.plId}/channels/${btn.dataset.i}/check`, { method: "POST" }); }
    catch (err) { alert(err.message); }
    refreshPlaylists();
    return;
  }
  const row = e.target.closest("tr.ch-row");
  if (!row) return;
  const i = +row.dataset.i;
  state.expanded.has(i) ? state.expanded.delete(i) : state.expanded.add(i);
  renderChannels();
});
$$(".ch-table th[data-sort]").forEach(th => th.addEventListener("click", () => {
  const k = th.dataset.sort;
  state.sort = { key: k, asc: state.sort.key === k ? !state.sort.asc : true };
  renderChannels();
}));
["#f-text", "#f-status", "#f-group"].forEach(s => $(s).addEventListener("input", renderChannels));

$("#pl-run").addEventListener("click", async () => {
  try { await api(`/playlists/${state.plId}/run`, { method: "POST" }); } catch (err) { alert(err.message); }
  setTimeout(refreshPlaylists, 400);
});
$("#pl-cancel").addEventListener("click", async () => { await api(`/playlists/${state.plId}/cancel`, { method: "POST" }); refreshPlaylists(); });
$("#pl-delete").addEventListener("click", async () => {
  const p = state.playlists.find(x => x.id === state.plId);
  if (!p || !confirm(`Remove playlist ${p.name}?`)) return;
  await api(`/playlists/${p.id}`, { method: "DELETE" });
  state.plId = null; refreshPlaylists();
});
$("#pl-add").addEventListener("submit", async e => {
  e.preventDefault();
  const fd = new FormData(e.target);
  $("#pl-add-error").textContent = "";
  try {
    const p = await api("/playlists", { method: "POST", body: {
      source: fd.get("source"), name: fd.get("name") || "", intervalMinutes: +fd.get("intervalMinutes"),
      maxConcurrency: +fd.get("maxConcurrency"), variantSelection: fd.get("variantSelection"),
      timeoutSeconds: +fd.get("timeoutSeconds"), userAgent: fd.get("userAgent") || null, checkLiveness: fd.get("checkLiveness") === "on",
    } });
    e.target.reset();
    state.plId = p.id; localStorage.setItem("plId", p.id);
    setTimeout(refreshPlaylists, 300);
  } catch (err) { $("#pl-add-error").textContent = err.message; }
});

// ---------- polling ----------
let tick = 0;
setInterval(() => {
  tick++;
  if (document.hidden) return;
  if (state.tab === "mc") { refreshFeeds(); if (state.detailId) refreshDetail(); }
  else {
    const running = state.playlists.some(p => p.running);
    if (running || tick % 5 === 0) refreshPlaylists();
  }
}, 1000);
addEventListener("resize", () => { renderFeeds(); if (state.detail) renderDetail(); });
setTab(state.tab);
