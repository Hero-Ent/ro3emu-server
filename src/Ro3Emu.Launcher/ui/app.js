/* RO3Emu Launcher page (layout after the FatesEnd launcher).
   Host -> page: state {phase, installed, remote, message, downloadBytes}, progress {stage, done, total, rate, eta, file},
   server {online, players}, settings {dir, launcher, serverName, links, canUninstall}, news {items}, hero {url},
   notes {version, items}, shortcuts {status: ask|done|error, created, failed}, uninstall {status: done|error, message}.
   Page -> host: ready, primary, pause, repair, changeDir, openDir, openUrl {url}, window {action}, shortcuts {desktop,
   startMenu}, notesSeen, uninstall.
   Inside the launcher the host is WebView2 (window.chrome.webview). In a plain browser a mock host simulates it:
   index.html?mock=install|update|ready|downloading|repair|offline|error|checking|running (default update),
   &panel=settings opens Settings, &notes=1 the What's new sheet, &first=1 the shortcuts question,
   &hero=<image url> shows that image (default: the built-in hero), &links=0 hides the mock links, &fast=10 speeds the
   fake transfer up. */
(function () {
  "use strict";

  var $ = function (id) { return document.getElementById(id); };
  var host = null;

  var S = { phase: "checking", installed: null, remote: null, message: null, downloadBytes: null, progress: null,
    news: null, server: null, settings: {} };
  var primaryLock = false;
  var pausedByUser = false;   // the player pressed Pause: the next install/update offer reads "Resume"

  // Shown until the repository's news.json arrives (or when there is none).
  var DEFAULT_NEWS = [
    { tag: "Welcome", color: "blue", title: null, text: "Our own Ragnarok Online 3 server. Everything is still being built, so report anything that breaks: Menu → Feedback in the game." },
    { tag: "Account", color: "green", title: "No registration needed", text: "Type any username and password in the game. The account is created on your first login." },
    { tag: "Updates", color: "orange", title: "Automatic game updates", text: "New versions download here. Interrupted downloads resume where they stopped." }
  ];
  var COLORS = { blue: "var(--chip-blue)", green: "var(--chip-green)", orange: "var(--chip-orange)", red: "var(--chip-red)", grey: "var(--chip-grey)", gray: "var(--chip-grey)" };
  var STAGES = { scanning: "Checking files", downloading: "Downloading", assembling: "Assembling files", verifying: "Verifying", committing: "Finishing up" };

  function send(o) { if (host) host.post(o); }

  function handle(m) {
    if (typeof m === "string") { try { m = JSON.parse(m); } catch (e) { return; } }
    if (!m || typeof m !== "object") return;
    switch (m.type) {
      case "state":
        var wasTransfer = isTransfer(S.phase);
        S.phase = m.phase || "checking";
        S.installed = m.installed || null;
        S.remote = m.remote || null;
        S.message = m.message || null;
        S.downloadBytes = typeof m.downloadBytes === "number" ? m.downloadBytes : null;
        if (!isTransfer(S.phase) || !wasTransfer) S.progress = null;
        primaryLock = false;
        renderState();
        renderSettings();
        break;
      case "progress":
        S.progress = m;
        renderProgress();
        break;
      case "server": S.server = m; renderServer(); break;
      case "settings": S.settings = m || {}; renderBrand(); renderSettings(); break;
      case "news": S.news = Array.isArray(m.items) ? m.items : null; renderNews(); break;
      case "hero": showHero(m.url); break;
      case "notes": openNotes(m); break;
      case "shortcuts": onShortcuts(m); break;
      case "uninstall": onUninstall(m); break;
    }
  }

  function isTransfer(phase) { return phase === "updating"; }

  // ---------- formatting ----------
  var MB = 1024 * 1024, GB = MB * 1024;
  function unitFor(n) { return n >= GB ? [GB, "GB", 2] : [MB, "MB", 1]; }
  function fmtBytes(n) { var u = unitFor(n); return (n / u[0]).toFixed(u[2]) + " " + u[1]; }
  function fmtPair(done, total) {
    var u = unitFor(total);
    return "<b>" + (done / u[0]).toFixed(u[2]) + "</b> / " + (total / u[0]).toFixed(u[2]) + " " + u[1];
  }
  function fmtSpeed(bps) {
    if (bps >= 100 * MB) return Math.round(bps / MB) + " MB/s";
    if (bps >= MB) return (bps / MB).toFixed(1) + " MB/s";
    return Math.max(1, Math.round(bps / 1024)) + " KB/s";
  }
  function fmtEta(s) {
    s = Math.round(s);
    if (s < 60) return s + " s left";
    if (s < 600) return Math.floor(s / 60) + " min " + (s % 60) + " s left";
    if (s < 3600) return Math.round(s / 60) + " min left";
    return Math.floor(s / 3600) + " h " + Math.round((s % 3600) / 60) + " min left";
  }
  var MONTHS = ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];
  function fmtDate(d, short) {
    if (!d) return "";
    var m = /^(\d{4})-(\d{2})-(\d{2})/.exec(String(d));
    if (!m) return String(d);
    return (+m[3]) + " " + MONTHS[(+m[2]) - 1] + (short && +m[1] === new Date().getFullYear() ? "" : " " + m[1]);
  }
  function esc(s) {
    return String(s == null ? "" : s).replace(/[&<>"']/g, function (c) {
      return { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c];
    });
  }
  function icon(id) { return '<svg class="i"><use href="#' + id + '"/></svg>'; }
  function safeUrl(u) { return /^https?:\/\//i.test(String(u || "")) ? String(u) : null; }
  function serverName() { return (S.settings && S.settings.serverName) || "RO3Emu"; }
  // a news item's chip colour: its "color", else a guess from the tag
  function chipColor(n) {
    if (COLORS[n.color]) return COLORS[n.color];
    var t = String(n.tag || "").toLowerCase();
    if (/patch|update|release/.test(t)) return COLORS.blue;
    if (/event|festival/.test(t)) return COLORS.orange;
    if (/maint|down|outage/.test(t)) return COLORS.red;
    if (/welcome|account|guide/.test(t)) return COLORS.green;
    return COLORS.blue;
  }

  // ---------- phase ----------
  function renderState() {
    var p = S.phase;
    if (p !== "install" && p !== "update" && p !== "checking") pausedByUser = false;
    document.body.setAttribute("data-phase", p);
    renderVersion();

    var btn = $("primary"), useEl = btn.querySelector("use");
    var cls = "", text, ico = "i-download", disabled = false, busy = false;
    switch (p) {
      case "install": text = pausedByUser ? "Resume" : "Install"; break;
      case "update": text = pausedByUser ? "Resume" : "Update"; break;
      case "repair": text = "Repair"; ico = "i-wrench"; break;
      case "updating": text = "Pause"; ico = "i-pause"; cls = "pause"; break;
      case "ready": text = "Play"; ico = "i-play"; cls = "play"; break;
      case "launching": text = "Starting"; disabled = true; busy = true; break;
      case "running": text = "Running"; ico = "i-play"; disabled = true; break;
      case "error": case "offline": text = "Retry"; ico = "i-refresh"; break;
      default: text = "Checking"; disabled = true; busy = true;
    }
    btn.className = "primary" + (cls ? " " + cls : "") + (busy ? " busy" : "");
    btn.disabled = disabled;
    $("primary-label").textContent = text;
    useEl.setAttribute("href", "#" + ico);
    btn.setAttribute("aria-label", text);

    var transfer = isTransfer(p);
    $("status").hidden = transfer;
    $("progress").hidden = !transfer;
    if (transfer) renderProgress(); else renderStatus();
  }

  function renderStatus() {
    var p = S.phase, name = serverName(), title, sub, ico = "i-star";
    var rv = S.remote, iv = S.installed, dir = S.settings.dir;
    switch (p) {
      case "install":
        title = pausedByUser ? "Installation paused" : "Ready to install";
        sub = S.message || ("The game " + (rv ? rv + " " : "") + (S.downloadBytes ? "(" + fmtBytes(S.downloadBytes) + ") " : "") +
          "will be installed to " + (dir || "the game folder") + ".");
        ico = "i-download"; break;
      case "update":
        title = pausedByUser ? "Update paused" : "Update available";
        sub = S.message || ("Version " + (rv || "") + " is ready. Only the parts that changed are downloaded.");
        ico = "i-download"; break;
      case "repair":
        title = "Repair needed";
        sub = S.message || "Some game files differ from the release."; ico = "i-wrench"; break;
      case "ready":
        title = "Ready to play";
        sub = S.message || ("Version " + (iv || rv || "") + " is installed and up to date."); ico = "i-check"; break;
      case "launching":
        title = "Starting the game";
        sub = S.message || "The game window opens in a moment…"; break;
      case "running":
        title = "The game is running";
        sub = S.message || "Have a good adventure! The launcher waits in the tray."; ico = "i-play"; break;
      case "error":
        title = "Something went wrong";
        sub = S.message || "The update could not finish. Try again."; ico = "i-alert"; break;
      case "offline":
        title = "Can't reach the update server";
        sub = S.message || "Check your connection and retry."; ico = "i-cloud-off"; break;
      default:
        title = "Checking for updates";
        sub = S.message || "Comparing your files with the latest " + name + " release…";
    }
    $("status-change").hidden = p !== "install";
    $("status-title").textContent = title;
    $("status-sub").textContent = sub;
    $("status-sub").title = sub;
    $("status-badge").querySelector("use").setAttribute("href", "#" + ico);
  }

  function renderVersion() {
    var iv = S.installed, rv = S.remote, el = $("ver-line");
    if (!iv && !rv) el.innerHTML = '<span class="na">' + (S.phase === "checking" ? "Checking…" : "Unknown") + "</span>";
    else if (!iv) el.innerHTML = '<span class="na">Not installed</span>' + icon("i-arrow") + "<b>" + esc(rv) + "</b>";
    else if (rv && rv !== iv) el.innerHTML = '<b class="old">' + esc(iv) + "</b>" + icon("i-arrow") + "<b>" + esc(rv) + "</b>";
    else el.innerHTML = "<b>" + esc(iv) + "</b>";
    el.title = iv && rv && iv !== rv ? "Installed " + iv + ", available " + rv : "";
  }

  function renderProgress() {
    if (!isTransfer(S.phase)) return;
    var pr = S.progress, bar = $("p-bar"), fill = $("p-fill");
    if (!pr) {
      $("p-stage").textContent = "Preparing";
      $("p-file").textContent = "";
      $("p-pct").textContent = "";
      bar.classList.add("indet");
      $("p-bytes").textContent = "Getting the update ready…";
      $("p-rate").textContent = "";
      return;
    }
    $("p-stage").textContent = STAGES[pr.stage] || pr.stage || "Working";
    $("p-file").textContent = pr.file || "";
    $("p-file").title = pr.file || "";
    var total = +pr.total || 0, done = Math.min(+pr.done || 0, total || Infinity);
    if (total > 0) {
      var pct = Math.max(0, Math.min(100, done / total * 100));
      bar.classList.remove("indet");
      fill.style.width = pct.toFixed(2) + "%";
      $("p-pct").textContent = Math.floor(pct) + "%";
      $("p-bytes").innerHTML = fmtPair(done, total);
    } else {
      bar.classList.add("indet");
      $("p-pct").textContent = "";
      $("p-bytes").textContent = pr.stage === "committing" ? "Moving the new files into place…" : "";
    }
    var rate = [];
    if (pr.rate > 0) rate.push(fmtSpeed(pr.rate));
    if (pr.eta > 0 && total > 0) rate.push(fmtEta(pr.eta));
    $("p-rate").textContent = rate.join("  ·  ");
  }

  // ---------- news, server, hero, brand ----------
  var featuredUrl = null;

  function renderNews() {
    var items = S.news && S.news.length ? S.news : DEFAULT_NEWS.map(function (n) { return n.title ? n : { tag: n.tag, color: n.color, text: n.text, title: "Welcome to " + serverName() }; });
    var f = items[0];
    $("feat-tag").textContent = f.tag || "News";
    $("feat-tag").style.setProperty("--c", chipColor(f));
    $("feat-date").textContent = fmtDate(f.date);
    $("feat-title").textContent = f.title || "";
    var text = f.excerpt || f.text || "";
    $("feat-excerpt").textContent = text;
    $("feat-excerpt").hidden = !text;
    featuredUrl = safeUrl(f.url);
    $("feat-more").hidden = !featuredUrl;

    var rest = items.slice(1, 4), ul = $("news-list");
    ul.innerHTML = "";
    rest.forEach(function (it) {
      var li = document.createElement("li");
      var b = document.createElement("button");
      b.type = "button";
      b.className = "item";
      b.innerHTML = '<span class="chip" style="--c:' + chipColor(it) + '">' + esc(it.tag || "News") + "</span>" +
        '<span class="t">' + esc(it.title) + "</span><time>" + esc(fmtDate(it.date, true)) + "</time>" + icon("i-arrow");
      b.title = [it.title, it.excerpt || it.text].filter(Boolean).join("\n");
      var u = safeUrl(it.url);
      if (u) b.addEventListener("click", function () { send({ cmd: "openUrl", url: u }); });
      else b.disabled = true;
      li.appendChild(b);
      ul.appendChild(li);
    });
    $("news-list-wrap").hidden = rest.length === 0;
  }

  function renderServer() {
    var el = $("server"), t = $("server-text"), s = S.server;
    if (!s) { el.setAttribute("data-state", "unknown"); t.textContent = "Checking server…"; return; }
    if (s.online) {
      el.setAttribute("data-state", "online");
      var n = s.players;
      t.innerHTML = "<b>Online</b>" + (typeof n === "number" ? " · " + n.toLocaleString("en-US") + (n === 1 ? " adventurer" : " adventurers") : "");
    } else {
      el.setAttribute("data-state", "offline");
      t.innerHTML = "<b>Server offline</b>";
    }
  }

  function renderBrand() {
    var name = serverName();
    document.title = name;
    $("brand").textContent = name;
    $("hero-name").textContent = name;
    if (!S.news) renderNews();   // the built-in welcome names the server
    var links = Array.isArray(S.settings.links) ? S.settings.links : [], nav = $("links");
    nav.innerHTML = "";
    links.forEach(function (l) {
      var u = safeUrl(l && l.url);
      if (!u || !l.label) return;
      var b = document.createElement("button");
      b.type = "button";
      b.className = "link";
      b.innerHTML = icon(linkIcon(l)) + esc(l.label);
      b.title = u;
      b.addEventListener("click", function () { send({ cmd: "openUrl", url: u }); });
      nav.appendChild(b);
    });
  }
  function linkIcon(l) {
    var known = { globe: 1, chat: 1, book: 1, trophy: 1, flag: 1, link: 1 };
    if (known[l.icon]) return "i-" + l.icon;
    var t = (String(l.label) + " " + l.url).toLowerCase();
    if (/discord|forum|chat|community/.test(t)) return "i-chat";
    if (/wiki|guide|database|db|help/.test(t)) return "i-book";
    if (/rank|ladder/.test(t)) return "i-trophy";
    if (/web|site|home/.test(t)) return "i-globe";
    return "i-link";
  }

  // The hero comes from the host (launcher.json "hero" or <repository>/hero.jpg); without one the drawn map stays.
  function showHero(url) {
    var img = $("hero-img");
    if (!url) { img.hidden = true; $("hero-default").hidden = false; return; }
    img.onload = function () { img.hidden = false; $("hero-default").hidden = true; };
    img.onerror = function () { img.hidden = true; $("hero-default").hidden = false; };
    img.src = url;
  }

  // ---------- settings ----------
  function renderSettings() {
    var st = S.settings || {};
    setPath($("set-dir"), st.dir || "");
    $("set-launcher").textContent = "Launcher " + (st.launcher || "—");
    var busy = isTransfer(S.phase) || S.phase === "launching" || S.phase === "running";
    $("set-dir-change").disabled = busy || S.phase === "checking";
    $("set-repair").disabled = busy || !S.installed || S.phase === "checking" || S.phase === "offline";
    $("set-uninstall-field").hidden = !st.canUninstall;
    $("set-uninstall").disabled = busy || S.phase === "checking";
  }

  // Long paths show their end (the folder name); the whole path is in the tooltip.
  function setPath(input, value) {
    var mark = String.fromCharCode(0x200E);   // keeps the path left-to-right inside the right-to-left field
    input.value = value ? mark + value + mark : "";
    input.title = value;
  }

  function openSettings() { $("settings").hidden = false; renderSettings(); setTimeout(function () { $("settings").querySelector(".sheet").focus(); }, 30); }
  function closeSettings() { $("settings").hidden = true; }

  var toastTimer = null;
  function toast(text) {
    var el = $("toast");
    el.textContent = text;
    el.classList.add("show");
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.classList.remove("show"); }, 2400);
  }

  // ---------- shortcuts (first run and Settings) ----------
  function openShortcuts() {
    closeSettings();
    $("shortcuts").hidden = false;
    setTimeout(function () { $("shortcuts-yes").focus(); }, 30);
  }
  function onShortcuts(m) {
    switch (m.status) {
      case "ask": openShortcuts(); break;
      case "done": $("shortcuts").hidden = true; if (m.created) toast("Shortcuts added."); break;
      case "error":
        $("shortcuts").hidden = true;
        toast("Couldn't add the " + (m.failed || []).join(" and ") + " shortcut.");
        break;
    }
  }
  function answerShortcuts(yes) {
    send({ cmd: "shortcuts", desktop: yes && $("shortcuts-desktop").checked, startMenu: yes && $("shortcuts-start").checked });
    $("shortcuts").hidden = true;
  }

  // ---------- what's new after an update ----------
  function openNotes(m) {
    var items = Array.isArray(m.items) ? m.items : [];
    if (!items.length) return;
    $("notes-version").textContent = serverName() + " " + (m.version || "") + " is installed. What changed:";
    var ul = $("notes-list");
    ul.innerHTML = "";
    items.forEach(function (t) { var li = document.createElement("li"); li.textContent = String(t); ul.appendChild(li); });
    $("notes").hidden = false;
    setTimeout(function () { $("notes-ok").focus(); }, 30);
  }
  function closeNotes() { $("notes").hidden = true; send({ cmd: "notesSeen" }); }

  // ---------- uninstall ----------
  function openUninstall() {
    closeSettings();
    $("uninstall-path").textContent = S.settings.dir || "";
    $("uninstall-error").hidden = true;
    $("uninstall-go").disabled = false;
    $("uninstall").hidden = false;
    setTimeout(function () { $("uninstall-cancel").focus(); }, 30);
  }
  function closeUninstall() { $("uninstall").hidden = true; }
  function onUninstall(m) {
    if (m.status === "done") { closeUninstall(); toast("The game folder was removed."); return; }
    $("uninstall").hidden = false;
    $("uninstall-go").disabled = false;
    $("uninstall-error").textContent = m.message || "Uninstalling failed.";
    $("uninstall-error").hidden = false;
  }

  // ---------- frames: a gold star at each corner ----------
  var CORNER = '<svg viewBox="0 0 44 44" fill="none"><path d="M18 11H44M11 18V44" stroke="currentColor" stroke-width="1.2"/>' +
    '<path d="M22 11h6M11 22v6" stroke="currentColor" stroke-width="3" stroke-linecap="round"/>' +
    '<path d="M11 1.5l2.2 7.3 7.3 2.2-7.3 2.2L11 20.5l-2.2-7.3L1.5 11l7.3-2.2z" fill="currentColor" stroke="#fff8e4" stroke-width=".8" stroke-linejoin="round"/>' +
    '<circle cx="11" cy="11" r="1.5" fill="#fff8e4"/></svg>';
  function decorateFrames() {
    Array.prototype.forEach.call(document.querySelectorAll(".frame"), function (f) {
      ["tl", "tr", "bl", "br"].forEach(function (c) {
        var i = document.createElement("i");
        i.className = "orn " + c;
        i.setAttribute("aria-hidden", "true");
        i.innerHTML = CORNER;
        f.appendChild(i);
      });
    });
  }

  // ---------- events ----------
  function bind() {
    // drag the window from the title strip (never from its buttons); double-click does nothing
    document.addEventListener("mousedown", function (e) {
      if (e.button !== 0) return;
      var t = e.target;
      if (!t.closest || !t.closest("[data-drag]") || t.closest("button,a,input,textarea")) return;
      e.preventDefault();
      send({ cmd: "window", action: "drag" });
    });
    document.addEventListener("dblclick", function (e) { if (e.target.closest && e.target.closest("[data-drag]")) e.preventDefault(); });
    document.addEventListener("dragstart", function (e) { e.preventDefault(); });

    $("btn-min").addEventListener("click", function () { send({ cmd: "window", action: "minimize" }); });
    $("btn-close").addEventListener("click", function () { send({ cmd: "window", action: "close" }); });
    $("btn-settings").addEventListener("click", openSettings);
    $("settings-x").addEventListener("click", closeSettings);
    $("settings-done").addEventListener("click", closeSettings);
    $("settings").addEventListener("mousedown", function (e) { if (e.target === this) closeSettings(); });
    document.addEventListener("keydown", function (e) {
      if (e.key !== "Escape") return;
      if (!$("notes").hidden) closeNotes();
      else if (!$("uninstall").hidden) closeUninstall();
      else if (!$("shortcuts").hidden) answerShortcuts(false);
      else if (!$("settings").hidden) closeSettings();
    });

    $("set-dir-change").addEventListener("click", function () { send({ cmd: "changeDir" }); });
    $("status-change").addEventListener("click", function () { send({ cmd: "changeDir" }); });
    $("set-open").addEventListener("click", function () { send({ cmd: "openDir" }); });
    $("set-repair").addEventListener("click", function () { send({ cmd: "repair" }); closeSettings(); });
    $("set-shortcuts").addEventListener("click", openShortcuts);
    $("set-uninstall").addEventListener("click", openUninstall);

    $("shortcuts-yes").addEventListener("click", function () { answerShortcuts(true); });
    $("shortcuts-no").addEventListener("click", function () { answerShortcuts(false); });
    $("shortcuts-x").addEventListener("click", function () { answerShortcuts(false); });
    $("notes-ok").addEventListener("click", closeNotes);
    $("notes-x").addEventListener("click", closeNotes);
    $("uninstall-x").addEventListener("click", closeUninstall);
    $("uninstall-cancel").addEventListener("click", closeUninstall);
    $("uninstall-go").addEventListener("click", function () { this.disabled = true; send({ cmd: "uninstall" }); });
    $("feat-more").addEventListener("click", function () { if (featuredUrl) send({ cmd: "openUrl", url: featuredUrl }); });

    $("primary").addEventListener("click", function () {
      if (primaryLock) return;
      if (isTransfer(S.phase)) { pausedByUser = true; send({ cmd: "pause" }); return; }
      primaryLock = true;   // released by the next state message
      setTimeout(function () { primaryLock = false; }, 1500);
      send({ cmd: "primary" });
    });
  }

  // ---------- mock host (plain browser only) ----------
  function createMockHost(mode) {
    var Q = new URLSearchParams(location.search);
    var FAST = +Q.get("fast") || 1;
    var timers = [];
    function later(ms, fn) { var t = setTimeout(fn, ms); timers.push(t); return t; }
    function clearAll() { timers.forEach(clearTimeout); timers = []; }
    function emit(o) { setTimeout(function () { handle(JSON.parse(JSON.stringify(o))); }, 0); }

    var scen = {
      install:     { installed: null, phase: "install" },
      update:      { installed: "2026.10.08.2", phase: "update" },
      ready:       { installed: "2026.10.12.1", phase: "ready" },
      running:     { installed: "2026.10.12.1", phase: "ready", autoPlay: true },
      repair:      { installed: "2026.10.12.1", phase: "repair", message: "3 game files differ from the release." },
      error:       { installed: "2026.10.08.2", phase: "error", message: "ro3_Data/Managed/Assembly-CSharp.dll failed verification. Nothing was changed, so retrying is safe." },
      offline:     { installed: null, remote: null, phase: "offline" },
      downloading: { installed: "2026.10.08.2", phase: "update", freezeAt: 0.434 },
      checking:    { installed: "2026.10.08.2", remote: null, phase: "checking", stay: true }
    }[mode] || { installed: "2026.10.08.2", phase: "update" };
    var m = { installed: scen.installed, remote: scen.remote === null ? null : "2026.10.12.1", dir: "C:\\Games\\RO3Emu\\Client", owned: true };

    function settings() {
      return { type: "settings", dir: m.dir, launcher: "1.1.0", serverName: "RO3Emu", canUninstall: !!m.installed && m.owned,
        links: Q.get("links") === "0" ? [] : [
          { label: "Website", url: "https://example.com/" },
          { label: "Discord", url: "https://discord.gg/example" },
          { label: "Wiki", url: "https://example.com/wiki" }] };
    }
    function state(phase, message) {
      var o = { type: "state", phase: phase, installed: m.installed, remote: m.remote };
      if (message) o.message = message;
      if (phase === "install") o.downloadBytes = 12.84 * GB;
      m.phase = phase;
      emit(o);
    }
    var NEWS = { type: "news", items: [
      { tag: "Patch", color: "blue", date: "2026-10-12", url: "https://example.com/news/patch",
        title: "Guild Expeditions and the Payon Forest rift",
        text: "Guilds can now take on weekly expeditions together. The rift in Payon Forest opens every evening, with Eddga waiting at its heart." },
      { tag: "Event", color: "orange", date: "2026-10-10", url: "https://example.com/news/event", title: "Harvest Festival in Prontera, 10 - 24 Oct" },
      { tag: "Maintenance", color: "red", date: "2026-10-08", title: "Server maintenance, Tuesday 06:00 CEST" },
      { tag: "News", color: "green", date: "2026-10-05", url: "https://example.com/news/alpha", title: "Closed alpha: thank you, testers!" }
    ] };

    function boot() {
      emit(settings());
      state("checking");
      if (Q.get("hero")) emit({ type: "hero", url: Q.get("hero") });
      if (scen.phase !== "offline") emit(NEWS);
      emit(scen.phase === "offline" ? { type: "server", online: false } : { type: "server", online: true, players: 128 });
      if (Q.get("first")) later(300, function () { emit({ type: "shortcuts", status: "ask" }); });
      if (Q.get("notes")) later(300, function () {
        emit({ type: "notes", version: m.remote, items: ["Guild Expeditions: weekly goals for the whole guild.",
          "The Payon Forest rift opens every evening at 20:00.", "Fixed the party list losing members after a map change.",
          "The launcher waits in the tray while you play."] });
      });
      if (scen.stay) return;
      later(500, function () {
        state(scen.phase, scen.message);
        if (scen.freezeAt != null) later(100, function () { startUpdate(scen.freezeAt); });
        if (scen.autoPlay) later(150, play);
      });
    }

    var FILES = ["ro3_Data/StreamingAssets/bundles/scene_payon.bundle", "ro3_Data/Managed/Assembly-CSharp.dll",
      "ro3_Data/StreamingAssets/bundles/ui_common.bundle", "GameAssembly.dll"];
    function plan(repair) {
      if (repair) return [{ stage: "scanning", total: 12.9 * GB, rate: 900 * MB }, { stage: "downloading", total: 3.1 * MB, rate: 9.5 * MB, speed: true },
        { stage: "verifying", total: 41 * MB, rate: 380 * MB }, { stage: "committing", total: 0, ms: 700 }];
      return [{ stage: "scanning", total: 12.9 * GB, rate: 1.4 * GB }, { stage: "downloading", total: 412 * MB, rate: 24 * MB, speed: true },
        { stage: "assembling", total: 1.1 * GB, rate: 260 * MB }, { stage: "verifying", total: 1.1 * GB, rate: 420 * MB },
        { stage: "committing", total: 0, ms: 800 }];
    }
    function startUpdate(freezeAt, repair) {
      clearAll();
      var stages = plan(repair), si = 0, done = 0, fileIx = 0;
      state("updating");
      if (freezeAt != null) {
        var s = stages[1], d = Math.round(s.total * freezeAt), sp = s.rate * 0.98;
        emit({ type: "progress", stage: "downloading", done: d, total: Math.round(s.total), rate: Math.round(sp), eta: Math.round((s.total - d) / sp), file: FILES[0] });
        return;
      }
      function tick() {
        var s = stages[si];
        if (!s.total) { emit({ type: "progress", stage: s.stage, done: 0, total: 0 }); later(s.ms / FAST, next); return; }
        var rate = s.rate * (s.speed ? 1 + 0.15 * Math.sin(Date.now() / 900) : 1) * FAST;
        done = Math.min(s.total, done + rate * 0.2);
        if (s.speed && Math.random() < 0.08) fileIx = (fileIx + 1) % FILES.length;
        emit({ type: "progress", stage: s.stage, done: Math.round(done), total: Math.round(s.total),
          rate: s.speed ? Math.round(rate) : 0, eta: s.speed ? Math.max(1, Math.round((s.total - done) / rate)) : 0,
          file: s.stage === "downloading" ? FILES[fileIx] : undefined });
        if (done >= s.total) later(250, next); else later(200, tick);
      }
      function next() {
        si++; done = 0;
        if (si >= stages.length) { m.installed = m.remote; state("ready"); return; }
        tick();
      }
      tick();
    }
    function play() {
      clearAll();
      state("launching");
      later(1200, function () { state("running"); later(12000, function () { state("ready"); }); });
    }

    return {
      post: function (c) {
        if (!c || !c.cmd) return;
        if (window.console) console.log("[mock host] <-", JSON.stringify(c));
        switch (c.cmd) {
          case "ready": boot(); break;
          case "primary":
            if (m.phase === "install" || m.phase === "update" || m.phase === "error") startUpdate(null);
            else if (m.phase === "repair") startUpdate(null, true);
            else if (m.phase === "offline") { m.remote = "2026.10.12.1"; state("checking"); later(800, function () { state("install"); }); }
            else if (m.phase === "ready") play();
            break;
          case "pause": clearAll(); state(m.installed ? "update" : "install", "Paused. Downloaded parts are kept; continue any time."); break;
          case "repair": if (m.installed) startUpdate(null, true); break;
          case "openUrl": toast("Opens in your browser: " + c.url); break;
          case "openDir": toast("Opens " + m.dir); break;
          case "changeDir": m.dir = "E:\\Games\\RO3Emu"; emit(settings()); break;
          case "window": if (c.action !== "drag") toast("Window: " + c.action); break;
          case "shortcuts": emit({ type: "shortcuts", status: "done", created: c.desktop || c.startMenu, failed: [] }); break;
          case "uninstall":
            later(600, function () { m.installed = null; emit({ type: "uninstall", status: "done" }); emit(settings()); state("install"); });
            break;
        }
      }
    };
  }

  // ---------- start ----------
  function start() {
    var params = new URLSearchParams(location.search);
    var wv = window.chrome && window.chrome.webview;
    if (wv) {
      host = { post: function (o) { wv.postMessage(o); } };
      wv.addEventListener("message", function (e) { handle(e.data); });
      document.addEventListener("contextmenu", function (e) { if (!e.target.closest("input,textarea")) e.preventDefault(); });
    } else {
      host = createMockHost(params.get("mock") || "update");
    }
    decorateFrames();
    bind();
    renderBrand();
    renderNews();
    renderState();
    renderServer();
    renderSettings();
    send({ cmd: "ready" });
    if (params.get("panel") === "settings") openSettings();
  }

  if (document.readyState === "loading") document.addEventListener("DOMContentLoaded", start);
  else start();
})();
