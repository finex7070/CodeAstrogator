/* ===========================================================================
   Code Astrogator — Chat UI (Teil B)
   Vanilla JS. Talks to the C# host exclusively via the §3 message contract,
   or to a built-in mock adapter when no host is present.
   =========================================================================== */
(function () {
  "use strict";

  // -------------------------------------------------------------------------
  // State (single in-memory object — §10)
  // -------------------------------------------------------------------------
  const state = {
    sessionId: null,
    title: "Untitled",
    model: "claude-opus-4-8",
    effort: "high", // host default (Tools → Options → Default effort)
    planMode: false,
    ultracode: false,
    verbosity: "normal", // compact | normal | detailed
    activeThinking: null,
    workspaceFiles: null, // [{path, isDir}] from files.list (lazy)
    filesRequested: false,
    permissionMode: "ask",
    autoAcceptCommands: false, // in acceptEdits mode, also auto-approve Bash/PowerShell/MCP
    reviewEditsInEditor: false, // review file edits inline in the code editor (per-hunk) instead of the chat diff card
    cwd: "",
    tokens: 0,
    contextTokens: 0,        // context size after the last turn (input incl. cache + output)
    limits: { sessionPct: 0, weeklyPct: 0 },
    plan: "",
    themeMode: "auto",
    resolvedTheme: "dark",
    accent: "",              // custom brand color (CSS hex); "" = per-theme default

    loggedIn: true,
    authMode: "oauth",
    status: "ready",
    remoteActive: false,     // remote-control mode locks composer + transcript
    remoteUrl: null,
    activeFile: { name: null, path: null, optionEnabled: true, enabled: true, lines: null }, // active editor tab reference
    messages: [],            // rendered message records
    attachments: [],         // {id, name}
    activeAssistant: null,   // {id, textNode, accText, el}
    pendingPermissions: new Set(), // requestIds of open permission/question cards (parallel tool use)
  };

  const MODELS = [
    { id: "claude-opus-4-8", label: "Opus 4.8" },
    { id: "claude-opus-4-7", label: "Opus 4.7" },
    { id: "claude-sonnet-4-6", label: "Sonnet 4.6" },
    { id: "claude-haiku-4-5", label: "Haiku 4.5" },
  ];
  const PERMISSION_LABELS = {
    ask: "Ask",
    acceptEdits: "Auto-accept",
    plan: "Plan",
    bypass: "Bypass",
  };
  // Descriptions for known commands — the CLI reports bare names only.
  const SLASH_DESCRIPTIONS = {
    "/clear": "Clear conversation",
    "/compact": "Compact history",
    "/context": "Show context usage",
    "/model": "Choose model",
    "/init": "Initialize project",
    "/usage": "Show plan usage",
    "/review": "Review a pull request",
    "/security-review": "Security review of changes",
    "/help": "Show help",
  };
  // Static fallback until the CLI reports its real list via `slash.commands`
  // (from the system/init event of the first turn). Mutated in place.
  const SLASH_COMMANDS = [
    { command: "/clear", desc: SLASH_DESCRIPTIONS["/clear"] },
    { command: "/compact", desc: SLASH_DESCRIPTIONS["/compact"] },
    { command: "/model", desc: SLASH_DESCRIPTIONS["/model"] },
    { command: "/init", desc: SLASH_DESCRIPTIONS["/init"] },
    { command: "/help", desc: SLASH_DESCRIPTIONS["/help"] },
  ];

  // -------------------------------------------------------------------------
  // DOM refs
  // -------------------------------------------------------------------------
  const $ = (id) => document.getElementById(id);
  const root = $("root");
  const titleEl = $("title");
  const transcript = $("transcript");
  const transcriptInner = $("transcript-inner");
  const jumpPill = $("jump-pill");
  const composer = $("composer");
  const input = $("input");
  const attachmentsEl = $("attachments");
  const sendBtn = $("btn-send");
  const activeFileChip = $("active-file");
  const activeFileName = $("active-file-name");
  const activeFileLines = $("active-file-lines");
  const modelModeBtn = $("btn-modelmode");
  const modelModeLabel = $("modelmode-label");
  const statusbar = $("statusbar");
  const statusDot = $("status-dot");
  const statusText = $("status-text");
  const statusSession = $("status-session");
  const statusWeekly = $("status-weekly");
  const meterSession = $("meter-session");
  const meterWeekly = $("meter-weekly");
  const labelSession = $("label-session");
  const labelWeekly = $("label-weekly");
  const planBadge = $("plan-badge");
  const planLabel = $("plan-label");
  const overlayLayer = $("overlay-layer");
  const btnRemote = $("btn-remote");
  const remotePanel = $("remote-panel");
  const remoteStatus = $("remote-status");
  const remoteQr = $("remote-qr");
  const remoteUrlEl = $("remote-url");
  const btnRemoteCopy = $("btn-remote-copy");
  const btnRemoteEnd = $("btn-remote-end");

  // Notice / announcement banner — content + on/off come from a remote JSON file fetched from
  // the project's GitHub (the "source of truth"), so announcements can be pushed without a VSIX
  // release. Because that means a network call, it is gated behind an explicit user opt-in: on
  // first run a consent popup asks permission to periodically fetch it; the choice is persisted
  // host-side (AstrogatorOptions.NoticeFetch*) and editable in the settings window. The notice file
  // has exactly five fields: { enabled, from, to, title, content } — `from`/`to` are optional
  // ISO date-times bounding the display window, `content` is Markdown. X dismisses for the
  // session only.
  //
  // Fetch policy (when enabled): on each window open fetch the notice and cache it in
  // localStorage, but at most once per NOTICE_MIN_INTERVAL (≥3 h between fetch ATTEMPTS — both
  // success and failure reset the timer). A failed fetch (offline/404/bad JSON) falls back to the
  // last cached notice; if there is none, nothing is shown — and it retries on the next window
  // open (still throttled). When disabled, nothing is fetched or shown at all.
  const noticeBanner = $("notice-banner");
  const noticeText = $("notice-text");
  const noticeClose = $("notice-close");
  if (noticeClose && noticeBanner) {
    noticeClose.addEventListener("click", () => { noticeBanner.hidden = true; });
  }
  const updateBanner = $("update-banner");
  const updateText = $("update-text");
  const updateClose = $("update-close");
  if (updateClose && updateBanner) {
    updateClose.addEventListener("click", () => { updateBanner.hidden = true; });
  }

  // GitHub URLs, both CORS-fetchable from the WebUI. The repo ("owner/repo") + notice branch come
  // from the build-time config file (WebUI/config.js — edit before building); the fallbacks here
  // keep things working if config.js is missing (e.g. isolated browser testing).
  // notice.json (raw.githubusercontent.com sends Access-Control-Allow-Origin: *) drives the
  // announcement banner — edit + push to reach all installed extensions without a release. The
  // update banner reads the latest GitHub *release* (api.github.com is CORS-enabled and the WebView
  // sends its own User-Agent): /releases/latest = newest non-draft/non-prerelease (tag_name, html_url).
  const CPFC_CFG = window.CPFC_CONFIG || {};
  const GITHUB_REPO = CPFC_CFG.githubRepo || "finex7070/CodeAstrogator";
  const NOTICE_BRANCH = CPFC_CFG.noticeBranch || "master";
  const NOTICE_SOURCE_URL =
    "https://raw.githubusercontent.com/" + GITHUB_REPO + "/" + NOTICE_BRANCH + "/notice.json";
  const UPDATE_SOURCE_URL =
    "https://api.github.com/repos/" + GITHUB_REPO + "/releases/latest";

  let bannersEvaluated = false; // run the consent/fetch flow at most once per window load
  let appVersion = "";          // installed extension version (from session.init)

  // localStorage cache (persists across window opens via the fixed WebView2 user-data folder).
  // Fetch policy for BOTH banners: on each window open fetch from GitHub, but at most once per
  // MIN_INTERVAL (≥3 h between attempts — success AND failure reset the timer). A failed fetch
  // falls back to the last cached value; if none, nothing shows — retried (throttled) next open.
  const MIN_INTERVAL = 3 * 60 * 60 * 1000; // ≥3 h between fetch attempts
  const NOTICE_CACHE_KEY = "cpfc.notice.cache", NOTICE_LASTFETCH_KEY = "cpfc.notice.lastFetch";
  const UPDATE_CACHE_KEY = "cpfc.update.cache", UPDATE_LASTFETCH_KEY = "cpfc.update.lastFetch";

  function lsReadJson(key) {
    try { const r = localStorage.getItem(key); return r ? JSON.parse(r) : null; } catch (_e) { return null; }
  }
  function lsWriteJson(key, v) {
    try { localStorage.setItem(key, JSON.stringify(v)); } catch (_e) { /* quota/disabled */ }
  }
  function lsReadTs(key) {
    try { const v = parseInt(localStorage.getItem(key) || "0", 10); return isNaN(v) ? 0 : v; } catch (_e) { return 0; }
  }
  function lsWriteTs(key, v) {
    try { localStorage.setItem(key, String(v)); } catch (_e) { /* quota/disabled */ }
  }

  async function fetchJsonQuietly(url) {
    try {
      const resp = await fetch(url, { cache: "no-store" });
      if (!resp || !resp.ok) return null;
      return await resp.json();
    } catch (_e) {
      return null; // offline / blocked / bad JSON — treat as "nothing"
    }
  }

  // Throttled+cached fetch shared by both banners: fetch when due, else use cache; render(fetched).
  async function fetchThrottledCached(url, lastKey, cacheKey, render) {
    const now = Date.now();
    if (now - lsReadTs(lastKey) >= MIN_INTERVAL) {
      lsWriteTs(lastKey, now); // record the attempt up front so failures are throttled too
      const fetched = await fetchJsonQuietly(url);
      if (fetched) { lsWriteJson(cacheKey, fetched); render(fetched); return; }
      // fetch failed → fall through to the cached copy (if any)
    }
    const cached = lsReadJson(cacheKey);
    if (cached) render(cached);
  }

  // ── announcement banner ──────────────────────────────────────────────────
  /** True when `now` lies within the optional [from, to] window (ISO date-times; blank = open). */
  function noticeWithinWindow(cfg) {
    const now = Date.now();
    if (cfg.from) { const t = Date.parse(cfg.from); if (!isNaN(t) && now < t) return false; }
    if (cfg.to) { const t = Date.parse(cfg.to); if (!isNaN(t) && now > t) return false; }
    return true;
  }
  function renderNotice(cfg) {
    if (!noticeBanner || !noticeText || !cfg || cfg.enabled !== true) return;
    if (!noticeWithinWindow(cfg)) return; // outside the scheduled from/to window
    const title = typeof cfg.title === "string" ? cfg.title.trim() : "";
    const content = typeof cfg.content === "string" ? cfg.content.trim() : "";
    if (!title && !content) return; // nothing to show
    noticeText.innerHTML = "";
    if (title) {
      const strong = document.createElement("strong");
      strong.textContent = title; // title is plain text
      noticeText.appendChild(strong);
      noticeText.appendChild(document.createTextNode(" "));
    }
    if (content) {
      // content is Markdown (so links work); reuse the transcript renderer. Links keep
      // target="_blank" via renderInline → opened in the system browser by the host.
      noticeText.appendChild(renderMarkdown(content));
    }
    noticeBanner.hidden = false;
  }
  function loadNotice() {
    return fetchThrottledCached(NOTICE_SOURCE_URL, NOTICE_LASTFETCH_KEY, NOTICE_CACHE_KEY, renderNotice);
  }

  // ── update banner ────────────────────────────────────────────────────────
  function parseVer(v) {
    return String(v == null ? "" : v).trim().replace(/^v/i, "").split(".").map((n) => parseInt(n, 10) || 0);
  }
  /** True when remote version is strictly newer than the installed one (numeric, dot-separated). */
  function isNewerVersion(remote, installed) {
    const a = parseVer(remote), b = parseVer(installed);
    const len = Math.max(a.length, b.length);
    for (let i = 0; i < len; i++) {
      const x = a[i] || 0, y = b[i] || 0;
      if (x > y) return true;
      if (x < y) return false;
    }
    return false;
  }
  function renderUpdate(info) {
    if (!updateBanner || !updateText || !info) return;
    // GitHub /releases/latest payload: tag_name = the version, html_url = the release page.
    const remote = typeof info.tag_name === "string" ? info.tag_name.trim() : "";
    if (!remote || !appVersion || !isNewerVersion(remote, appVersion)) return;
    const shown = remote.replace(/^v/i, ""); // display without a leading "v"
    updateText.innerHTML = "";
    const strong = document.createElement("strong");
    strong.textContent = "Update available";
    updateText.appendChild(strong);
    updateText.appendChild(document.createTextNode(
      " Version " + shown + " is available (you have " + appVersion + "). "));
    const url = typeof info.html_url === "string" ? info.html_url : "";
    if (url && /^https?:\/\//i.test(url)) {
      const a = document.createElement("a");
      a.href = url; a.target = "_blank"; a.rel = "noopener noreferrer";
      a.textContent = "View release ↗";
      updateText.appendChild(a);
    }
    updateBanner.hidden = false;
  }
  function loadUpdate() {
    return fetchThrottledCached(UPDATE_SOURCE_URL, UPDATE_LASTFETCH_KEY, UPDATE_CACHE_KEY, renderUpdate);
  }

  // ── consent + evaluation ───────────────────────────────────────────────────
  // Decide what to do once we know the persisted opt-in state (from session.init / banner.settings).
  // Shows the one-time consent popup (asks about BOTH announcements and updates), or fetches the
  // banners that are already allowed. Runs at most once per window load.
  function evaluateBanners(s) {
    if (bannersEvaluated) return;
    bannersEvaluated = true;
    appVersion = s.appVersion || appVersion;
    if (!s.noticeDecided || !s.updateDecided) {
      openConsentPopup(s);
      return;
    }
    if (s.noticeEnabled) loadNotice();
    if (s.updateEnabled) loadUpdate();
  }

  // First-run consent popup — asks about announcements AND update notifications in one dialog.
  function openConsentPopup(s) {
    if (!overlayLayer) return;
    const backdrop = el("div", "modal-backdrop");
    const modal = el("div", "modal");
    modal.setAttribute("role", "dialog");
    modal.setAttribute("aria-label", "Notifications");
    modal.appendChild(el("div", "modal-title", "Notifications"));

    const body = el("div", "modal-body");
    body.textContent =
      "Code Astrogator can check the project's GitHub when this window opens. Choose what "
      + "you'd like — this makes a small network request and can be changed anytime in the settings.";
    modal.appendChild(body);

    // Pre-fill: reflect a previously-decided choice, otherwise suggest "on".
    const ann = consentRow("Show announcements from the project",
      s.noticeDecided ? !!s.noticeEnabled : true);
    const upd = consentRow("Notify me about new versions (updates)",
      s.updateDecided ? !!s.updateEnabled : true);
    modal.appendChild(ann.row);
    modal.appendChild(upd.row);

    const actions = el("div", "modal-actions");
    const save = el("button", "modal-btn primary", "Save");
    save.addEventListener("click", () => {
      const noticeEnabled = ann.input.checked, updateEnabled = upd.input.checked;
      post("consent.set", { noticeEnabled: noticeEnabled, updateEnabled: updateEnabled });
      if (backdrop.parentNode) backdrop.parentNode.removeChild(backdrop);
      if (noticeEnabled) loadNotice();
      if (updateEnabled) loadUpdate();
    });
    actions.appendChild(save);
    modal.appendChild(actions);

    backdrop.appendChild(modal);
    overlayLayer.appendChild(backdrop);
    save.focus();
  }

  // A labelled checkbox row for the consent popup; returns { row, input }.
  function consentRow(label, checked) {
    const row = el("label", "consent-row");
    const input = document.createElement("input");
    input.type = "checkbox";
    input.checked = !!checked;
    row.appendChild(input);
    row.appendChild(el("span", "consent-label", label));
    return { row: row, input: input };
  }

  // Live update pushed by the host when a banner setting changes in the settings window.
  function applyBannerSettings(m) {
    bannersEvaluated = true; // explicit settings exist now → never auto-show the consent popup
    if (m.appVersion) appVersion = m.appVersion;
    if (m.noticeEnabled) loadNotice();
    else if (noticeBanner) noticeBanner.hidden = true;
    if (m.updateEnabled) loadUpdate();
    else if (updateBanner) updateBanner.hidden = true;
  }

  // -------------------------------------------------------------------------
  // Host bridge (§3) — postMessage out, addEventListener in
  // -------------------------------------------------------------------------
  const hasHost = !!(window.chrome && window.chrome.webview);

  function post(type, payload) {
    const msg = Object.assign({ type }, payload || {});
    if (hasHost) {
      window.chrome.webview.postMessage(msg);
    } else if (mock) {
      mock.receive(msg);
    }
  }

  function onHostMessage(data) {
    let m = data;
    if (typeof m === "string") {
      try { m = JSON.parse(m); } catch (e) { return; }
    }
    if (!m || !m.type) return;
    handle(m);
  }

  if (hasHost) {
    window.chrome.webview.addEventListener("message", (e) => onHostMessage(e.data));
  }

  // -------------------------------------------------------------------------
  // host → web dispatcher (§3.1)
  // -------------------------------------------------------------------------
  function handle(m) {
    switch (m.type) {
      case "theme": return applyTheme(m);
      case "auth.state": return applyAuth(m);
      case "session.init": return applySessionInit(m);
      case "session.list": return renderHistoryList(m.sessions || []);
      case "transcript.load": return loadTranscript(m);
      case "status": return applyStatus(m.state, m.text);
      case "assistant.start": return assistantStart(m.id);
      case "assistant.delta": return assistantDelta(m.id, m.text);
      case "assistant.end": return assistantEnd(m.id);
      case "tool.use": return toolUse(m);
      case "tool.result": return toolResult(m);
      case "permission.request": return permissionRequest(m);
      case "permission.result": return applyPermissionResult(m);
      case "permission.finalize": return finalizePermissionCard(m.requestId, m.status);
      case "permission.expire": return expirePermissionCard(m.requestId);
      case "mode.update": return applyModeUpdate(m);
      case "question.request": return questionRequest(m);
      case "turn.result": return turnResult(m);
      case "usage.update": return usageUpdate(m);
      case "system.note": return systemNote(m);
      case "thinking.start": return thinkingStart(m.id);
      case "thinking.delta": return thinkingDelta(m.id, m.text, m.estimatedTokens);
      case "thinking.end": return thinkingEnd(m.id);
      case "attach.added": return attachAdded(m.attachments || []);
      case "files.list": return onFilesList(m.files || []);
      case "slash.commands": return onSlashCommands(m.commands || []);
      case "remote.state": return applyRemoteState(m);
      case "activeFile": return applyActiveFile(m);
      case "composer.append": return appendToComposer(m.text);
      case "banner.settings": return applyBannerSettings(m);
      case "error": return errorMessage(m.message);
    }
  }

  function onFilesList(files) {
    state.workspaceFiles = files;
    maybeOpenAtAutocomplete(); // re-evaluate a pending "@…" fragment
  }

  /** CLI-reported slash commands (bare names) replace the static fallback list.
      /help stays — it is answered host-side even though the CLI rejects it. */
  function onSlashCommands(names) {
    if (!names.length) return;
    const cmds = names.map((n) => {
      const command = String(n).charAt(0) === "/" ? String(n) : "/" + n;
      return { command, desc: SLASH_DESCRIPTIONS[command] || "" };
    });
    if (!cmds.some((c) => c.command === "/help")) {
      cmds.push({ command: "/help", desc: SLASH_DESCRIPTIONS["/help"] });
    }
    SLASH_COMMANDS.length = 0;
    Array.prototype.push.apply(SLASH_COMMANDS, cmds);
  }

  /** Host returns picker results for attach.files / attach.context / attach.browse. */
  function attachAdded(attachments) {
    attachments.forEach((a) =>
      addAttachment(a && (a.name || a.path) ? a.name || a.path : String(a), a && a.path)
    );
  }

  // -------------------------------------------------------------------------
  // Theme (§6/§8)
  // -------------------------------------------------------------------------
  function applyTheme(m) {
    state.themeMode = m.mode || state.themeMode;
    state.resolvedTheme = m.resolved || (m.mode === "light" ? "light" : "dark");
    const docEl = document.documentElement;
    docEl.classList.add("themed-transition");
    docEl.setAttribute("data-theme", state.resolvedTheme);
    // Replace the inline var overrides on :root. We must clear the ones set by a
    // previous theme message first — switching from "auto" (which ships VS-theme
    // vars like --bg) to an explicit dark/light mode (which ships empty vars and
    // relies on the data-theme palette) would otherwise leave stale backgrounds
    // behind, so only the palette-only colors change.
    const prevKeys = state.appliedThemeVars || [];
    for (let i = 0; i < prevKeys.length; i++) docEl.style.removeProperty(prevKeys[i]);
    const nextKeys = [];
    if (m.vars && typeof m.vars === "object") {
      for (const k in m.vars) {
        if (Object.prototype.hasOwnProperty.call(m.vars, k)) {
          docEl.style.setProperty(k, m.vars[k]);
          nextKeys.push(k);
        }
      }
    }
    state.appliedThemeVars = nextKeys;
    if (state.accent) applyAccent(state.accent); // recompute hover shade for the new theme
  }

  // ---- Custom brand/accent color ------------------------------------------
  // Applied as inline overrides on :root so they win over both theme palettes.
  // Derived shades (hover/faint + Claude's bubble tint) are computed from the hex.
  // hex === "" reverts to the built-in per-theme accent.
  const ACCENT_KEYS = [
    "--accent", "--accent-hover", "--accent-faint",
    "--msg-assistant-bg", "--msg-assistant-border", "--msg-assistant-fg",
  ];
  function hexToRgb(hex) {
    let h = String(hex || "").trim().replace(/^#/, "");
    if (h.length === 3) h = h[0] + h[0] + h[1] + h[1] + h[2] + h[2];
    if (!/^[0-9a-fA-F]{6}$/.test(h)) return null;
    return { r: parseInt(h.slice(0, 2), 16), g: parseInt(h.slice(2, 4), 16), b: parseInt(h.slice(4, 6), 16) };
  }
  function mixToward(c, target, t) { return Math.round(c + (target - c) * t); }
  function applyAccent(hex) {
    const docEl = document.documentElement;
    const rgb = hexToRgb(hex);
    state.accent = rgb ? "#" + [rgb.r, rgb.g, rgb.b].map((x) => x.toString(16).padStart(2, "0")).join("") : "";
    if (!rgb) { ACCENT_KEYS.forEach((k) => docEl.style.removeProperty(k)); return; }
    // lighten in dark themes, darken slightly in light themes for the hover shade
    const toward = state.resolvedTheme === "light" ? 0 : 255;
    const hover = `rgb(${mixToward(rgb.r, toward, 0.18)},${mixToward(rgb.g, toward, 0.18)},${mixToward(rgb.b, toward, 0.18)})`;
    const rgba = (a) => `rgba(${rgb.r},${rgb.g},${rgb.b},${a})`;
    docEl.style.setProperty("--accent", state.accent);
    docEl.style.setProperty("--accent-hover", hover);
    docEl.style.setProperty("--accent-faint", rgba(0.16));
    docEl.style.setProperty("--msg-assistant-bg", rgba(0.14));
    docEl.style.setProperty("--msg-assistant-border", rgba(0.46));
    docEl.style.setProperty("--msg-assistant-fg", state.accent);
  }

  function applyAuth(m) {
    state.loggedIn = !!m.loggedIn;
    state.authMode = m.mode || "none";
    if (state.messages.length === 0) renderEmptyOrSignin();
  }

  function applySessionInit(m) {
    state.sessionId = m.sessionId;
    state.title = m.title || "Untitled";
    state.model = m.model || state.model;
    state.effort = m.effort || state.effort;
    state.planMode = !!m.planMode;
    state.ultracode = !!m.ultracode;
    state.verbosity = m.verbosity || state.verbosity;
    applyVerbosity();
    if (m.accent !== undefined) applyAccent(m.accent); // sticky custom brand color
    state.permissionMode = m.permissionMode || state.permissionMode;
    if (m.autoAcceptCommands !== undefined) state.autoAcceptCommands = !!m.autoAcceptCommands;
    if (m.reviewEditsInEditor !== undefined) state.reviewEditsInEditor = !!m.reviewEditsInEditor;
    state.cwd = m.cwd || "";
    state.tokens = m.tokens || 0;
    state.contextTokens = m.contextTokens || 0;
    state.limits = m.limits || { sessionPct: 0, weeklyPct: 0 };
    state.plan = m.plan || "";
    state.messages = [];
    state.activeAssistant = null;
    state.pendingPermissions.clear(); // fresh view — drop any stale open-card ids
    transcriptInner.innerHTML = "";
    renderEmptyOrSignin();
    updateTitle();
    updateModelModeLabel();
    updateStatusbar();
    applyStatus("ready");
    // First session.init carries the persisted opt-in state + installed version → consent popup
    // (asks about announcements AND updates) or fetch the allowed banners (once per window load).
    evaluateBanners({
      noticeEnabled: !!m.noticeFetchEnabled,
      noticeDecided: !!m.noticeFetchDecided,
      updateEnabled: !!m.updateCheckEnabled,
      updateDecided: !!m.updateCheckDecided,
      appVersion: m.appVersion || "",
    });
  }

  function loadTranscript(m) {
    state.sessionId = m.sessionId;
    state.title = m.title || state.title;
    state.messages = [];
    state.activeAssistant = null;
    state.pendingPermissions.clear(); // fresh view — drop any stale open-card ids
    transcriptInner.innerHTML = "";
    updateTitle();
    const msgs = m.messages || [];
    if (msgs.length === 0) {
      renderEmptyOrSignin();
    } else {
      msgs.forEach(renderHistoricMessage);
    }
    scrollToBottom(true);
  }

  // -------------------------------------------------------------------------
  // Status (§5.5)
  // -------------------------------------------------------------------------
  const STATUS_TEXT = {
    ready: "Ready",
    working: "Working…",
    "waiting-permission": "Awaiting approval",
    error: "Error",
  };
  function applyStatus(s, text) {
    const prev = state.status;
    state.status = s;
    statusbar.setAttribute("data-state", s);
    statusText.textContent = text || STATUS_TEXT[s] || s;
    // send <-> stop
    if (s === "working") {
      sendBtn.classList.add("stop");
      sendBtn.disabled = false;
      sendBtn.title = "Stop";
      showWorkingIndicator();
    } else {
      sendBtn.classList.remove("stop");
      sendBtn.title = "Send";
      updateSendEnabled();
      hideWorkingIndicator();
    }
    // permission expiry: status moved away while one or more cards are still open
    // (host abandoned them — e.g. turn ended/errored). Expire every still-pending card.
    // Cards the user just answered are already removed from the set, so they're untouched.
    if (prev === "waiting-permission" && s !== "waiting-permission" && state.pendingPermissions.size) {
      Array.from(state.pendingPermissions).forEach(expirePermissionCard);
    }
    // orphaned thinking: the turn reached a terminal state (Stop/error/normal end) — drop any
    // "✻ Thinking…" line that never got a thinking.end (interrupted turns emit none).
    if (s !== "working" && s !== "waiting-permission") finalizeActiveThinking();
  }

  // "Working" indicator — a flying rocket + a random Space-Astrogator one-liner, pinned to the
  // bottom of the transcript while a turn runs (appendNode keeps it last). The phrases come from
  // the build-time config (WebUI/config.js → workingPhrases); the fallback keeps it working if
  // config.js is missing or supplies an empty/invalid list (e.g. isolated browser testing).
  const WORKING_PHRASES = (Array.isArray(CPFC_CFG.workingPhrases)
    && CPFC_CFG.workingPhrases.filter((p) => typeof p === "string" && p.trim()).length)
    ? CPFC_CFG.workingPhrases.filter((p) => typeof p === "string" && p.trim())
    : ["Working…"];
  let workingEl = null;
  let workingPhraseIdx = -1;
  function randomWorkingPhrase() {
    if (WORKING_PHRASES.length <= 1) return WORKING_PHRASES[0] || "";
    let i;
    do { i = Math.floor(Math.random() * WORKING_PHRASES.length); } while (i === workingPhraseIdx);
    workingPhraseIdx = i;
    return WORKING_PHRASES[i];
  }
  function showWorkingIndicator() {
    if (workingEl && workingEl.parentNode) return; // already flying
    workingEl = el("div", "working-indicator");
    workingEl.appendChild(el("span", "rocket", "🚀"));
    workingEl.appendChild(el("span", "wi-text", randomWorkingPhrase()));
    workingEl.appendChild(el("span", "wi-dots"));
    clearEmptyState();
    const atBottom = isNearBottom();
    transcriptInner.appendChild(workingEl);
    if (atBottom) scrollToBottom();
  }
  // swap the rocket's one-liner whenever new content lands in the transcript
  function refreshWorkingPhrase() {
    if (!workingEl) return;
    const t = workingEl.querySelector(".wi-text");
    if (t) t.textContent = randomWorkingPhrase();
  }
  function hideWorkingIndicator() {
    if (workingEl && workingEl.parentNode) {
      const atBottom = isNearBottom();
      workingEl.parentNode.removeChild(workingEl);
      if (atBottom) scrollToBottom(); // stay pinned after the rocket leaves the bottom
    }
    workingEl = null;
  }

  /** ≥75% warn (yellow), ≥90% crit (red) — applied to the S/W usage meters. */
  function setMeterLevel(meterEl, pct) {
    meterEl.classList.toggle("crit", pct >= 90);
    meterEl.classList.toggle("warn", pct >= 75 && pct < 90);
  }

  function updateStatusbar() {
    const sp = clampPct(state.limits.sessionPct);
    const wp = clampPct(state.limits.weeklyPct);
    meterSession.style.width = sp + "%";
    meterWeekly.style.width = wp + "%";
    labelSession.textContent = "Session " + sp + "%";
    labelWeekly.textContent = "Weekly " + wp + "%";
    setMeterLevel(statusSession, sp);
    setMeterLevel(statusWeekly, wp);
    // resets_at from the usage endpoint — shown as native tooltips on the meters
    statusSession.title = meterTooltip("Session usage", sp, state.limits.sessionResetsAt);
    statusWeekly.title = meterTooltip("Weekly usage", wp, state.limits.weeklyResetsAt);
    if (state.plan) {
      planLabel.textContent = state.plan;
      planBadge.hidden = false;
    } else {
      planBadge.hidden = true;
    }
  }

  // -------------------------------------------------------------------------
  // Active-file reference chip (right of the slash button) — auto-adds the active
  // editor file to each prompt; click toggles the setting (strike-through = off).
  // -------------------------------------------------------------------------
  function applyActiveFile(m) {
    state.activeFile = {
      name: m.name || null,
      path: m.path || null,
      optionEnabled: m.optionEnabled !== false, // option off → feature off, chip hidden
      enabled: m.enabled !== false,             // per-session toggle (strike-through)
      lines: m.lines || null,                   // selected line range, e.g. "10-20"
    };
    renderActiveFileChip();
  }

  function renderActiveFileChip() {
    const af = state.activeFile;
    // Option off (master switch) or no active file → chip hidden entirely.
    if (!af.name || !af.optionEnabled) { activeFileChip.hidden = true; return; }
    activeFileChip.hidden = false;
    activeFileName.textContent = af.name;
    activeFileLines.textContent = af.lines ? ":" + af.lines : "";
    activeFileLines.hidden = !af.lines; // fixed-width, stays visible while the name truncates
    activeFileChip.classList.toggle("off", !af.enabled);
    activeFileChip.setAttribute("aria-pressed", af.enabled ? "true" : "false");
    const what = af.name + (af.lines ? " (lines " + af.lines + ")" : "");
    activeFileChip.title = (af.enabled
      ? "Referencing " + what + " in your prompts"
      : what + " is not referenced this session")
      + "\n" + (af.path || "") + "\nClick to " + (af.enabled ? "disable" : "enable")
      + " for this session (change the default in options)";
  }

  activeFileChip.addEventListener("click", () => {
    if (activeFileChip.disabled) return;
    const enabled = !state.activeFile.enabled;
    state.activeFile.enabled = enabled; // optimistic; host echoes via activeFile
    renderActiveFileChip();
    post("activeFile.setEnabled", { enabled });
  });


  function clampPct(v) { v = Number(v) || 0; return Math.max(0, Math.min(100, Math.round(v))); }

  /** Meter tooltip: "Session usage: 12% · resets 14:30" (reset time optional). */
  function meterTooltip(label, pct, resetsAt) {
    let s = label + ": " + pct + "%";
    const d = resetsAt ? new Date(resetsAt) : null;
    if (d && !isNaN(d.getTime())) s += " · resets " + formatResetTime(d);
    return s;
  }

  /** Local time for today, "tomorrow 14:30", weekday+date beyond that. */
  function formatResetTime(d) {
    const now = new Date();
    const time = d.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" });
    if (d.toDateString() === now.toDateString()) return time;
    const tomorrow = new Date(now.getFullYear(), now.getMonth(), now.getDate() + 1);
    if (d.toDateString() === tomorrow.toDateString()) return "tomorrow " + time;
    return d.toLocaleDateString([], { weekday: "short", month: "short", day: "numeric" }) + " " + time;
  }

  /** Tool/thinking cards start expanded only in "detailed"; class suffix for card creation. */
  function collapsedInit() { return state.verbosity === "detailed" ? "" : " collapsed"; }

  /**
   * Verbosity:
   *  - compact  → hides system notes + thinking entirely (CSS .v-compact)
   *  - normal   → everything shown; thinking + tool cards collapsed (click to expand)
   *  - detailed → thinking + tool cards expanded by default (input/output visible)
   * Switching re-applies the density to cards already in the transcript (todo cards stay open).
   */
  function applyVerbosity() {
    root.classList.toggle("v-compact", state.verbosity === "compact");
    root.classList.toggle("v-detailed", state.verbosity === "detailed");
    const expand = state.verbosity === "detailed";
    transcriptInner.querySelectorAll(".thinking-card, .tool-card:not(.todo-card)")
      .forEach((c) => c.classList.toggle("collapsed", !expand));
  }
  function formatNum(n) { return (Number(n) || 0).toLocaleString("en-US"); }

  function updateTitle() {
    titleEl.textContent = state.title || "Untitled";
    titleEl.title = state.title || "Untitled";
  }

  function updateModelModeLabel() {
    const md = MODELS.find((x) => x.id === state.model);
    const modeLabel = state.planMode ? "Plan" : PERMISSION_LABELS[state.permissionMode] || "Ask";
    let full = (md ? md.label : state.model) + " · " + modeLabel;
    if (state.ultracode) full += " · Ultra";
    modelModeLabel.textContent = full;
    // compact: show only model short label is handled by CSS truncation; provide model label
  }

  // -------------------------------------------------------------------------
  // Empty / sign-in state (§5.2)
  // -------------------------------------------------------------------------
  function renderEmptyOrSignin() {
    transcriptInner.innerHTML = "";
    if (!state.loggedIn) {
      const wrap = el("div", "signin-state");
      wrap.appendChild(el("div", "msg-line", "Not signed in to Claude Code"));
      const btn = el("button", "signin-btn", "Sign in");
      btn.addEventListener("click", () => post("slash.run", { command: "/login" }));
      wrap.appendChild(btn);
      transcriptInner.appendChild(wrap);
      return;
    }
    const wrap = el("div", "empty-state");
    const wm = el("div", "wordmark");
    wm.appendChild(document.createTextNode("Code Astrogator"));
    wrap.appendChild(wm);
    wrap.appendChild(buildLogo());
    wrap.appendChild(el("div", "empty-todo", "// pre-flight check complete. where to, captain?"));
    transcriptInner.appendChild(wrap);
  }

  // Extension logo (astrogator robot); served from the WebUI folder (virtual host / file://).
  function buildLogo() {
    const img = document.createElement("img");
    img.setAttribute("class", "robot logo-img");
    img.setAttribute("src", "logo.png");
    img.setAttribute("width", "96");
    img.setAttribute("height", "96");
    img.setAttribute("alt", "");
    img.setAttribute("aria-hidden", "true");
    return img;
  }

  function clearEmptyState() {
    const es = transcriptInner.querySelector(".empty-state, .signin-state");
    if (es) es.remove();
  }

  // -------------------------------------------------------------------------
  // Message helpers
  // -------------------------------------------------------------------------
  function el(tag, cls, text) {
    const n = document.createElement(tag);
    if (cls) n.className = cls;
    if (text != null) {
      n.textContent = text;
      if (cls && (cls.includes("tool-summary") || cls.includes("perm-path") || cls.includes("chip-name") || cls.includes("att-name") || cls.includes("active-file-name") || cls.includes("hi-title") || cls.includes("hi-preview"))) {
        n.title = text;
      }
    }
    return n;
  }

  function makeMsgRow(role, gutterGlyph) {
    const row = el("div", "msg msg-" + role);
    const bubble = el("div", "msg-body");      // tinted card (bg/padding live here)
    // role glyph — now INSIDE the card; assistant uses the head logo instead of a glyph
    const g = el("div", "gutter");
    if (role === "assistant") {
      const av = document.createElement("img");
      av.setAttribute("class", "gutter-logo");
      av.setAttribute("src", "head.png");
      av.setAttribute("alt", "");
      av.setAttribute("aria-hidden", "true");
      av.setAttribute("draggable", "false");
      g.appendChild(av);
    } else {
      g.textContent = gutterGlyph || "";
    }
    const content = el("div", "msg-content");
    bubble.appendChild(g);
    bubble.appendChild(content);
    row.appendChild(bubble);
    return { row, body: content };
  }

  function appendNode(node) {
    clearEmptyState();
    const atBottom = isNearBottom();
    // keep the "working" rocket pinned to the very bottom — new content goes above it
    if (workingEl && workingEl.parentNode === transcriptInner) {
      transcriptInner.insertBefore(node, workingEl);
      refreshWorkingPhrase(); // new chat content → swap the rocket's one-liner
    } else {
      transcriptInner.appendChild(node);
    }
    if (atBottom) scrollToBottom();
    else showJumpPill(true);
    return node;
  }

  // -------------------------------------------------------------------------
  // User message
  // -------------------------------------------------------------------------
  function renderUserMessage(text, attachments) {
    const { row, body } = makeMsgRow("user", "›");
    body.classList.add("md");
    body.appendChild(renderMarkdown(text));
    appendMsgAttachments(body, attachments);
    appendNode(row);
  }

  // Renders attached-file chips below a user message (shared by live + historic).
  // Each attachment is { name, path } (or a bare string name).
  function appendMsgAttachments(body, attachments) {
    if (!attachments || !attachments.length) return;
    const ch = el("div", "msg-attachments");
    attachments.forEach((a) => {
      const name = (a && (a.name || a.path)) || a;
      if (!name) return;
      const chip = el("span", "att-chip");
      chip.appendChild(iconFile());
      const nm = el("span", "att-name", String(name));
      if (a && a.path) chip.title = a.path;
      chip.appendChild(nm);
      ch.appendChild(chip);
    });
    body.appendChild(ch);
  }

  // -------------------------------------------------------------------------
  // Assistant streaming (§5.2, §9)
  // -------------------------------------------------------------------------
  function assistantStart(id) {
    const { row, body } = makeMsgRow("assistant", "✳");
    const stream = el("div", "streaming-text");
    const textNode = document.createTextNode("");
    stream.appendChild(textNode);
    const caret = el("span", "caret");
    stream.appendChild(caret);
    body.appendChild(stream);
    state.activeAssistant = { id, textNode, accText: "", el: body, caret };
    appendNode(row);
  }

  function assistantDelta(id, text) {
    const a = state.activeAssistant;
    if (!a || a.id !== id) return;
    const atBottom = isNearBottom();        // capture BEFORE the text grows the height
    a.accText += text || "";
    a.textNode.nodeValue = a.accText;       // append to existing text node, no re-layout of markdown
    if (atBottom) scrollToBottom();
  }

  function assistantEnd(id) {
    const a = state.activeAssistant;
    if (!a || a.id !== id) return;
    const atBottom = isNearBottom();        // capture BEFORE the markdown re-render changes height
    if (a.caret && a.caret.parentNode) a.caret.remove();
    // final markdown render
    a.el.innerHTML = "";
    a.el.classList.add("md");
    a.el.appendChild(renderMarkdown(a.accText));
    state.activeAssistant = null;
    if (atBottom) scrollToBottom();
  }

  // -------------------------------------------------------------------------
  // System notes (display option "C" — dim one-liners)
  // -------------------------------------------------------------------------
  function systemNote(m) {
    appendNode(el("div", "sys-note", m.text || ""));
  }

  // -------------------------------------------------------------------------
  // Thinking (decision #13, revised): the print-mode CLI redacts thinking text
  // (empty deltas, only estimated_tokens) — so the default is a TRANSIENT dim
  // "✻ Thinking…" line that disappears on thinking.end. Only if real text ever
  // arrives does it upgrade to a collapsible card worth expanding.
  // -------------------------------------------------------------------------
  function thinkingStart(id) {
    const line = el("div", "sys-note thinking-line", "✻ Thinking…");
    line.dataset.thinkingId = id;
    state.activeThinking = { id, line, card: null, textNode: null, status: null, text: "", est: 0 };
    appendNode(line);
  }

  function thinkingDelta(id, text, estimatedTokens) {
    const t = state.activeThinking;
    if (!t || t.id !== id) return;
    // print-mode CLI redacts thinking text (empty deltas) → keep the plain "✻ Thinking…" line,
    // no token counter. Only real streamed text upgrades to a card.
    if (!text) return;
    const atBottom = isNearBottom();
    t.text += text;
    if (!t.card) upgradeThinkingToCard(t);
    t.textNode.nodeValue = t.text;
    const summary = t.card.querySelector(".tool-summary");
    if (summary) {
      const summaryText = t.text.split("\n")[0].slice(0, 120);
      summary.textContent = summaryText;
      summary.title = summaryText;
    }
    if (atBottom) scrollToBottom();
  }

  function thinkingEnd(id) {
    const t = state.activeThinking;
    if (!t || t.id !== id) return;
    if (t.card) {
      t.status.innerHTML = "";
      t.status.appendChild(iconCheck());
    } else if (t.line) {
      t.line.remove(); // nothing to keep — purely transient feedback
    }
    state.activeThinking = null;
  }

  /**
   * Drop an orphaned "✻ Thinking…" item when the turn ends without a thinking.end —
   * e.g. the user hit Stop (the interrupted CLI emits no thinking.end). The transient
   * line is removed (nothing to keep); a card (real streamed text) just loses its spinner.
   * No-op on normal turns, where thinking.end already cleared state.activeThinking.
   */
  function finalizeActiveThinking() {
    const t = state.activeThinking;
    if (!t) return;
    if (t.card) {
      if (t.status) t.status.innerHTML = ""; // stop the spinner; the partial text stays
    } else if (t.line) {
      t.line.remove();
    }
    state.activeThinking = null;
  }

  /** Swaps the transient line for a collapsible card once real text streams in. */
  function upgradeThinkingToCard(t) {
    const collapsed = state.verbosity !== "detailed";
    const card = el("div", "thinking-card" + (collapsed ? " collapsed" : ""));
    card.dataset.thinkingId = t.id;
    const head = el("div", "tool-head");
    head.appendChild(el("span", "thinking-glyph", "✻"));
    head.appendChild(el("span", "tool-name", "Thinking"));
    head.appendChild(el("span", "tool-summary", ""));
    const status = el("span", "tool-status");
    status.appendChild(el("span", "spinner"));
    head.appendChild(status);
    card.appendChild(head);
    const body = el("div", "thinking-body");
    const textNode = document.createTextNode("");
    body.appendChild(textNode);
    card.appendChild(body);
    head.addEventListener("click", () => card.classList.toggle("collapsed"));
    t.line.replaceWith(card);
    t.line = null;
    t.card = card;
    t.textNode = textNode;
    t.status = status;
  }

  // -------------------------------------------------------------------------
  // Tool cards (+ special renderers: Task agents, TodoWrite, ExitPlanMode)
  // -------------------------------------------------------------------------
  function toolUse(m) {
    if (m.name === "TodoWrite") return todoCard(m);
    if (m.name === "ExitPlanMode" || m.name === "exit_plan_mode") return planCard(m);
    // AskUserQuestion: render a normal tool card here; when the permission hook fires
    // (question.request) it replaces this card with the interactive question card — same
    // tool_use_id, same pattern as the permission flow. (Bypass mode has no hook → the
    // tool card just resolves to the CLI's "did not answer".)

    const isAgent = m.name === "Task" || m.name === "Agent"; // decision #14
    const card = el("div", "tool-card" + collapsedInit() + (isAgent ? " agent-card" : ""));
    card.dataset.toolId = m.id;
    const head = el("div", "tool-head");
    head.appendChild(toolIcon(m.name));
    head.appendChild(el("span", "tool-name", isAgent ? "Agent" : m.name || "Tool"));
    const mcp = isMcpTool(m.name);
    const headLabel = isAgent && m.input && m.input.description
      ? m.input.description
      : toolHeadLabel(m.name, m.input); // file/lines for Read, command for Bash, …
    head.appendChild(el("span", "tool-summary", headLabel || (mcp ? "" : "running…")));
    if (headLabel || mcp) card.dataset.headLabel = "1"; // keep it; don't let tool.result overwrite
    const status = el("span", "tool-status");
    status.appendChild(el("span", "spinner"));
    head.appendChild(status);
    card.appendChild(head);
    const bodyWrap = el("div", "tool-body");
    const pre = el("pre");
    pre.textContent = prettyJson(m.input);
    bodyWrap.appendChild(pre);
    card.appendChild(bodyWrap);
    head.addEventListener("click", () => card.classList.toggle("collapsed"));
    appendNode(card);
  }

  /** TodoWrite as a checklist card (decision #15) — open by default, head collapses. */
  function todoCard(m) {
    const card = el("div", "tool-card todo-card");
    card.dataset.toolId = m.id;
    const head = el("div", "tool-head");
    head.appendChild(toolIcon(m.name));
    head.appendChild(el("span", "tool-name", "Todos"));
    head.appendChild(el("span", "tool-summary", ""));
    const status = el("span", "tool-status");
    status.appendChild(el("span", "spinner"));
    head.appendChild(status);
    card.appendChild(head);
    const list = el("div", "tool-body todo-list");
    const todos = (m.input && m.input.todos) || [];
    let done = 0;
    todos.forEach((t) => {
      const st = t.status || "pending";
      if (st === "completed") done++;
      const row = el("div", "todo-item " + st);
      row.appendChild(el("span", "todo-mark", st === "completed" ? "☑" : st === "in_progress" ? "◐" : "☐"));
      row.appendChild(el("span", "todo-text", t.content || t.activeForm || ""));
      list.appendChild(row);
    });
    const summaryText = done + "/" + todos.length;
    const summaryEl = head.querySelector(".tool-summary");
    summaryEl.textContent = summaryText;
    summaryEl.title = summaryText;
    card.appendChild(list);
    head.addEventListener("click", () => card.classList.toggle("collapsed"));
    appendNode(card);
  }

  /** ExitPlanMode rendered as a full plan card with markdown (decision #18).
      Approval runs through the regular permission card once the MCP bridge lands. */
  function planCard(m) {
    const card = el("div", "plan-card");
    card.dataset.toolId = m.id;
    const head = el("div", "perm-head");
    head.appendChild(el("span", "perm-tool", "Plan"));
    head.appendChild(el("span", "perm-path", "proposed plan"));
    card.appendChild(head);
    const body = el("div", "plan-body md");
    body.appendChild(renderMarkdown((m.input && m.input.plan) || ""));
    card.appendChild(body);
    appendNode(card);
  }

  function toolResult(m) {
    const card = transcriptInner.querySelector('.tool-card[data-tool-id="' + cssEscape(m.id) + '"]');
    if (!card) return;
    const status = card.querySelector(".tool-status");
    const summary = card.querySelector(".tool-summary");
    status.innerHTML = "";
    if (m.status === "ok") {
      status.appendChild(iconCheck());
    } else {
      status.appendChild(iconCross());
    }
    card.classList.remove("tool-ok", "tool-err");
    card.classList.add(m.status === "ok" ? "tool-ok" : "tool-err");
    if (card.classList.contains("todo-card")) return; // checklist keeps its n/m summary
    if (m.summary != null) {
      // keep the input-derived title (Read filename, command, …); otherwise show the result's
      // first line. Long output always goes into the body behind "Show more" (decision #17).
      if (!card.dataset.headLabel) {
        const summaryText = m.summary.split("\n")[0].slice(0, 160);
        summary.textContent = summaryText;
        summary.title = summaryText;
      }
      if (m.summary.length > 200) {
        const bodyWrap = card.querySelector(".tool-body");
        const out = el("pre", "tool-output");
        out.textContent = m.summary;
        bodyWrap.appendChild(out);
        if (m.summary.length > 1200) {
          out.classList.add("clamped");
          const btn = el("button", "show-more", "Show more");
          btn.addEventListener("click", (e) => {
            e.stopPropagation();
            const clamped = out.classList.toggle("clamped");
            btn.textContent = clamped ? "Show more" : "Show less";
          });
          bodyWrap.appendChild(btn);
        }
      }
    }
  }

  // Short one-line preview of a tool's input for card headers (e.g. the Bash/PowerShell
  // command). Newlines/whitespace runs collapse to single spaces; truncated.
  function commandPreview(input) {
    if (!input) return "";
    const cmd = input.command || input.cmd || input.script || "";
    if (!cmd) return "";
    const s = String(cmd).replace(/\s+/g, " ").trim();
    return s.length > 160 ? s.slice(0, 160) + "…" : s;
  }
  // Header subtitle for a tool/permission card: file path if any, else the command preview.
  function cardHeadDetail(m) {
    return (m.diff && m.diff.path) || (m.input && m.input.file_path) || commandPreview(m.input) || "";
  }

  function baseName(p) {
    p = String(p || "");
    const i = Math.max(p.lastIndexOf("/"), p.lastIndexOf("\\"));
    return i >= 0 ? p.slice(i + 1) : p;
  }
  // MCP tools (mcp__<server>__<tool>) carry the full name in the tool-name span already, so
  // their header detail stays empty — no JSON-output "{" leaking into the title.
  function isMcpTool(name) { return (name || "").indexOf("mcp__") === 0; }
  // Descriptive tool-card title derived from the INPUT (not the output) — e.g. Read shows
  // "File.cs:120-170", Bash shows the command. "" → fall back to "running…" / result line.
  function toolHeadLabel(name, input) {
    if (!input) return "";
    switch (name) {
      case "Read":
      case "NotebookRead": {
        const f = baseName(input.file_path || input.notebook_path || input.path);
        if (!f) return "";
        const o = Number(input.offset) || 0, l = Number(input.limit) || 0;
        if (o > 0 && l > 0) return f + ":" + o + "-" + (o + l - 1);
        if (o > 0) return f + ":" + o + "+";
        return f;
      }
      case "Edit":
      case "Write":
      case "MultiEdit":
      case "NotebookEdit":
        return baseName(input.file_path || input.notebook_path || input.path);
      case "Grep": {
        const pat = input.pattern ? '"' + String(input.pattern).slice(0, 60) + '"' : "";
        const where = input.path ? " in " + baseName(input.path) : (input.glob ? " " + input.glob : "");
        return (pat + where).trim();
      }
      case "Glob":
        return input.pattern ? String(input.pattern).slice(0, 100) : "";
      default:
        return commandPreview(input); // Bash/PowerShell and anything else with a command
    }
  }

  // -------------------------------------------------------------------------
  // Permission / diff card (§5.2)
  // -------------------------------------------------------------------------
  function permissionRequest(m) {
    // the tool.use card was already shown (CLI emits it before the permission prompt) —
    // the permission card represents the same call, so drop the redundant tool card
    const existingTool = transcriptInner.querySelector('.tool-card[data-tool-id="' + cssEscape(m.requestId) + '"]');
    if (existingTool) existingTool.remove();
    // guard against a second permission.request for the same tool_use_id. This happens when the
    // user switches the permission mode mid-turn: the host pre-renders an auto-approved card while
    // the still-running process (launched in the old mode) also fires the real permission hook.
    // The latest request wins — drop the earlier perm-card so we never leave an orphaned card with
    // live Approve/Reject buttons that setPermCardState/applyPermissionResult (first-match) miss.
    const existingPerm = transcriptInner.querySelector('.perm-card[data-request-id="' + cssEscape(m.requestId) + '"]');
    if (existingPerm) existingPerm.remove();

    const card = el("div", "perm-card");
    card.dataset.requestId = m.requestId;
    const head = el("div", "perm-head");
    head.appendChild(el("span", "perm-chevron", "▸")); // far left; re-expand toggle (decided only)
    head.appendChild(el("span", "perm-tool", m.toolName || "Permission"));
    head.appendChild(el("span", "perm-path", cardHeadDetail(m)));
    head.appendChild(el("span", "perm-status")); // filled in once decided/expired
    card.appendChild(head);

    // edit-review mode (toggle on + a file edit): the diff is reviewed in the editor, not here.
    const editInEditor = !!m.editInEditor && !m.autoApproved;

    const body = el("div", "perm-body"); // collapses after a decision
    if (editInEditor) {
      const n = m.hunkCount || 0;
      const label = n > 0
        ? (n === 1 ? "1 change to review in the editor" : n + " changes to review in the editor")
        : "Review this edit in the editor";
      body.appendChild(el("div", "perm-editreview", label));
    } else if (m.diff && (m.diff.oldText != null || m.diff.newText != null)) {
      body.appendChild(buildDiff(m.diff.oldText || "", m.diff.newText || "", m.diff.startLine || 1));
    } else {
      const j = el("div", "perm-json");
      const pre = el("pre");
      pre.textContent = prettyJson(m.input);
      j.appendChild(pre);
      body.appendChild(j);
    }
    card.appendChild(body);

    // auto-approved edits (acceptEdits/bypass): no decision needed — skip the buttons and
    // render the card pre-decided as "approved" (tool.result then upgrades it to "applied")
    if (editInEditor) {
      const actions = el("div", "perm-actions");
      // "Accept all" applies the full edit without opening the editor — a plain allow, which the
      // host echoes back as the original input (= every hunk accepted).
      const acceptAll = el("button", "btn-approve", "Accept all");
      acceptAll.title = "Apply every change without opening the editor";
      acceptAll.addEventListener("click", () => decidePermission(m.requestId, "allow"));
      const open = el("button", "btn-secondary", "Open in editor");
      open.title = "Open the file and accept/reject each change inline";
      open.addEventListener("click", () => post("editReview.open", { requestId: m.requestId }));
      const reject = el("button", "btn-reject", "Reject all");
      reject.addEventListener("click", () => decidePermission(m.requestId, "deny"));
      actions.appendChild(acceptAll);
      actions.appendChild(open);
      actions.appendChild(reject);
      card.appendChild(actions);
    } else if (!m.autoApproved) {
      const actions = el("div", "perm-actions");
      const approve = el("button", "btn-approve", "Approve");
      const reject = el("button", "btn-reject", "Reject");
      approve.addEventListener("click", () => decidePermission(m.requestId, "allow"));
      reject.addEventListener("click", () => decidePermission(m.requestId, "deny"));
      actions.appendChild(approve);
      // "Always": opens a popover to review/edit the patterns, then approve + persist them
      if (m.canApproveAlways) {
        const always = el("button", "btn-always", "Always");
        always.title = "Approve and auto-approve this command/tool from now on";
        always.addEventListener("click", () => openApprovePopover(always, m.requestId, m.approveAlwaysSuggestions || []));
        actions.appendChild(always);
      }
      actions.appendChild(reject);
      card.appendChild(actions);
    }

    // once decided/expired the head toggles the (now read-only) diff back open
    head.addEventListener("click", () => {
      if (card.classList.contains("decided") || card.classList.contains("expired"))
        card.classList.toggle("expanded");
    });

    if (m.autoApproved) {
      appendNode(card);
      setPermCardState(m.requestId, "approved");
    } else {
      // flip status first (removes the working rocket) so the final scroll measures the
      // real bottom — then insert the card and pin to it
      state.pendingPermissions.add(m.requestId);
      applyStatus("waiting-permission");
      updateSendEnabled();
      appendNode(card);
    }
  }

  // "Always" → editable popover: review/edit the patterns to add, then approve + persist.
  // Patterns show in an editable list (one row each, like the settings window). The popover is
  // sized to the card. Cancel leaves the card pending.
  function openApprovePopover(anchorBtn, requestId, suggestions) {
    const card = anchorBtn.closest(".perm-card");
    const list = (suggestions && suggestions.length) ? suggestions.slice() : [""];
    const pop = el("div", "popover approve-pop");
    pop.appendChild(el("div", "approve-pop-title", "Auto-approve patterns to add"));

    const rows = el("div", "approve-list");
    function addRow(value) {
      const row = el("div", "approve-row");
      const inp = document.createElement("input");
      inp.type = "text";
      inp.className = "approve-row-input";
      inp.value = value || "";
      inp.spellcheck = false;
      const del = el("button", "approve-row-del", "✕");
      del.title = "Remove";
      del.addEventListener("click", () => { row.remove(); if (openOverlay) openOverlay.reposition(); });
      row.appendChild(inp);
      row.appendChild(del);
      rows.appendChild(row);
      return inp;
    }
    list.forEach(addRow);
    pop.appendChild(rows);

    const addBtn = el("button", "approve-add", "+ Add pattern");
    addBtn.addEventListener("click", () => { addRow("").focus(); if (openOverlay) openOverlay.reposition(); });
    pop.appendChild(addBtn);

    pop.appendChild(el("div", "approve-pop-hint", "One pattern per row · * = wildcard"));
    const acts = el("div", "approve-pop-actions");
    const cancel = el("button", "btn-reject", "Cancel");
    const save = el("button", "btn-approve", "Add & approve");
    cancel.addEventListener("click", () => closeAllOverlays());
    save.addEventListener("click", () => {
      const patterns = Array.prototype.map.call(rows.querySelectorAll(".approve-row-input"), (i) => i.value.trim())
        .filter(Boolean);
      closeAllOverlays();
      setPermCardState(requestId, "approved");
      post("permission.approveAlways", { requestId: requestId, patterns: patterns });
      state.pendingPermissions.delete(requestId);
      updateSendEnabled();
    });
    acts.appendChild(cancel);
    acts.appendChild(save);
    pop.appendChild(acts);
    openPopover(anchorBtn, pop, { align: "start", side: "top", matchWidthEl: card, hAnchorEl: card });
    setTimeout(() => { const first = rows.querySelector(".approve-row-input"); if (first) first.focus(); }, 0);
  }

  // status → badge label (single source of truth for live + historic cards)
  function permLabel(status) {
    return status === "approved" ? "✓ Approved"
      : status === "applied" ? "✓ Applied"
      : status === "rejected" ? "✕ Rejected"
      : status === "failed" ? "✗ Failed"
      : status === "expired" ? "Expired" : "";
  }
  // approved/applied render green, rejected/failed render red
  function permStatusClass(status) {
    return status === "approved" || status === "applied" ? "approved"
      : status === "rejected" || status === "failed" ? "rejected"
      : status === "expired" ? "expired" : "";
  }

  function setPermCardState(requestId, status) {
    const card = transcriptInner.querySelector('.perm-card[data-request-id="' + cssEscape(requestId) + '"]');
    if (!card || card.classList.contains("decided")) return null;
    card.classList.add("decided", status, permStatusClass(status));
    card.classList.remove("expanded"); // collapse the diff + hide the buttons
    const st = card.querySelector(".perm-status");
    if (st) st.textContent = permLabel(status);
    return card;
  }

  function decidePermission(requestId, behavior) {
    setPermCardState(requestId, behavior === "allow" ? "approved" : "rejected");
    if (behavior === "allow") {
      post("permission.decision", { requestId, behavior: "allow" });
    } else {
      post("permission.decision", { requestId, behavior: "deny", message: "User rejected" });
    }
    state.pendingPermissions.delete(requestId);
    updateSendEnabled();
  }

  // host→web permission.result: an approved edit finished executing (applied / failed)
  function applyPermissionResult(m) {
    const card = transcriptInner.querySelector('.perm-card[data-request-id="' + cssEscape(m.requestId) + '"]');
    if (!card) return;
    card.classList.remove("approved", "applied", "rejected", "failed");
    card.classList.add("decided", m.status, permStatusClass(m.status));
    const st = card.querySelector(".perm-status");
    if (st) st.textContent = permLabel(m.status);
  }

  // host→web mode.update: the permission/plan mode changed host-side (e.g. approving an
  // ExitPlanMode plan exits plan mode → acceptEdits). Refresh the selector label only —
  // no transcript manipulation (unlike session.init).
  function applyModeUpdate(m) {
    if (m.permissionMode) state.permissionMode = m.permissionMode;
    state.planMode = !!m.planMode;
    updateModelModeLabel();
  }

  function expirePermissionCard(requestId) {
    setPermCardState(requestId, "expired");  // permission card (no-op if not one)
    expireQuestionCard(requestId);           // question card (no-op if not one)
    state.pendingPermissions.delete(requestId);
    updateSendEnabled();
  }

  // host→web permission.finalize: the host decided an edit-review card (in the editor or via
  // Reject-all) — stamp the card decided + free the composer, exactly like decidePermission does
  // for a chat decision. A later permission.result then upgrades approved → applied/failed.
  function finalizePermissionCard(requestId, status) {
    setPermCardState(requestId, status || "approved");
    state.pendingPermissions.delete(requestId);
    updateSendEnabled();
  }

  // Mark an open question card as expired (host abandoned/timed it out): collapse it and make it
  // read-only by reusing the "answered" styling, but with an "Expired" summary and no answer sent.
  function expireQuestionCard(requestId) {
    const card = transcriptInner.querySelector('.q-card[data-request-id="' + cssEscape(requestId) + '"]');
    if (!card || card.classList.contains("answered")) return;
    card.classList.add("answered", "expired"); // answered → collapses + disables the options/input
    card.classList.remove("expanded");
    const title = card.querySelector(".q-title");
    if (title) title.textContent = "Claude asked";
    const summary = card.querySelector(".q-summary");
    if (summary) summary.textContent = "Expired — no answer";
    const acts = card.querySelector(".q-actions");
    if (acts) acts.remove();
  }

  // -------------------------------------------------------------------------
  // AskUserQuestion — interactive question card (routed via the permission hook).
  // The chosen option(s)/free text go back as question.answer; the host returns
  // them to the CLI as the tool result. Same card is reused read-only for history.
  // -------------------------------------------------------------------------
  function buildQuestionCard(requestId, questions, prefilled) {
    const answered = !!prefilled;
    const card = el("div", "q-card");
    card.dataset.requestId = requestId;
    if (answered) card.classList.add("answered");

    // collapsible header (like permission cards): chevron + title + answer summary
    const head = el("div", "q-head");
    head.appendChild(el("span", "q-chevron", "▸"));
    head.appendChild(el("span", "q-title", answered ? "Claude asked" : "Claude is asking"));
    const summaryEl = el("span", "q-summary");
    if (answered) summaryEl.textContent = summarizeQuestionAnswers(prefilled);
    head.appendChild(summaryEl);
    head.addEventListener("click", () => {
      if (card.classList.contains("answered")) card.classList.toggle("expanded");
    });
    card.appendChild(head);

    const body = el("div", "q-body"); // collapses once answered
    card.appendChild(body);

    const states = []; // { q, selected:Set, customGetter }
    questions.forEach((q, qi) => {
      const ans = prefilled && prefilled[qi];
      const preSel = new Set((ans && ans.selected) || []);
      const preCustom = (ans && ans.custom) || "";

      const block = el("div", "q-block");
      if (q.header) block.appendChild(el("span", "q-header", q.header));
      block.appendChild(el("div", "q-question", q.question || ""));
      const multi = !!q.multiSelect;
      if (multi) block.appendChild(el("div", "q-hint", "Select one or more"));

      const st = { q: q, selected: new Set(preSel), customEl: null };
      const optsEl = el("div", "q-options");
      (q.options || []).forEach((o) => {
        const label = o && o.label != null ? String(o.label) : String(o);
        const btn = el("button", "q-option");
        btn.appendChild(el("span", "q-option-label", label));
        if (o && o.description) btn.appendChild(el("span", "q-option-desc", o.description));
        if (st.selected.has(label)) btn.classList.add("sel");
        if (!answered) btn.addEventListener("click", () => {
          if (multi) {
            if (st.selected.has(label)) { st.selected.delete(label); btn.classList.remove("sel"); }
            else { st.selected.add(label); btn.classList.add("sel"); }
          } else {
            st.selected.clear();
            optsEl.querySelectorAll(".q-option.sel").forEach((e) => e.classList.remove("sel"));
            st.selected.add(label); btn.classList.add("sel");
            // single question, single-select, no free text typed → decide immediately
            if (questions.length === 1 && !(st.customEl && st.customEl.value.trim())) submit();
          }
        });
        optsEl.appendChild(btn);
      });
      block.appendChild(optsEl);

      // free-text "Other" (the TUI option) — a custom answer alongside the buttons
      const other = document.createElement("input");
      other.type = "text";
      other.className = "q-other-input";
      other.placeholder = "Other… (type a custom answer)";
      other.value = preCustom;
      if (answered) other.disabled = true;
      else other.addEventListener("keydown", (e) => { if (e.key === "Enter") { e.preventDefault(); submit(); } });
      st.customEl = other;
      block.appendChild(other);

      states.push(st);
      body.appendChild(block);
    });

    if (!answered) {
      const actions = el("div", "q-actions");
      const submitBtn = el("button", "btn-approve", "Submit");
      submitBtn.addEventListener("click", submit);
      actions.appendChild(submitBtn);
      body.appendChild(actions);
    }

    function submit() {
      if (card.classList.contains("answered")) return;
      const answers = states.map((s) => {
        const custom = (s.customEl && s.customEl.value || "").trim();
        return {
          header: s.q.header || null,
          question: s.q.question || null,
          selected: Array.from(s.selected),
          custom: custom || undefined,
        };
      });
      post("question.answer", { requestId: requestId, answers: answers });
      // collapse like a decided permission card: title→"Claude asked", summary fills, body hides
      card.classList.add("answered");
      card.classList.remove("expanded");
      head.querySelector(".q-title").textContent = "Claude asked";
      summaryEl.textContent = summarizeQuestionAnswers(answers);
      const acts = card.querySelector(".q-actions");
      if (acts) acts.remove();
      state.pendingPermissions.delete(requestId);
      updateSendEnabled();
    }

    return card;
  }

  // compact one-line summary of answers for the collapsed question card
  function summarizeQuestionAnswers(answers) {
    return (answers || []).map((a) => {
      const parts = (a.selected || []).slice();
      if (a.custom) parts.push(a.custom);
      const val = parts.join(", ") || "—";
      return answers.length > 1 && a.header ? a.header + ": " + val : val;
    }).join(" · ");
  }

  function questionRequest(m) {
    // CLI emits the tool_use before the prompt — the question card represents it
    const existingTool = transcriptInner.querySelector('.tool-card[data-tool-id="' + cssEscape(m.requestId) + '"]');
    if (existingTool) existingTool.remove();
    const card = buildQuestionCard(m.requestId, m.questions || [], null);
    state.pendingPermissions.add(m.requestId);
    applyStatus("waiting-permission");
    updateSendEnabled();
    appendNode(card);
  }

  // Line-based diff via prefix/suffix trim + middle replacement.
  // startLine = 1-based file line where this snippet begins (Edit) so the gutter shows
  // real file line numbers, not snippet-relative ones.
  function buildDiff(oldText, newText, startLine) {
    const base = startLine && startLine > 0 ? startLine : 1;
    const wrap = el("div", "diff");
    const oldLines = String(oldText).split("\n");
    const newLines = String(newText).split("\n");
    let start = 0;
    while (start < oldLines.length && start < newLines.length && oldLines[start] === newLines[start]) start++;
    let endO = oldLines.length, endN = newLines.length;
    while (endO > start && endN > start && oldLines[endO - 1] === newLines[endN - 1]) { endO--; endN--; }

    // size the line-number gutters to the largest number so columns line up
    const maxNo = base + Math.max(oldLines.length, newLines.length) - 1;
    wrap.style.setProperty("--lno-ch", (String(maxNo).length + 0.5) + "ch");

    const addLine = (oldNo, newNo, sign, cls, txt) => {
      const line = el("div", "diff-line " + cls);
      line.appendChild(el("span", "diff-lno", oldNo == null ? "" : String(oldNo)));
      line.appendChild(el("span", "diff-lno", newNo == null ? "" : String(newNo)));
      line.appendChild(el("span", "diff-sign", sign));
      line.appendChild(el("span", "diff-text", txt));
      wrap.appendChild(line);
    };
    for (let i = 0; i < start; i++) addLine(base + i, base + i, " ", "", oldLines[i]);
    for (let i = start; i < endO; i++) addLine(base + i, null, "-", "diff-del", oldLines[i]);
    for (let i = start; i < endN; i++) addLine(null, base + i, "+", "diff-add", newLines[i]);
    for (let i = endO; i < oldLines.length; i++) addLine(base + i, base + endN + (i - endO), " ", "", oldLines[i]);
    return wrap;
  }

  // -------------------------------------------------------------------------
  // Turn result / usage / error
  // -------------------------------------------------------------------------
  function turnResult(m) {
    if (m.contextTokens != null) state.contextTokens = m.contextTokens; // kept for /compact note
    if (m.limits) state.limits = m.limits;
    updateStatusbar();
    // turn separator + readable elapsed time (cost/token counts intentionally dropped)
    appendNode(el("hr", "turn-divider"));
    if (m.durationMs != null)
      appendNode(el("div", "sys-note turn-footer", formatDuration(m.durationMs)));
  }

  // ms → human-readable elapsed, e.g. 9300 → "9s", 130000 → "2m 10s", 3725000 → "1h 2m"
  function formatDuration(ms) {
    const total = Math.max(0, Math.round(Number(ms) / 1000));
    const h = Math.floor(total / 3600);
    const mnt = Math.floor((total % 3600) / 60);
    const s = total % 60;
    if (h > 0) return h + "h " + mnt + "m";
    if (mnt > 0) return mnt + "m " + s + "s";
    return s + "s";
  }
  function usageUpdate(m) {
    if (m.tokens != null) state.tokens = m.tokens;
    if (m.contextTokens != null) state.contextTokens = m.contextTokens;
    if (m.sessionPct != null) state.limits.sessionPct = m.sessionPct;
    if (m.weeklyPct != null) state.limits.weeklyPct = m.weeklyPct;
    if (m.sessionResetsAt !== undefined) state.limits.sessionResetsAt = m.sessionResetsAt;
    if (m.weeklyResetsAt !== undefined) state.limits.weeklyResetsAt = m.weeklyResetsAt;
    if (m.plan) state.plan = m.plan;
    updateStatusbar();
  }
  function errorMessage(message) {
    const block = el("div", "error-block", message || "An error occurred.");
    appendNode(block);
    applyStatus("error", "Error");
    updateSendEnabled();
  }

  // -------------------------------------------------------------------------
  // Historic messages (transcript.load)
  // -------------------------------------------------------------------------
  function renderHistoricMessage(msg) {
    switch (msg.role) {
      case "user": {
        const { row, body } = makeMsgRow("user", "›");
        body.classList.add("md");
        body.appendChild(renderMarkdown(msg.text || ""));
        appendMsgAttachments(body, msg.attachments);
        transcriptInner.appendChild(row);
        break;
      }
      case "assistant": {
        const { row, body } = makeMsgRow("assistant", "✳");
        body.classList.add("md");
        body.appendChild(renderMarkdown(msg.text || ""));
        transcriptInner.appendChild(row);
        break;
      }
      case "question": {
        const card = buildQuestionCard(msg.id, msg.questions || [], msg.answers || []);
        transcriptInner.appendChild(card);
        break;
      }
      case "tool": {
        const card = el("div", "tool-card" + collapsedInit());
        const head = el("div", "tool-head");
        head.appendChild(toolIcon(msg.toolName));
        head.appendChild(el("span", "tool-name", msg.toolName || "Tool"));
        head.appendChild(el("span", "tool-summary",
          toolHeadLabel(msg.toolName, msg.input)
            || (isMcpTool(msg.toolName) ? "" : (msg.status === "error" ? "error" : "ok"))));
        const status = el("span", "tool-status");
        status.appendChild(msg.status === "error" ? iconCross() : iconCheck());
        head.appendChild(status);
        card.classList.add(msg.status === "error" ? "tool-err" : "tool-ok");
        const bodyWrap = el("div", "tool-body");
        const pre = el("pre"); pre.textContent = prettyJson(msg.input);
        bodyWrap.appendChild(pre);
        card.appendChild(head); card.appendChild(bodyWrap);
        head.addEventListener("click", () => card.classList.toggle("collapsed"));
        transcriptInner.appendChild(card);
        break;
      }
      case "permission": {
        const status = msg.status || "approved";
        const card = el("div", "perm-card decided");
        const sc = permStatusClass(status);
        if (sc) card.classList.add(status, sc);
        const head = el("div", "perm-head");
        head.appendChild(el("span", "perm-chevron", "▸"));
        head.appendChild(el("span", "perm-tool", msg.toolName || "Permission"));
        head.appendChild(el("span", "perm-path", cardHeadDetail(msg)));
        head.appendChild(el("span", "perm-status", permLabel(status)));
        card.appendChild(head);
        const body = el("div", "perm-body");
        if (msg.diff && (msg.diff.oldText != null || msg.diff.newText != null)) {
          body.appendChild(buildDiff(msg.diff.oldText || "", msg.diff.newText || "", msg.diff.startLine || 1));
        } else {
          const j = el("div", "perm-json");
          const pre = el("pre"); pre.textContent = prettyJson(msg.input);
          j.appendChild(pre);
          body.appendChild(j);
        }
        card.appendChild(body);
        head.addEventListener("click", () => card.classList.toggle("expanded"));
        transcriptInner.appendChild(card);
        break;
      }
      case "error": {
        transcriptInner.appendChild(el("div", "error-block", msg.text || "Error"));
        break;
      }
      case "system": {
        transcriptInner.appendChild(el("div", "sys-note", msg.text || ""));
        break;
      }
      default: {
        const { row, body } = makeMsgRow("system", "");
        body.textContent = msg.text || "";
        transcriptInner.appendChild(row);
      }
    }
  }

  // -------------------------------------------------------------------------
  // Markdown renderer — escapes first, then transforms (no raw innerHTML of model text)
  // -------------------------------------------------------------------------
  function escapeHtml(s) {
    return String(s)
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;")
      .replace(/'/g, "&#39;");
  }

  function renderMarkdown(src) {
    const frag = document.createDocumentFragment();
    const text = String(src == null ? "" : src);
    // split out fenced code blocks
    const parts = [];
    const fence = /```([^\n`]*)\n([\s\S]*?)```/g;
    let last = 0, mm;
    while ((mm = fence.exec(text)) !== null) {
      if (mm.index > last) parts.push({ type: "text", value: text.slice(last, mm.index) });
      parts.push({ type: "code", lang: (mm[1] || "").trim(), value: mm[2].replace(/\n$/, "") });
      last = fence.lastIndex;
    }
    if (last < text.length) parts.push({ type: "text", value: text.slice(last) });

    parts.forEach((p) => {
      if (p.type === "code") {
        frag.appendChild(buildCodeBlock(p.lang, p.value));
      } else {
        renderTextBlocks(p.value, frag);
      }
    });
    return frag;
  }

  function renderTextBlocks(text, frag) {
    const lines = text.split("\n");
    let i = 0;
    let para = [];
    const flushPara = () => {
      if (para.length) {
        const p = document.createElement("p");
        p.innerHTML = renderInline(para.join("\n"));
        frag.appendChild(p);
        para = [];
      }
    };
    while (i < lines.length) {
      const line = lines[i];
      // GFM table: a header row (with a pipe) followed by a delimiter row (---|:--:|…)
      if (line.indexOf("|") >= 0 && i + 1 < lines.length && isTableDelimiter(lines[i + 1])) {
        flushPara();
        const headers = splitTableRow(line);
        const aligns = splitTableRow(lines[i + 1]).map(cellAlign);
        i += 2;
        const rows = [];
        while (i < lines.length && lines[i].trim() !== "" && lines[i].indexOf("|") >= 0) {
          rows.push(splitTableRow(lines[i]));
          i++;
        }
        frag.appendChild(buildTable(headers, aligns, rows));
        continue;
      }
      const ulMatch = /^\s*[-*+]\s+(.*)$/.exec(line);
      const olMatch = /^\s*\d+\.\s+(.*)$/.exec(line);
      if (ulMatch || olMatch) {
        flushPara();
        const ordered = !!olMatch;
        const listEl = document.createElement(ordered ? "ol" : "ul");
        while (i < lines.length) {
          const lm = ordered ? /^\s*\d+\.\s+(.*)$/.exec(lines[i]) : /^\s*[-*+]\s+(.*)$/.exec(lines[i]);
          if (!lm) break;
          const li = document.createElement("li");
          li.innerHTML = renderInline(lm[1]);
          listEl.appendChild(li);
          i++;
        }
        frag.appendChild(listEl);
        continue;
      }
      if (line.trim() === "") { flushPara(); i++; continue; }
      para.push(line);
      i++;
    }
    flushPara();
  }

  // GFM table helpers ------------------------------------------------------
  // delimiter row: contains a pipe + only dash/colon/pipe/space cells, e.g. | --- | :-: |
  function isTableDelimiter(line) {
    const t = line.trim();
    if (t.indexOf("|") < 0 || t.indexOf("-") < 0) return false;
    return /^\|?\s*:?-+:?\s*(\|\s*:?-+:?\s*)*\|?$/.test(t);
  }
  function splitTableRow(line) {
    let s = line.trim();
    if (s.startsWith("|")) s = s.slice(1);
    if (s.endsWith("|")) s = s.slice(0, -1);
    return s.split("|").map((c) => c.trim());
  }
  function cellAlign(cell) {
    const c = cell.trim();
    const l = c.startsWith(":"), r = c.endsWith(":");
    return l && r ? "center" : r ? "right" : l ? "left" : "";
  }
  function buildTable(headers, aligns, rows) {
    const wrap = el("div", "md-table-wrap");
    const tbl = document.createElement("table");
    tbl.className = "md-table";
    const thead = document.createElement("thead");
    const htr = document.createElement("tr");
    headers.forEach((h, idx) => {
      const th = document.createElement("th");
      th.innerHTML = renderInline(h);
      if (aligns[idx]) th.style.textAlign = aligns[idx];
      htr.appendChild(th);
    });
    thead.appendChild(htr);
    tbl.appendChild(thead);
    const tbody = document.createElement("tbody");
    rows.forEach((r) => {
      const tr = document.createElement("tr");
      for (let c = 0; c < headers.length; c++) {
        const td = document.createElement("td");
        td.innerHTML = renderInline(r[c] != null ? r[c] : "");
        if (aligns[c]) td.style.textAlign = aligns[c];
        tr.appendChild(td);
      }
      tbody.appendChild(tr);
    });
    tbl.appendChild(tbody);
    wrap.appendChild(tbl);
    return wrap;
  }

  // inline: escape first, then bold/italic/code/links — operate on escaped string
  function renderInline(raw) {
    let s = escapeHtml(raw);
    // inline code (protect content) — capture between backticks
    const codes = [];
    s = s.replace(/`([^`]+)`/g, (m, c) => {
      codes.push(c);
      return "\u0000CODE" + (codes.length - 1) + "\u0000";
    });
    // links [text](url) — url already escaped; strip quotes from href safety
    s = s.replace(/\[([^\]]+)\]\(([^)\s]+)\)/g, (m, t, u) => {
      const href = u.replace(/&quot;/g, "").replace(/&#39;/g, "");
      return '<a href="' + href + '" target="_blank" rel="noopener noreferrer">' + t + "</a>";
    });
    // bold **text**
    s = s.replace(/\*\*([^*]+)\*\*/g, "<strong>$1</strong>");
    // italic *text* or _text_
    s = s.replace(/(^|[^*])\*([^*\n]+)\*/g, "$1<em>$2</em>");
    s = s.replace(/(^|[^_])_([^_\n]+)_/g, "$1<em>$2</em>");
    // restore inline code
    s = s.replace(/\u0000CODE(\d+)\u0000/g, (m, idx) => {
      return '<code class="inline">' + codes[Number(idx)] + "</code>";
    });
    return s;
  }

  function buildCodeBlock(lang, code) {
    const block = el("div", "code-block");
    const head = el("div", "code-head");
    head.appendChild(el("span", "code-lang", lang || "text"));
    const copyBtn = el("button", "code-btn", "Copy");
    copyBtn.addEventListener("click", () => {
      copyText(code);
      copyBtn.textContent = "Copied";
      setTimeout(() => (copyBtn.textContent = "Copy"), 1200);
    });
    const insertBtn = el("button", "code-btn", "Insert into editor");
    insertBtn.addEventListener("click", () => post("editor.insert", { text: code }));
    head.appendChild(copyBtn);
    head.appendChild(insertBtn);
    block.appendChild(head);
    const pre = el("pre");
    pre.innerHTML = highlight(code, lang);   // highlight builds escaped, tokenized HTML
    block.appendChild(pre);
    return block;
  }

  function copyText(text) {
    if (navigator.clipboard && navigator.clipboard.writeText) {
      navigator.clipboard.writeText(text).catch(() => fallbackCopy(text));
    } else {
      fallbackCopy(text);
    }
  }
  function fallbackCopy(text) {
    const ta = document.createElement("textarea");
    ta.value = text;
    ta.style.position = "fixed";
    ta.style.opacity = "0";
    document.body.appendChild(ta);
    ta.select();
    try { document.execCommand("copy"); } catch (e) {}
    document.body.removeChild(ta);
  }

  // -------------------------------------------------------------------------
  // Lightweight syntax highlighter — escapes input, emits token spans
  // Generic for js/ts/csharp/json/bash and similar.
  // -------------------------------------------------------------------------
  const KEYWORDS = {
    js: "var let const function return if else for while do switch case break continue new class extends super this typeof instanceof in of try catch finally throw await async yield import export from default null true false undefined delete void",
    ts: "var let const function return if else for while do switch case break continue new class extends super this typeof instanceof in of try catch finally throw await async yield import export from default null true false undefined interface type enum implements public private protected readonly as namespace",
    csharp: "using namespace class struct interface enum public private protected internal static void var int string bool double float decimal long short byte char object new return if else for foreach while do switch case break continue try catch finally throw async await this base null true false get set partial override virtual abstract sealed readonly const",
    json: "true false null",
    bash: "if then else elif fi for while do done case esac function in return export local echo cd ls cat grep sed awk source exit set unset",
  };

  function highlight(code, lang) {
    const l = (lang || "").toLowerCase();
    let key = "js";
    if (l.indexOf("ts") === 0 || l === "typescript") key = "ts";
    else if (l === "cs" || l === "csharp" || l === "c#") key = "csharp";
    else if (l === "json") key = "json";
    else if (l === "bash" || l === "sh" || l === "shell" || l === "zsh") key = "bash";
    else if (l === "js" || l === "javascript" || l === "jsx" || l === "") key = "js";

    const kw = new Set((KEYWORDS[key] || KEYWORDS.js).split(/\s+/));
    let out = "";
    let i = 0;
    const n = code.length;

    const emit = (cls, text) => {
      const esc = escapeHtml(text);
      out += cls ? '<span class="' + cls + '">' + esc + "</span>" : esc;
    };

    while (i < n) {
      const ch = code[i];
      // line comments // or #
      if (ch === "/" && code[i + 1] === "/") {
        let j = i; while (j < n && code[j] !== "\n") j++;
        emit("tok-com", code.slice(i, j)); i = j; continue;
      }
      if (ch === "#" && key === "bash") {
        let j = i; while (j < n && code[j] !== "\n") j++;
        emit("tok-com", code.slice(i, j)); i = j; continue;
      }
      // block comments /* */
      if (ch === "/" && code[i + 1] === "*") {
        let j = i + 2; while (j < n && !(code[j] === "*" && code[j + 1] === "/")) j++;
        j = Math.min(n, j + 2);
        emit("tok-com", code.slice(i, j)); i = j; continue;
      }
      // strings
      if (ch === '"' || ch === "'" || ch === "`") {
        const q = ch; let j = i + 1;
        while (j < n && code[j] !== q) { if (code[j] === "\\") j++; j++; }
        j = Math.min(n, j + 1);
        emit("tok-str", code.slice(i, j)); i = j; continue;
      }
      // numbers
      if (/[0-9]/.test(ch)) {
        let j = i; while (j < n && /[0-9._xXa-fA-F]/.test(code[j])) j++;
        emit("tok-num", code.slice(i, j)); i = j; continue;
      }
      // identifiers / keywords
      if (/[A-Za-z_$]/.test(ch)) {
        let j = i; while (j < n && /[A-Za-z0-9_$]/.test(code[j])) j++;
        const word = code.slice(i, j);
        if (kw.has(word)) emit("tok-kw", word);
        else if (code[j] === "(") emit("tok-fn", word);
        else emit(null, word);
        i = j; continue;
      }
      // default char
      emit(null, ch); i++;
    }
    return out;
  }

  // -------------------------------------------------------------------------
  // Icons
  // -------------------------------------------------------------------------
  function svg(inner, attrs) {
    const a = Object.assign({ viewBox: "0 0 24 24", width: 14, height: 14, fill: "none", stroke: "currentColor", "stroke-width": 1.8, "stroke-linecap": "round", "stroke-linejoin": "round" }, attrs || {});
    const NS = "http://www.w3.org/2000/svg";
    const s = document.createElementNS(NS, "svg");
    for (const k in a) s.setAttribute(k, a[k]);
    s.innerHTML = inner;
    s.setAttribute("aria-hidden", "true");
    return s;
  }
  function iconCheck() { return svg('<path d="M5 13l4 4L19 7"/>', { class: "status-ok", width: 14, height: 14, "stroke-width": 2.2 }); }
  function iconCross() { return svg('<path d="M6 6l12 12M18 6L6 18"/>', { class: "status-err", width: 14, height: 14, "stroke-width": 2.2 }); }
  function iconTrash() { return svg('<path d="M4 7h16M9 7V5a1 1 0 011-1h4a1 1 0 011 1v2M6 7l1 13a1 1 0 001 1h8a1 1 0 001-1l1-13"/>', { width: 14, height: 14, "stroke-width": 1.8 }); }
  function toolIcon(name) {
    // MCP tools (mcp__server__Tool) get one consistent "plug" icon — the substring heuristic
    // below would otherwise misclassify them by their long names (e.g. "...ManageEditor" → edit).
    if (isMcpTool(name)) {
      const wrapMcp = el("span", "tool-icon");
      wrapMcp.appendChild(svg('<path d="M9 8V4M15 8V4M6 8h12v4a6 6 0 0 1-12 0z"/><path d="M12 18v3"/>', { width: 14, height: 14 }));
      return wrapMcp;
    }
    const n = (name || "").toLowerCase();
    let path = '<path d="M4 6h16M4 12h16M4 18h10"/>'; // default lines
    if (n.indexOf("read") >= 0) path = '<path d="M4 5h12l4 4v10H4z"/><path d="M16 5v4h4"/>';
    else if (n.indexOf("bash") >= 0 || n.indexOf("shell") >= 0) path = '<path d="M5 7l4 4-4 4M12 17h7"/>';
    else if (n.indexOf("grep") >= 0 || n.indexOf("search") >= 0) path = '<circle cx="11" cy="11" r="6"/><path d="M21 21l-4-4"/>';
    else if (n.indexOf("edit") >= 0 || n.indexOf("write") >= 0) path = '<path d="M4 20h4L18 10l-4-4L4 16z"/>';
    const wrap = el("span", "tool-icon");
    wrap.appendChild(svg(path, { width: 14, height: 14 }));
    return wrap;
  }

  function prettyJson(v) {
    if (v == null) return "";
    if (typeof v === "string") return v;
    try { return JSON.stringify(v, null, 2); } catch (e) { return String(v); }
  }

  function cssEscape(s) { return String(s).replace(/["\\]/g, "\\$&"); }

  // -------------------------------------------------------------------------
  // Auto-scroll (§5.2 / §7)
  // -------------------------------------------------------------------------
  function isNearBottom() {
    return transcript.scrollHeight - transcript.scrollTop - transcript.clientHeight < 40;
  }
  function scrollToBottom(force) {
    transcript.scrollTop = transcript.scrollHeight;
    showJumpPill(false);
    // re-pin after layout settles (markdown re-render, code highlight, fonts, table reflow,
    // or the working rocket being added/removed can change height in the same frame)
    requestAnimationFrame(() => {
      transcript.scrollTop = transcript.scrollHeight;
      showJumpPill(false);
    });
  }
  function showJumpPill(show) { jumpPill.hidden = !show; }

  transcript.addEventListener("scroll", () => {
    if (isNearBottom()) showJumpPill(false);
  });
  jumpPill.addEventListener("click", () => scrollToBottom(true));

  // -------------------------------------------------------------------------
  // Composer: send / stop / keyboard
  // -------------------------------------------------------------------------
  function canSend() {
    return input.value.trim().length > 0 &&
      !state.remoteActive &&
      state.status !== "working" &&
      state.status !== "waiting-permission";
  }
  function updateSendEnabled() {
    if (state.status === "working") { sendBtn.disabled = false; return; }
    sendBtn.disabled = !canSend();
  }

  function sendPrompt() {
    if (!canSend()) return;
    const text = input.value;
    const attachments = state.attachments.slice();
    // also surface the auto-attached active editor file (host appends it as an @-reference),
    // so the sent bubble reflects exactly what was sent — matching the host-persisted record
    const display = attachments.slice();
    const af = state.activeFile;
    if (af && af.name && af.optionEnabled && af.enabled) {
      const name = af.name + (af.lines ? ":" + af.lines : "");
      if (!display.some((a) => a.path && af.path && a.path === af.path))
        display.push({ name, path: af.path });
    }
    renderUserMessage(text, display);
    scrollToBottom(); // the user just sent → always pin to bottom (even after a long prompt)
    post("prompt.send", { text, attachments: attachments.length ? attachments : undefined });
    input.value = "";
    state.attachments = [];
    renderAttachments();
    autoGrow();
    updateSendEnabled();
  }

  sendBtn.addEventListener("click", () => {
    if (state.status === "working") { post("turn.stop", {}); return; }
    sendPrompt();
  });

  input.addEventListener("input", () => {
    autoGrow();
    updateSendEnabled();
    maybeOpenSlashAutocomplete();
    maybeOpenAtAutocomplete();
  });

  input.addEventListener("keydown", (e) => {
    if (atAutoOpen && handleAtAutocompleteKey(e)) return;
    if (slashAutocompleteOpen && handleAutocompleteKey(e)) return;
    if (e.key === "Enter" && !e.shiftKey) {
      e.preventDefault();
      sendPrompt();
    }
  });

  // Ctrl+Esc toggles composer focus (global)
  document.addEventListener("keydown", (e) => {
    if (e.ctrlKey && e.key === "Escape") {
      e.preventDefault();
      if (document.activeElement === input) input.blur();
      else input.focus();
    } else if (e.key === "Escape") {
      closeAllOverlays();
    }
  });

  // Ctrl+V of a file/image copied in Windows: the textarea can't carry a path, so
  // let the host read the clipboard and return real paths as attachments. Plain
  // text still pastes natively — the paste event's file items are our only signal
  // that the clipboard actually holds a file/image.
  input.addEventListener("paste", (e) => {
    const dt = e.clipboardData;
    if (!dt) return;
    const hasFile =
      (dt.files && dt.files.length > 0) ||
      Array.prototype.some.call(dt.items || [], (it) => it.kind === "file");
    if (hasFile) {
      e.preventDefault();
      post("clipboard.paste", {});
    }
  });

  input.addEventListener("focus", () => {
    composer.classList.add("focused");
    refreshActiveFile();
  });

  // Returning focus to the chat (e.g. after changing the option in Tools → Options)
  // re-pulls the active-file state, so the chip reflects option + selection changes.
  window.addEventListener("focus", refreshActiveFile);

  function refreshActiveFile() {
    // host re-reads option + current editor selection and re-sends `activeFile`
    post("activeFile.refresh", {});
  }
  input.addEventListener("blur", () => composer.classList.remove("focused"));

  // -------------------------------------------------------------------------
  // Textarea auto-grow (max ~40% of panel height; ResizeObserver-driven)
  // -------------------------------------------------------------------------
  let maxInputHeight = 160;
  function recomputeMaxInputHeight() {
    const panelH = root.clientHeight || window.innerHeight;
    maxInputHeight = Math.max(40, Math.round(panelH * 0.4));
    input.style.maxHeight = maxInputHeight + "px";
    autoGrow();
  }
  // host→web composer.append: append text (e.g. an editor selection added via the right-click
  // menu) to the composer, on its own line, then focus the composer with the caret at the end.
  function appendToComposer(text) {
    if (!text) return;
    const cur = input.value;
    const sep = cur.length && !cur.endsWith("\n") ? "\n" : "";
    input.value = cur + sep + text;
    autoGrow();
    updateSendEnabled();
    input.focus();
    input.selectionStart = input.selectionEnd = input.value.length;
  }

  function autoGrow() {
    input.style.height = "auto";
    const h = Math.min(input.scrollHeight, maxInputHeight);
    input.style.height = h + "px";
    input.style.overflowY = input.scrollHeight > maxInputHeight ? "auto" : "hidden";
  }

  // -------------------------------------------------------------------------
  // Attachments
  // -------------------------------------------------------------------------
  function addAttachment(name, path) {
    state.attachments.push({
      id: "att-" + Date.now() + "-" + Math.random().toString(36).slice(2, 6),
      name,
      path: path || undefined,
    });
    renderAttachments();
  }
  function removeAttachment(id) {
    state.attachments = state.attachments.filter((a) => a.id !== id);
    renderAttachments();
  }
  function renderAttachments() {
    attachmentsEl.innerHTML = "";
    if (!state.attachments.length) { attachmentsEl.hidden = true; return; }
    attachmentsEl.hidden = false;
    state.attachments.forEach((a) => {
      const chip = el("div", "chip");
      chip.appendChild(el("span", "chip-name", a.name));
      const x = el("button", "chip-x");
      x.appendChild(svg('<path d="M6 6l12 12M18 6L6 18"/>', { width: 12, height: 12, "stroke-width": 2 }));
      x.addEventListener("click", () => removeAttachment(a.id));
      chip.appendChild(x);
      attachmentsEl.appendChild(chip);
    });
  }

  // -------------------------------------------------------------------------
  // Overlays / popovers — anchored, clamped, close on outside-click & Esc
  // -------------------------------------------------------------------------
  let openOverlay = null; // { el, anchor, reposition }

  function closeAllOverlays() {
    if (openOverlay) {
      openOverlay.el.remove();
      if (openOverlay.anchor) openOverlay.anchor.setAttribute("aria-expanded", "false");
      openOverlay = null;
    }
    closeSlashAutocomplete();
    closeAtAutocomplete();
    closeModal();
  }

  function openPopover(anchor, contentEl, opts) {
    closeAllOverlays();
    opts = opts || {};
    overlayLayer.appendChild(contentEl);
    // optionally match another element's width (e.g. size the popover to the card)
    if (opts.matchWidthEl) contentEl.style.width = opts.matchWidthEl.getBoundingClientRect().width + "px";
    if (anchor) anchor.setAttribute("aria-expanded", "true");
    const reposition = () => positionPopover(anchor, contentEl, opts.align || "start", opts.side || "bottom", opts.hAnchorEl);
    reposition();
    openOverlay = { el: contentEl, anchor, reposition };
  }

  function positionPopover(anchor, popEl, align, side, hAnchor) {
    const margin = 6;
    const ar = anchor.getBoundingClientRect();
    const hr = (hAnchor || anchor).getBoundingClientRect(); // horizontal anchor (may differ, e.g. the card)
    const pr = popEl.getBoundingClientRect();
    const vw = window.innerWidth, vh = window.innerHeight;
    let top, left;
    // vertical: prefer below, flip above if not enough room
    if (side === "top") {
      top = ar.top - pr.height - margin;
      if (top < margin) top = ar.bottom + margin;
    } else {
      top = ar.bottom + margin;
      if (top + pr.height > vh - margin) {
        const above = ar.top - pr.height - margin;
        if (above >= margin) top = above;
        else top = Math.max(margin, vh - pr.height - margin);
      }
    }
    // horizontal alignment (relative to the horizontal anchor)
    if (align === "end") left = hr.right - pr.width;
    else left = hr.left;
    // clamp/shift
    if (left + pr.width > vw - margin) left = vw - pr.width - margin;
    if (left < margin) left = margin;
    popEl.style.top = Math.max(margin, top) + "px";
    popEl.style.left = left + "px";
  }

  // outside click
  document.addEventListener("mousedown", (e) => {
    if (openOverlay && !openOverlay.el.contains(e.target) &&
        (!openOverlay.anchor || !openOverlay.anchor.contains(e.target))) {
      closeAllOverlays();
    }
    if (slashAutocompleteOpen && slashAutoEl && !slashAutoEl.contains(e.target) && e.target !== input) {
      closeSlashAutocomplete();
    }
    if (atAutoOpen && atAutoEl && !atAutoEl.contains(e.target) && e.target !== input) {
      closeAtAutocomplete();
    }
  });

  // -------------------------------------------------------------------------
  // Selection context menu — right-click on a text selection → Copy.
  // (The WebView2 default context menu is disabled host-side, so we offer our own.)
  // -------------------------------------------------------------------------
  document.addEventListener("contextmenu", (e) => {
    const selected = (window.getSelection && window.getSelection().toString()) || "";
    if (!selected.trim()) return; // nothing selected → leave the (suppressed) default
    e.preventDefault();
    openContextMenu(e.clientX, e.clientY, [["Copy", () => copyText(selected)]]);
  });

  function openContextMenu(x, y, items) {
    closeAllOverlays();
    const pop = el("div", "popover context-menu");
    items.forEach(([label, fn]) => {
      const b = el("button", "menu-item", label);
      b.addEventListener("click", () => { closeAllOverlays(); fn(); });
      pop.appendChild(b);
    });
    overlayLayer.appendChild(pop);
    const margin = 6, r = pop.getBoundingClientRect();
    let left = x, top = y;
    if (left + r.width > window.innerWidth - margin) left = window.innerWidth - r.width - margin;
    if (top + r.height > window.innerHeight - margin) top = window.innerHeight - r.height - margin;
    pop.style.left = Math.max(margin, left) + "px";
    pop.style.top = Math.max(margin, top) + "px";
    openOverlay = { el: pop, anchor: null, reposition: function () {} };
  }

  function copyText(text) {
    if (!text) return;
    const fallback = () => { try { document.execCommand("copy"); } catch (_) {} };
    try {
      if (navigator.clipboard && navigator.clipboard.writeText) {
        navigator.clipboard.writeText(text).catch(fallback);
      } else {
        fallback();
      }
    } catch (_) {
      fallback();
    }
  }

  // -------------------------------------------------------------------------
  // Rename modal (session.rename, §5.1)
  // -------------------------------------------------------------------------
  let openModalEl = null;

  function closeModal() {
    if (openModalEl) { openModalEl.remove(); openModalEl = null; }
  }

  function openRenameModal() {
    closeAllOverlays();
    const backdrop = el("div", "modal-backdrop");
    const modal = el("div", "modal");
    modal.setAttribute("role", "dialog");
    modal.setAttribute("aria-label", "Rename session");
    modal.appendChild(el("div", "modal-title", "Rename session"));

    const field = document.createElement("input");
    field.type = "text";
    field.className = "modal-input";
    field.value = state.title === "Untitled" ? "" : state.title;
    field.placeholder = "Session title";
    field.maxLength = 120;
    modal.appendChild(field);

    const actions = el("div", "modal-actions");
    const cancel = el("button", "modal-btn", "Cancel");
    const save = el("button", "modal-btn primary", "Save");
    cancel.addEventListener("click", closeModal);
    save.addEventListener("click", saveRename);
    actions.appendChild(cancel);
    actions.appendChild(save);
    modal.appendChild(actions);

    backdrop.appendChild(modal);
    backdrop.addEventListener("mousedown", (e) => { if (e.target === backdrop) closeModal(); });
    field.addEventListener("keydown", (e) => {
      if (e.key === "Enter") { e.preventDefault(); saveRename(); }
      else if (e.key === "Escape") { e.preventDefault(); closeModal(); }
    });

    function saveRename() {
      const title = field.value.trim();
      if (title && title !== state.title) {
        state.title = title; // optimistic — host persists and echoes via session.init/list
        updateTitle();
        post("session.rename", { sessionId: state.sessionId, title });
      }
      closeModal();
    }

    overlayLayer.appendChild(backdrop);
    openModalEl = backdrop;
    field.focus();
    field.select();
  }

  $("btn-rename").addEventListener("click", openRenameModal);

  /** Generic confirmation modal (reused for destructive actions like deleting a session). */
  function openConfirmModal(opts) {
    closeAllOverlays();
    const backdrop = el("div", "modal-backdrop");
    const modal = el("div", "modal");
    modal.setAttribute("role", "dialog");
    modal.setAttribute("aria-label", opts.title);
    modal.appendChild(el("div", "modal-title", opts.title));
    modal.appendChild(el("div", "modal-body", opts.message));
    const actions = el("div", "modal-actions");
    const cancel = el("button", "modal-btn", "Cancel");
    const confirm = el("button", "modal-btn " + (opts.danger ? "danger" : "primary"), opts.confirmLabel || "OK");
    cancel.addEventListener("click", closeModal);
    confirm.addEventListener("click", () => { closeModal(); if (opts.onConfirm) opts.onConfirm(); });
    actions.appendChild(cancel);
    actions.appendChild(confirm);
    modal.appendChild(actions);
    backdrop.appendChild(modal);
    backdrop.addEventListener("mousedown", (e) => { if (e.target === backdrop) closeModal(); });
    modal.addEventListener("keydown", (e) => { if (e.key === "Escape") { e.preventDefault(); closeModal(); } });
    overlayLayer.appendChild(backdrop);
    openModalEl = backdrop;
    confirm.focus(); // Enter activates the focused confirm button
  }

  /** Trash button in the history list → confirm, then ask the host to delete the session. */
  function confirmDeleteSession(s) {
    const name = (s.title || "Untitled");
    openConfirmModal({
      title: "Delete session",
      message: "Delete “" + name + "”? This permanently removes it from the chat history.",
      confirmLabel: "Delete",
      danger: true,
      onConfirm: () => post("session.delete", { sessionId: s.id }),
    });
  }

  // -------------------------------------------------------------------------
  // Remote control (claude remote-control server) — locks composer + transcript,
  // shows link + QR, "End remote session" loads the remote session afterwards.
  // -------------------------------------------------------------------------
  let remoteQrUrl = null; // last URL rendered into the canvas

  /** Locks everything except the remote panel: header actions (rename, history,
      new chat) and the composer controls (+, /, Model·Mode, attachment chips). */
  function setRemoteLocked(locked) {
    [$("btn-rename"), $("btn-history"), $("btn-new"), $("btn-plus"), $("btn-slash"), modelModeBtn, activeFileChip]
      .forEach((b) => { if (b) b.disabled = locked; });
    input.disabled = locked;
    root.classList.toggle("remote-locked", locked);
  }

  function applyRemoteState(m) {
    const s = m.state || "";
    if (s === "stopped") {
      state.remoteActive = false;
      state.remoteUrl = null;
      remoteQrUrl = null;
      remotePanel.hidden = true;
      btnRemote.classList.remove("active");
      setRemoteLocked(false);
      btnRemoteEnd.disabled = false;
      applyStatus("ready");
      return;
    }

    state.remoteActive = true;
    remotePanel.hidden = false;
    btnRemote.classList.add("active");
    setRemoteLocked(true);
    btnRemoteEnd.disabled = false;
    closeAllOverlays();
    applyStatus("remote", "Remote control active");
    remoteStatus.classList.toggle("error", s === "error");
    btnRemoteEnd.textContent = s === "error" ? "Close" : "End remote session";

    if (s === "error" || s === "starting") {
      remoteStatus.textContent = s === "error"
        ? (m.message || "Remote control failed.")
        : "Starting remote control…";
      remoteQr.hidden = true;
      remoteUrlEl.hidden = true;
      btnRemoteCopy.hidden = true;
      return;
    }

    // ready
    const n = m.activeSessions || 0;
    remoteStatus.textContent = n > 0
      ? n + " active remote session" + (n > 1 ? "s" : "")
      : "Scan with the Claude app or open the link to connect";
    if (m.url) {
      state.remoteUrl = m.url;
      remoteUrlEl.textContent = m.url;
      remoteUrlEl.hidden = false;
      btnRemoteCopy.hidden = false;
      drawRemoteQr(m.url);
    }
  }

  /** 1px/module into the canvas (CSS scales it up with image-rendering: pixelated). */
  function drawRemoteQr(url) {
    if (remoteQrUrl === url) { remoteQr.hidden = false; return; }
    const modules = typeof qrEncode === "function" ? qrEncode(url) : null;
    if (!modules) { remoteQr.hidden = true; return; } // URL too long / qr.js missing
    const quiet = 4;
    const dim = modules.length + quiet * 2;
    remoteQr.width = dim;
    remoteQr.height = dim;
    const ctx = remoteQr.getContext("2d");
    ctx.fillStyle = "#fff";
    ctx.fillRect(0, 0, dim, dim);
    ctx.fillStyle = "#000";
    for (let y = 0; y < modules.length; y++) {
      for (let x = 0; x < modules.length; x++) {
        if (modules[y][x]) ctx.fillRect(x + quiet, y + quiet, 1, 1);
      }
    }
    remoteQr.hidden = false;
    remoteQrUrl = url;
  }

  btnRemote.addEventListener("click", () => {
    if (state.remoteActive || state.status === "working") return;
    post("remote.start", {});
    applyRemoteState({ state: "starting" }); // optimistic — host confirms
  });

  btnRemoteEnd.addEventListener("click", () => {
    btnRemoteEnd.disabled = true;
    remoteStatus.classList.remove("error");
    remoteStatus.textContent = "Stopping…";
    post("remote.stop", {});
  });

  btnRemoteCopy.addEventListener("click", () => {
    if (!state.remoteUrl) return;
    copyText(state.remoteUrl);
    btnRemoteCopy.textContent = "Copied!";
    setTimeout(() => { btnRemoteCopy.textContent = "Copy link"; }, 1500);
  });

  // -------------------------------------------------------------------------
  // Header buttons
  // -------------------------------------------------------------------------
  $("btn-history").addEventListener("click", function () {
    const pop = el("div", "popover history-panel");
    pop.appendChild(el("div", "history-empty", "Loading…"));
    openPopover(this, pop, { align: "end" });
    historyPanelEl = pop;
    post("session.listRequest", {});
  });
  let historyPanelEl = null;

  function renderHistoryList(sessions) {
    if (!historyPanelEl || !openOverlay || openOverlay.el !== historyPanelEl) return;
    historyPanelEl.innerHTML = "";
    if (!sessions.length) {
      historyPanelEl.appendChild(el("div", "history-empty", "No sessions yet."));
      return;
    }
    sessions.forEach((s) => {
      // A div (not a button) so the delete button can nest inside without invalid markup.
      const item = el("div", "history-item" + (s.id === state.sessionId ? " active" : ""));
      const top = el("div", "hi-top");
      top.appendChild(el("span", "hi-title", s.title || "Untitled"));
      top.appendChild(el("span", "hi-time", relativeTime(s.updatedAt)));
      const del = el("button", "hi-delete");
      del.title = "Delete session";
      del.setAttribute("aria-label", "Delete session");
      del.appendChild(iconTrash());
      del.addEventListener("click", (e) => { e.stopPropagation(); confirmDeleteSession(s); });
      top.appendChild(del);
      item.appendChild(top);
      if (s.preview) item.appendChild(el("div", "hi-preview", s.preview));
      item.addEventListener("click", () => {
        post("session.load", { sessionId: s.id });
        closeAllOverlays();
      });
      historyPanelEl.appendChild(item);
    });
    if (openOverlay) openOverlay.reposition();
  }

  $("btn-new").addEventListener("click", () => {
    // optimistic empty state
    state.messages = [];
    state.title = "Untitled";
    state.activeAssistant = null;
    transcriptInner.innerHTML = "";
    updateTitle();
    renderEmptyOrSignin();
    applyStatus("ready");
    post("session.new", {});
  });

  // Accent-color picker: preset swatches + a native custom color input. Choosing one applies
  // it instantly (applyAccent) and persists it host-side (accent.set). "Default" clears it.
  function buildAccentPicker() {
    const PRESETS = [
      ["", "Default"],
      ["#8d5fc7", "Purple"],
      ["#4f8cf0", "Blue"],
      ["#2bb3a3", "Teal"],
      ["#5aa469", "Green"],
      ["#d9a23f", "Amber"],
      ["#d97757", "Clay"],
      ["#c45fa0", "Magenta"],
    ];
    const wrap = el("div", "accent-swatches");
    let colorInput = null;
    function refreshActive() {
      wrap.querySelectorAll(".accent-swatch").forEach((s) =>
        s.classList.toggle("active", (s.dataset.color || "") === (state.accent || "")));
    }
    function selectAccent(hex) {
      applyAccent(hex);
      post("accent.set", { color: state.accent });
      refreshActive();
      if (colorInput && state.accent) colorInput.value = state.accent;
    }
    PRESETS.forEach(([hex, label]) => {
      const sw = el("button", "accent-swatch" + (hex === "" ? " accent-default" : ""));
      sw.dataset.color = hex;
      sw.title = label;
      sw.setAttribute("aria-label", label);
      if (hex) sw.style.background = hex;
      sw.addEventListener("click", () => selectAccent(hex));
      wrap.appendChild(sw);
    });
    // custom color — native picker behind a "+" tile
    const custom = el("label", "accent-swatch accent-custom");
    custom.title = "Custom…";
    colorInput = document.createElement("input");
    colorInput.type = "color";
    colorInput.value = state.accent || "#8d5fc7";
    colorInput.addEventListener("input", () => selectAccent(colorInput.value));
    custom.appendChild(colorInput);
    custom.appendChild(el("span", "accent-custom-glyph", "+"));
    wrap.appendChild(custom);
    refreshActive();
    return wrap;
  }

  $("btn-settings").addEventListener("click", function () {
    const pop = el("div", "popover appearance-pop");
    pop.appendChild(el("div", "appearance-title", "Appearance"));
    const seg = el("div", "segmented");
    [["dark", "Dark"], ["light", "Light"], ["auto", "Auto"]].forEach(([mode, label]) => {
      const b = el("button", "seg-btn" + (state.themeMode === mode ? " active" : ""), label);
      b.addEventListener("click", () => {
        state.themeMode = mode;
        seg.querySelectorAll(".seg-btn").forEach((x) => x.classList.remove("active"));
        b.classList.add("active");
        post("theme.setMode", { mode });
      });
      seg.appendChild(b);
    });
    pop.appendChild(seg);

    // Verbosity — controls how much of each turn the transcript shows
    pop.appendChild(el("div", "appearance-title appearance-title-2", "Verbosity"));
    const segV = el("div", "segmented");
    const VERBOSITY_HINTS = {
      compact: "Hides system notes and thinking; tool calls collapsed.",
      normal: "Shows everything; thinking and tool cards collapsed (click to expand).",
      detailed: "Shows everything with thinking and tool cards (input/output) expanded.",
    };
    const vHint = el("div", "mm-hint", VERBOSITY_HINTS[state.verbosity] || VERBOSITY_HINTS.normal);
    [["compact", "Compact"], ["normal", "Normal"], ["detailed", "Detailed"]].forEach(([level, label]) => {
      const b = el("button", "seg-btn" + (state.verbosity === level ? " active" : ""), label);
      b.addEventListener("click", () => {
        state.verbosity = level;
        segV.querySelectorAll(".seg-btn").forEach((x) => x.classList.remove("active"));
        b.classList.add("active");
        vHint.textContent = VERBOSITY_HINTS[level] || VERBOSITY_HINTS.normal;
        applyVerbosity();
        post("verbosity.set", { level });
      });
      segV.appendChild(b);
    });
    pop.appendChild(segV);
    pop.appendChild(vHint);

    // Accent color — custom brand color (persisted host-side, applied as :root overrides)
    pop.appendChild(el("div", "appearance-title appearance-title-2", "Accent color"));
    pop.appendChild(buildAccentPicker());

    // Footer: opens the host-side settings window (all options) via options.open
    pop.appendChild(el("div", "appearance-divider"));
    const optBtn = el("button", "menu-item appearance-options-link", "Advanced options…");
    optBtn.addEventListener("click", () => { closeAllOverlays(); post("options.open", {}); });
    pop.appendChild(optBtn);

    // Changelog — opens the GitHub CHANGELOG in the system browser (target=_blank → host opens it)
    const CHANGELOG_URL = "https://github.com/finex7070/CodeAstrogator/blob/main/CHANGELOG.md";
    const clBtn = el("button", "menu-item appearance-options-link", "Changelog…");
    clBtn.addEventListener("click", () => { closeAllOverlays(); window.open(CHANGELOG_URL, "_blank", "noopener"); });
    pop.appendChild(clBtn);

    openPopover(this, pop, { align: "end" });
  });

  // -------------------------------------------------------------------------
  // + menu
  // -------------------------------------------------------------------------
  $("btn-plus").addEventListener("click", function () {
    const pop = el("div", "popover");
    const items = [
      ["Add file…", () => post("attach.files", {})],          // host opens a file picker
      ["Add context…", () => insertAtCaret("@")],             // opens the @-file autocomplete
      ["Browse the web", () => insertAtCaret("@browser:")],
    ];
    items.forEach(([label, fn]) => {
      const b = el("button", "menu-item", label);
      b.addEventListener("click", () => { closeAllOverlays(); fn(); });
      pop.appendChild(b);
    });
    openPopover(this, pop, { align: "start", side: "top" });
  });

  // -------------------------------------------------------------------------
  // / menu (toolbar button)
  // -------------------------------------------------------------------------
  $("btn-slash").addEventListener("click", function () {
    const pop = el("div", "popover");
    SLASH_COMMANDS.forEach((c) => {
      const b = el("button", "menu-item");
      b.appendChild(el("span", null, c.command));
      if (c.desc) b.appendChild(el("span", "mi-sub", c.desc));
      b.addEventListener("click", () => { runSlash(c.command); closeAllOverlays(); });
      pop.appendChild(b);
    });
    openPopover(this, pop, { align: "start", side: "top" });
  });

  function runSlash(command) {
    post("slash.run", { command });
    if (input.value.trim().startsWith("/")) { input.value = ""; autoGrow(); updateSendEnabled(); }
  }

  // -------------------------------------------------------------------------
  // Slash autocomplete in textarea
  // -------------------------------------------------------------------------
  let slashAutocompleteOpen = false;
  let slashAutoEl = null;
  let slashAutoItems = [];
  let slashAutoIndex = 0;

  function maybeOpenSlashAutocomplete() {
    const v = input.value;
    if (v.length > 0 && v[0] === "/" && v.indexOf("\n") === -1) {
      const filter = v.toLowerCase();
      const matches = SLASH_COMMANDS.filter((c) => c.command.toLowerCase().startsWith(filter));
      if (matches.length) { openSlashAutocomplete(matches); return; }
    }
    closeSlashAutocomplete();
  }

  function openSlashAutocomplete(matches) {
    slashAutoItems = matches;
    slashAutoIndex = 0;
    if (!slashAutoEl) {
      slashAutoEl = el("div", "popover autocomplete");
      overlayLayer.appendChild(slashAutoEl);
    }
    slashAutoEl.innerHTML = "";
    matches.forEach((c, idx) => {
      const b = el("button", "menu-item" + (idx === 0 ? " active" : ""));
      b.appendChild(el("span", null, c.command));
      if (c.desc) b.appendChild(el("span", "mi-sub", c.desc));
      b.addEventListener("mousedown", (e) => { e.preventDefault(); selectAutocomplete(idx); });
      slashAutoEl.appendChild(b);
    });
    slashAutocompleteOpen = true;
    positionPopover($("btn-slash"), slashAutoEl, "start", "top");
  }

  function closeSlashAutocomplete() {
    if (slashAutoEl) { slashAutoEl.remove(); slashAutoEl = null; }
    slashAutocompleteOpen = false;
    slashAutoItems = [];
  }

  function selectAutocomplete(idx) {
    const c = slashAutoItems[idx];
    if (!c) return;
    closeSlashAutocomplete();
    runSlash(c.command);
  }

  function handleAutocompleteKey(e) {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      slashAutoIndex = (slashAutoIndex + 1) % slashAutoItems.length;
      refreshAutoActive(); return true;
    }
    if (e.key === "ArrowUp") {
      e.preventDefault();
      slashAutoIndex = (slashAutoIndex - 1 + slashAutoItems.length) % slashAutoItems.length;
      refreshAutoActive(); return true;
    }
    if (e.key === "Enter") {
      e.preventDefault();
      selectAutocomplete(slashAutoIndex); return true;
    }
    if (e.key === "Escape") {
      e.preventDefault();
      closeSlashAutocomplete(); return true;
    }
    return false;
  }
  function refreshAutoActive() {
    if (!slashAutoEl) return;
    const items = slashAutoEl.querySelectorAll(".menu-item");
    items.forEach((it, i) => it.classList.toggle("active", i === slashAutoIndex));
  }

  // -------------------------------------------------------------------------
  // @-mention file autocomplete ("Add context")
  // -------------------------------------------------------------------------
  let atAutoOpen = false;
  let atAutoEl = null;
  let atAutoItems = [];
  let atAutoIndex = 0;
  let atFragmentStart = -1; // index of "@" in input.value for the active fragment

  /** Inserts text at the caret and re-runs the input pipeline (focus + autocomplete). */
  function insertAtCaret(text) {
    const start = input.selectionStart != null ? input.selectionStart : input.value.length;
    const end = input.selectionEnd != null ? input.selectionEnd : start;
    const before = input.value.slice(0, start);
    // "@" needs a word boundary in front to register as a mention
    if (text[0] === "@" && before.length > 0 && !/\s$/.test(before)) text = " " + text;
    input.value = before + text + input.value.slice(end);
    const caret = before.length + text.length;
    input.focus();
    input.setSelectionRange(caret, caret);
    input.dispatchEvent(new Event("input"));
  }

  function maybeOpenAtAutocomplete() {
    const caret = input.selectionStart != null ? input.selectionStart : input.value.length;
    const upToCaret = input.value.slice(0, caret);
    const match = /(^|\s)@([^\s@]*)$/.exec(upToCaret);
    if (!match || match[2].startsWith("browser:")) {
      closeAtAutocomplete();
      return;
    }
    atFragmentStart = caret - match[2].length - 1;

    if (state.workspaceFiles === null) {
      if (!state.filesRequested) {
        state.filesRequested = true;
        post("files.listRequest", {});
      }
      return; // opens once files.list arrives
    }

    const query = match[2].toLowerCase();
    const matches = state.workspaceFiles
      .filter((f) => f.path.toLowerCase().indexOf(query) !== -1)
      .slice(0, 12);
    if (!matches.length) { closeAtAutocomplete(); return; }
    openAtAutocomplete(matches);
  }

  function openAtAutocomplete(matches) {
    atAutoItems = matches;
    atAutoIndex = 0;
    if (!atAutoEl) {
      atAutoEl = el("div", "popover autocomplete file-autocomplete");
      overlayLayer.appendChild(atAutoEl);
    }
    atAutoEl.innerHTML = "";
    matches.forEach((f, idx) => {
      const slash = f.path.lastIndexOf("/");
      const name = slash === -1 ? f.path : f.path.slice(slash + 1);
      const dir = slash === -1 ? "" : f.path.slice(0, slash);
      const b = el("button", "menu-item" + (idx === 0 ? " active" : ""));
      b.appendChild(f.isDir ? iconFolder() : iconFile());
      b.appendChild(el("span", "fa-name", name + (f.isDir ? "/" : "")));
      b.appendChild(el("span", "mi-sub", dir));
      b.addEventListener("mousedown", (e) => { e.preventDefault(); selectAtAutocomplete(idx); });
      atAutoEl.appendChild(b);
    });
    atAutoOpen = true;
    positionPopover(composer, atAutoEl, "start", "top");
  }

  function closeAtAutocomplete() {
    if (atAutoEl) { atAutoEl.remove(); atAutoEl = null; }
    atAutoOpen = false;
    atAutoItems = [];
    atFragmentStart = -1;
  }

  function selectAtAutocomplete(idx) {
    const f = atAutoItems[idx];
    if (!f || atFragmentStart < 0) return;
    const caret = input.selectionStart != null ? input.selectionStart : input.value.length;
    const insertion = "@" + f.path + (f.isDir ? "/" : " ");
    input.value = input.value.slice(0, atFragmentStart) + insertion + input.value.slice(caret);
    const newCaret = atFragmentStart + insertion.length;
    input.focus();
    input.setSelectionRange(newCaret, newCaret);
    closeAtAutocomplete();
    input.dispatchEvent(new Event("input")); // folders re-open the list for drilling down
  }

  function handleAtAutocompleteKey(e) {
    if (e.key === "ArrowDown") {
      e.preventDefault();
      atAutoIndex = (atAutoIndex + 1) % atAutoItems.length;
      refreshAtAutoActive(); return true;
    }
    if (e.key === "ArrowUp") {
      e.preventDefault();
      atAutoIndex = (atAutoIndex - 1 + atAutoItems.length) % atAutoItems.length;
      refreshAtAutoActive(); return true;
    }
    if (e.key === "Enter" || e.key === "Tab") {
      e.preventDefault();
      selectAtAutocomplete(atAutoIndex); return true;
    }
    if (e.key === "Escape") {
      e.preventDefault();
      closeAtAutocomplete(); return true;
    }
    return false;
  }
  function refreshAtAutoActive() {
    if (!atAutoEl) return;
    const items = atAutoEl.querySelectorAll(".menu-item");
    items.forEach((it, i) => it.classList.toggle("active", i === atAutoIndex));
  }

  function iconFile() {
    return svg('<path d="M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8z"/><path d="M14 2v6h6"/>', { width: 14, height: 14, "stroke-width": 1.6 });
  }
  function iconFolder() {
    return svg('<path d="M22 19a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V5a2 2 0 0 1 2-2h5l2 3h9a2 2 0 0 1 2 2z"/>', { width: 14, height: 14, "stroke-width": 1.6 });
  }

  // -------------------------------------------------------------------------
  // Model·Mode popover
  // -------------------------------------------------------------------------
  modelModeBtn.addEventListener("click", function () {
    const pop = el("div", "popover mm-pop");

    // Model
    const s1 = el("div", "mm-section");
    s1.appendChild(el("div", "mm-section-title", "Model"));
    MODELS.forEach((md) => {
      const row = el("div", "radio-row" + (state.model === md.id ? " selected" : ""));
      row.appendChild(el("span", "radio-dot"));
      row.appendChild(el("span", null, md.label));
      row.addEventListener("click", () => {
        state.model = md.id;
        s1.querySelectorAll(".radio-row").forEach((r) => r.classList.remove("selected"));
        row.classList.add("selected");
        updateModelModeLabel();
        post("model.set", { model: md.id });
      });
      s1.appendChild(row);
    });
    pop.appendChild(s1);

    // Effort — the CLI's --effort levels (claude 2.1.x)
    const s2 = el("div", "mm-section");
    s2.appendChild(el("div", "mm-section-title", "Effort / Thinking"));
    const segE = el("div", "seg-effort segmented");
    const EFFORT_LEVELS = [
      ["low", "Low"],
      ["medium", "Med"],
      ["high", "High"],
      ["xhigh", "XHigh"],
      ["max", "Max"],
    ];
    EFFORT_LEVELS.forEach(([lvl, label]) => {
      const b = el("button", "seg-btn" + (state.effort === lvl ? " active" : ""), label);
      b.title = lvl;
      b.addEventListener("click", () => {
        state.effort = lvl;
        segE.querySelectorAll(".seg-btn").forEach((x) => x.classList.remove("active"));
        b.classList.add("active");
        post("effort.set", { effort: lvl });
      });
      segE.appendChild(b);
    });
    s2.appendChild(segE);
    pop.appendChild(s2);

    // NOTE: no separate Plan-Mode toggle — "Plan" in the Permission radio below is
    // the same thing (--permission-mode plan); one source of truth.

    // Ultracode — injects the "ultracode" keyword into each prompt (multi-agent
    // orchestration in the CLI). Not an effort level, hence its own toggle.
    const s3 = el("div", "mm-section");
    s3.appendChild(el("div", "mm-section-title", "Ultracode"));
    const ultraRow = el("div", "toggle-row" + (state.ultracode ? " on" : ""));
    ultraRow.appendChild(el("span", "label", "Ultracode"));
    ultraRow.appendChild(el("span", "toggle-switch"));
    ultraRow.addEventListener("click", () => {
      state.ultracode = !state.ultracode;
      ultraRow.classList.toggle("on", state.ultracode);
      updateModelModeLabel();
      post("ultracode.set", { enabled: state.ultracode });
    });
    s3.appendChild(ultraRow);
    s3.appendChild(el("div", "mm-hint", "Multi-agent orchestration — spawns subagent workflows, uses far more tokens."));
    pop.appendChild(s3);

    // Permission radio — each mode's sub-toggle (if any) is nested directly beneath its radio
    // and shown only while that mode is selected.
    const s4 = el("div", "mm-section");
    s4.appendChild(el("div", "mm-section-title", "Permission"));

    // "Auto-accept commands" (under Auto-accept edits) — also run Bash/PowerShell/MCP without a
    // prompt (questions still ask). "Review edits in the editor" (under Ask) — file edits open
    // inline in the code editor with per-hunk Accept/Reject instead of the chat diff card.
    function buildSubToggle(stateKey, msgType, label, hint) {
      const wrap = el("div", "mm-subtoggle");
      const row = el("div", "toggle-row" + (state[stateKey] ? " on" : ""));
      row.appendChild(el("span", "label", label));
      row.appendChild(el("span", "toggle-switch"));
      row.addEventListener("click", () => {
        state[stateKey] = !state[stateKey];
        row.classList.toggle("on", state[stateKey]);
        post(msgType, { enabled: state[stateKey] });
      });
      wrap.appendChild(row);
      wrap.appendChild(el("div", "mm-hint", hint));
      return wrap;
    }
    // mode → its nested sub-toggle element (modes without one stay bare)
    const subToggles = {
      acceptEdits: buildSubToggle("autoAcceptCommands", "autoAcceptCommands.set", "Auto-accept commands",
        "In Auto-accept edits, also run Bash, PowerShell and MCP tools without asking. Questions still prompt."),
      ask: buildSubToggle("reviewEditsInEditor", "reviewEditsInEditor.set", "Review edits in the editor",
        "Open file edits in the code editor with an inline red/green diff and per-hunk Accept/Reject."),
    };

    const perms = [
      ["ask", "Ask before edits"],
      ["acceptEdits", "Auto-accept edits"],
      ["plan", "Plan"],
      ["bypass", "Bypass"],
    ];
    perms.forEach(([mode, label]) => {
      const row = el("div", "radio-row" + (state.permissionMode === mode ? " selected" : ""));
      row.appendChild(el("span", "radio-dot"));
      row.appendChild(el("span", null, label));
      row.addEventListener("click", () => {
        state.permissionMode = mode;
        s4.querySelectorAll(".radio-row").forEach((r) => r.classList.remove("selected"));
        row.classList.add("selected");
        updateModelModeLabel();
        post("permission.set", { mode });
        syncSubToggles();
      });
      s4.appendChild(row);
      if (subToggles[mode]) s4.appendChild(subToggles[mode]); // nested directly under its mode
    });

    // show only the selected mode's sub-toggle
    function syncSubToggles() {
      for (const mode in subToggles)
        subToggles[mode].style.display = state.permissionMode === mode ? "" : "none";
    }
    syncSubToggles();

    pop.appendChild(s4);

    openPopover(this, pop, { align: "end", side: "top" });
  });

  // -------------------------------------------------------------------------
  // Relative time
  // -------------------------------------------------------------------------
  function relativeTime(iso) {
    if (!iso) return "";
    const then = new Date(iso).getTime();
    if (isNaN(then)) return "";
    const diff = Date.now() - then;
    const min = Math.floor(diff / 60000);
    if (min < 1) return "just now";
    if (min < 60) return min + "m ago";
    const hr = Math.floor(min / 60);
    if (hr < 24) return hr + "h ago";
    const d = Math.floor(hr / 24);
    if (d < 7) return d + "d ago";
    return new Date(then).toLocaleDateString();
  }

  // -------------------------------------------------------------------------
  // ResizeObserver on root (debounced ~50ms, rAF-batched)
  // -------------------------------------------------------------------------
  let resizeTimer = null;
  let rafPending = false;
  const ro = new ResizeObserver(() => {
    const wasBottom = isNearBottom();
    if (resizeTimer) clearTimeout(resizeTimer);
    resizeTimer = setTimeout(() => {
      if (rafPending) return;
      rafPending = true;
      requestAnimationFrame(() => {
        rafPending = false;
        recomputeMaxInputHeight();
        applyHeightClass();
        if (openOverlay) openOverlay.reposition();
        if (wasBottom) scrollToBottom(true);
      });
    }, 50);
  });
  ro.observe(root);

  function applyHeightClass() {
    root.classList.toggle("h-short", root.clientHeight < 180);
  }

  // -------------------------------------------------------------------------
  // Mock adapter (§2.6) — simulate host when no WebView bridge
  // -------------------------------------------------------------------------
  let mock = null;
  if (!hasHost) {
    mock = createMock();
  }

  function createMock() {
    let idc = 0;
    const nid = (p) => p + "-" + (++idc);
    let stopped = false;
    let timers = [];
    const sched = (fn, ms) => { const t = setTimeout(fn, ms); timers.push(t); return t; };
    const clearTimers = () => { timers.forEach(clearTimeout); timers = []; };

    function darkOrLight() {
      return (window.matchMedia && window.matchMedia("(prefers-color-scheme: light)").matches) ? "light" : "dark";
    }

    const fakeSessions = [
      { id: "mock-1", title: "Untitled", updatedAt: new Date(Date.now() - 2 * 60000).toISOString(), preview: "Refactor the parser module" },
      { id: "mock-2", title: "Fix diff renderer", updatedAt: new Date(Date.now() - 3 * 3600000).toISOString(), preview: "The inline diff was off by one line" },
      { id: "mock-3", title: "Add status bar meters", updatedAt: new Date(Date.now() - 2 * 86400000).toISOString(), preview: "Session and weekly usage" },
    ];

    const answerMd =
      "Here's a quick plan and a small example.\n\n" +
      "**Steps:**\n" +
      "- Parse the `stream-json` events\n" +
      "- Map them to *domain events*\n" +
      "- Render incrementally\n\n" +
      "See [the docs](https://example.com/docs) for details. Inline `code` looks like this.\n\n" +
      "```js\n" +
      "// stream parser\n" +
      "function parse(line) {\n" +
      "  const ev = JSON.parse(line);\n" +
      "  if (ev.type === \"text_delta\") {\n" +
      "    return { kind: \"delta\", text: ev.text };\n" +
      "  }\n" +
      "  return null;\n" +
      "}\n" +
      "```\n\n" +
      "That should get you started.";

    function receive(msg) {
      // web -> host (mock handles)
      switch (msg.type) {
        case "ready": return onReady();
        case "prompt.send": return runTurn(msg.text);
        case "turn.stop": return onStop();
        case "session.new": return onNew();
        case "session.listRequest": return sendIn("session.list", { sessions: fakeSessions }, 120);
        case "session.load": return onLoad(msg.sessionId);
        case "session.rename": return onRename(msg);
        case "session.delete": {
          const i = fakeSessions.findIndex((x) => x.id === msg.sessionId);
          if (i >= 0) fakeSessions.splice(i, 1);
          return sendIn("session.list", { sessions: fakeSessions }, 60);
        }
        case "theme.setMode": return onThemeMode(msg.mode);
        case "accent.set": return; // applied optimistically in the UI; mock just persists nothing
        case "model.set": return; // echoed into state already
        case "effort.set": return;
        case "mode.set": return;
        case "ultracode.set": return;
        case "verbosity.set": return;
        case "options.open": return sendIn("system.note", { id: nid("note"), text: "(mock) Would open the Code Astrogator settings window" }, 80);
        case "permission.set": return;
        case "autoAcceptCommands.set": return;
        case "reviewEditsInEditor.set": return;
        case "editReview.open":
          // No real editor in the mock — simulate the user accepting the edit inline.
          sendIn("system.note", { id: nid("note"), text: "(mock) Would open the file for inline review" }, 60);
          sendIn("permission.finalize", { requestId: msg.requestId, status: "approved" }, 500);
          sendIn("permission.result", { requestId: msg.requestId, status: "applied" }, 1100);
          return;
        case "permission.decision": return onDecision(msg);
        case "permission.approveAlways":
          sendIn("system.note", { id: nid("note"), text: "(mock) Added auto-approve pattern" }, 40);
          return onDecision({ requestId: msg.requestId, behavior: "allow" });
        case "attach.files": return sched(() => addAttachment("Program.cs"), 150);
        case "attach.context": return sched(() => addAttachment("src/ (folder)"), 150);
        case "attach.browse": return sched(() => addAttachment("notes.md"), 150);
        case "clipboard.paste": return sched(() => addAttachment("pasted-image.png"), 150);
        case "files.listRequest": return sendIn("files.list", { files: [
          { path: ".gitattributes", isDir: false },
          { path: ".github", isDir: true },
          { path: ".github/copilot-instructions.md", isDir: false },
          { path: ".gitignore", isDir: false },
          { path: "Assets", isDir: true },
          { path: "Assets/Bakery", isDir: true },
          { path: "Assets/Bakery/BakeryPointLight.cs", isDir: false },
          { path: "Assets/Bakery/BakeryProjectSettings.cs", isDir: false },
          { path: "src/Program.cs", isDir: false },
          { path: "docs/NOTES.md", isDir: false },
        ] }, 150);
        case "slash.run": return onSlash(msg.command);
        case "editor.insert": return;
        case "remote.start": return onRemoteStart();
        case "remote.stop": return onRemoteStop();
        case "activeFile.setEnabled":
          mockActiveFileEnabled = !!msg.enabled; // session-only in the mock too
          return sendIn("activeFile", { name: "GameManager.cs", path: "C:/repo/Assets/GameManager.cs", optionEnabled: true, enabled: mockActiveFileEnabled, lines: "42-58" }, 60);
        case "activeFile.refresh": return; // mock keeps a static selection
      }
    }

    let mockActiveFileEnabled = true;

    // ── remote control mock — link + QR, a "phone" connects after a while ──
    let mockRemote = false;
    function onRemoteStart() {
      mockRemote = true;
      const url = "https://claude.ai/code?environment=env_mock01TTYKCwQH2jZuEBg9z2N7wB";
      sendIn("remote.state", { state: "starting" }, 100);
      sendIn("remote.state", { state: "ready", url, activeSessions: 0 }, 800);
      sched(() => { if (mockRemote) handle({ type: "remote.state", state: "ready", url, activeSessions: 1 }); }, 4500);
    }
    function onRemoteStop() {
      const wasActive = mockRemote;
      mockRemote = false;
      sendIn("remote.state", { state: "stopped" }, 400);
      if (!wasActive) return;
      // simulate the imported remote session being loaded into the transcript
      sendIn("session.init", {
        sessionId: nid("mock-remote"), title: "Fix the login bug", model: state.model,
        effort: state.effort, planMode: false, ultracode: state.ultracode,
        permissionMode: state.permissionMode, verbosity: state.verbosity,
        cwd: state.cwd, tokens: state.tokens, limits: state.limits, plan: state.plan,
      }, 600);
      sendIn("transcript.load", {
        sessionId: "mock-remote", title: "Fix the login bug",
        messages: [
          { role: "user", id: nid("m"), text: "Fix the login bug", ts: new Date().toISOString() },
          { role: "tool", id: nid("m"), toolName: "Read", input: { file_path: "src/Auth.cs" }, status: "ok", ts: new Date().toISOString() },
          { role: "assistant", id: nid("m"), text: "Done — the token refresh now retries once before failing.", ts: new Date().toISOString() },
        ],
      }, 700);
      sendIn("system.note", { id: nid("note"), text: "Remote session imported" }, 800);
    }

    function sendIn(type, payload, ms) { sched(() => handle(Object.assign({ type }, payload)), ms || 0); }

    // reset times for the meter tooltips (session in ~3h, weekly in ~4d)
    function mockLimits(sessionPct, weeklyPct) {
      return {
        sessionPct, weeklyPct,
        sessionResetsAt: new Date(Date.now() + 3 * 3600000).toISOString(),
        weeklyResetsAt: new Date(Date.now() + 4 * 86400000).toISOString(),
      };
    }

    function onRename(msg) {
      const s = fakeSessions.find((x) => x.id === msg.sessionId);
      if (s) s.title = msg.title;
    }

    function onReady() {
      const resolved = darkOrLight();
      sendIn("theme", { mode: "auto", resolved, vars: {} }, 0);
      sendIn("auth.state", { loggedIn: true, mode: "oauth" }, 10);
      sendIn("session.init", {
        sessionId: "mock-1", title: "Untitled", model: "claude-opus-4-8",
        effort: "high", planMode: false, ultracode: false, permissionMode: "ask",
        verbosity: "normal",
        cwd: "C:/Users/Jan/source/repos/CodeAstrogator",
        tokens: 0, limits: mockLimits(12, 34), plan: "Team Plan",
      }, 20);
      // CLI-reported slash commands (bare names, like system/init.slash_commands)
      sendIn("slash.commands", { commands: [
        "clear", "compact", "context", "init", "usage", "review", "security-review", "deep-research",
      ] }, 30);
      sendIn("activeFile", { name: "GameManager.cs", path: "C:/repo/Assets/GameManager.cs", optionEnabled: true, enabled: mockActiveFileEnabled, lines: "42-58" }, 40);
    }

    function onThemeMode(mode) {
      const resolved = mode === "auto" ? darkOrLight() : mode;
      sendIn("theme", { mode, resolved, vars: {} }, 0);
    }

    function onNew() {
      sendIn("session.init", {
        sessionId: nid("mock-new"), title: "Untitled", model: state.model,
        effort: state.effort, planMode: state.planMode, ultracode: state.ultracode,
        permissionMode: state.permissionMode,
        cwd: state.cwd, tokens: 0, limits: mockLimits(12, 34), plan: "Team Plan",
      }, 30);
    }

    function onLoad(sessionId) {
      const s = fakeSessions.find((x) => x.id === sessionId) || fakeSessions[0];
      sendIn("transcript.load", {
        sessionId: s.id, title: s.title,
        messages: [
          { role: "user", id: "u1", text: "Can you explain the parser?", ts: Date.now(),
            attachments: [{ name: "NdjsonParser.cs", path: "C:/repo/Core/NdjsonParser.cs" }, { name: "GameManager.cs:42-58", path: "C:/repo/Assets/GameManager.cs#L42-58" }] },
          { role: "assistant", id: "a1", text: "Sure! It reads **NDJSON** and emits domain events.\n\n| Event | Maps to | Streamed |\n| --- | --- | :-: |\n| `text_delta` | assistant.delta | yes |\n| `tool_use` | tool.use | no |\n| `result` | turn.result | no |\n\n```json\n{ \"type\": \"text_delta\", \"text\": \"hi\" }\n```", ts: Date.now() },
        ],
      }, 100);
    }

    function onSlash(command) {
      if (command === "/clear") {
        onNew();
        return;
      }
      if (command === "/compact") {
        const pre = state.contextTokens || 26540;
        const post = Math.max(2000, Math.round(pre * 0.12));
        sendIn("status", { state: "working", text: "Compacting context…" }, 0);
        sendIn("system.note", { id: nid("n"), text: "Context compacted · " + formatNum(pre) + " → " + formatNum(post) + " tokens" }, 1400);
        sendIn("usage.update", { contextTokens: post }, 1450);
        sendIn("turn.result", { sessionId: state.sessionId, costUsd: 0.198, tokens: { input: 0, output: 0, total: 0 }, contextTokens: post, durationMs: 1500, limits: state.limits }, 1500);
        sendIn("status", { state: "ready" }, 1550);
        return;
      }
      // decision #8 — slash commands answer via the result line; the host renders
      // that as a synthetic assistant block. Simulate the same here.
      const id = nid("slash");
      sendIn("status", { state: "working" }, 0);
      sendIn("assistant.start", { id }, 150);
      sendIn("assistant.delta", { id, text: "Result of `" + command + "` (mock): command executed." }, 220);
      sendIn("assistant.end", { id }, 300);
      sendIn("turn.result", { sessionId: state.sessionId, costUsd: 0.001, tokens: { input: 40, output: 12, total: state.tokens + 52 }, contextTokens: state.contextTokens + 52, durationMs: 800, limits: state.limits }, 360);
      sendIn("status", { state: "ready" }, 400);
    }

    function onStop() {
      stopped = true;
      clearTimers();
      const a = state.activeAssistant;
      if (a) handle({ type: "assistant.end", id: a.id });
      sendIn("system.note", { id: nid("n"), text: "Turn stopped" }, 0); // decision #11
      sendIn("status", { state: "ready" }, 10);
    }

    const pendingResolvers = {}; // requestId → continuation (mock permission decisions)

    let announced = false;

    function runTurn(text) {
      stopped = false;
      const aId = nid("a");
      sendIn("status", { state: "working" }, 0);

      // session-start note, once (host does this on the first system/init)
      if (!announced) {
        announced = true;
        sendIn("system.note", { id: nid("n"), text: "Session started · claude-opus-4-8 · C:/Users/Jan/source/repos/CodeAstrogator" }, 60);
      }

      // thinking block before the answer (decision #13) — mirrors the real
      // print-mode CLI: redacted text, only a growing estimated_tokens counter
      const thId = nid("th");
      sendIn("thinking.start", { id: thId }, 120);
      let tDelay = 160;
      for (let est = 50; est <= 400; est += 50) {
        const e = est;
        sched(() => { if (!stopped) handle({ type: "thinking.delta", id: thId, text: "", estimatedTokens: e }); }, tDelay);
        tDelay += 60;
      }
      sched(() => { if (!stopped) handle({ type: "thinking.end", id: thId }); }, tDelay + 40);

      sendIn("assistant.start", { id: aId }, tDelay + 120);

      // stream ~30 chunks
      const chunks = chunkify(answerMd, 30);
      let delay = tDelay + 200;
      chunks.forEach((c) => {
        sched(() => { if (!stopped) handle({ type: "assistant.delta", id: aId, text: c }); }, delay);
        delay += 40;
      });

      // end the first assistant block (caret removed, markdown rendered)
      sched(() => { if (!stopped) handle({ type: "assistant.end", id: aId }); }, delay + 40);

      // tool use + result (long summary exercises "Show more", decision #17)
      const tId = nid("t");
      sched(() => { if (stopped) return; handle({ type: "tool.use", id: tId, name: "Read", input: { file_path: "src/Program.cs" }, status: "running" }); }, delay + 100);
      sched(() => {
        if (stopped) return;
        let longOut = "using System;\n";
        for (let i = 1; i <= 60; i++) longOut += "// line " + i + " of the file contents preview …\n";
        handle({ type: "tool.result", id: tId, status: "ok", summary: longOut });
      }, delay + 500);

      // todo checklist (decision #15)
      const todoId = nid("td");
      sched(() => {
        if (stopped) return;
        handle({
          type: "tool.use", id: todoId, name: "TodoWrite", status: "running",
          input: { todos: [
            { content: "Parse NDJSON stream", status: "completed" },
            { content: "Map events to UI messages", status: "in_progress" },
            { content: "Write fixture tests", status: "pending" },
          ] },
        });
      }, delay + 600);
      sched(() => { if (stopped) return; handle({ type: "tool.result", id: todoId, status: "ok", summary: "" }); }, delay + 680);

      // AskUserQuestion → routed through the permission hook → interactive question card.
      // tool.use first (CLI emits it before the prompt), then question.request replaces it.
      const askId = nid("ask");
      sched(() => {
        if (stopped) return;
        handle({ type: "tool.use", id: askId, name: "AskUserQuestion", status: "running", input: {} });
        handle({ type: "question.request", requestId: askId, questions: [
          { question: "Which test framework should the fixtures use?", header: "Framework", multiSelect: false,
            options: [ { label: "xUnit", description: "Already referenced by the test project." }, { label: "NUnit", description: "Adds a new dependency." } ] },
        ] });
      }, delay + 690);

      // Bash command permission card — gets the "Always" button (command/MCP only)
      const bashId = nid("perm");
      sched(() => {
        if (stopped) return;
        handle({ type: "tool.use", id: bashId, name: "Bash", status: "running", input: { command: "npm run build & npm test" } });
        handle({
          type: "permission.request", requestId: bashId, toolName: "Bash", canApproveAlways: true,
          input: { command: "npm run build & npm test", description: "Build then test" },
          approveAlwaysSuggestions: ["npm run build", "npm test"], // nested → split into two patterns
        });
        pendingResolvers[bashId] = () => {}; // decide → card collapses; turn continues via the edit below
      }, delay + 695);

      // Edit permission card with inline diff — NO "Always" button (file edits use the diff flow)
      const reqId = nid("perm");
      sched(() => {
        if (stopped) return;
        handle({
          type: "permission.request",
          requestId: reqId,
          toolName: "Edit",
          input: { file_path: "src/Program.cs" },
          // when "Review edits in the editor" is on, render the file card (Open in editor / Reject all)
          editInEditor: state.reviewEditsInEditor,
          hunkCount: 2,
          diff: {
            path: "src/Program.cs",
            startLine: 12, // demo: edit sits partway into the file
            oldText:
              "using System;\n\nclass Program {\n    static void Main() {\n        Console.WriteLine(\"Hello\");\n    }\n}",
            newText:
              "using System;\n\nclass Program {\n    static void Main() {\n        Console.WriteLine(\"Hello, Claude!\");\n        Console.WriteLine(\"Done.\");\n    }\n}",
          },
        });
        // resume after decision: store the continuation
        pendingResolvers[reqId] = (behavior) => continueTurn(aId, reqId, behavior);
      }, delay + 700);

      // auto-approved edit (acceptEdits/bypass): pre-decided green card, no buttons,
      // upgrades to "applied" on tool.result — same look as an approved edit
      const autoId = nid("auto");
      sched(() => {
        if (stopped) return;
        handle({ type: "tool.use", id: autoId, name: "Write", status: "running", input: { file_path: "src/Notes.md" } });
        handle({
          type: "permission.request", requestId: autoId, toolName: "Write", autoApproved: true,
          input: { file_path: "src/Notes.md", content: "# Notes\n\nAuto-approved write.\n" },
          diff: { path: "src/Notes.md", startLine: 1, oldText: "", newText: "# Notes\n\nAuto-approved write.\n" },
        });
      }, delay + 760);
      sched(() => { if (stopped) return; sendIn("permission.result", { requestId: autoId, status: "applied" }, 0); }, delay + 900);
    }

    function continueTurn(aId, reqId, behavior) {
      if (stopped) return;
      // approved edit "executes" → mark the card applied (host does this on tool.result)
      if (behavior === "allow") sendIn("permission.result", { requestId: reqId, status: "applied" }, 30);
      // fresh assistant block for the continuation after the permission decision
      const aId2 = nid("a");
      sendIn("status", { state: "working" }, 0);
      sendIn("assistant.start", { id: aId2 }, 50);
      const more = "\n\nApplied the edit. The program now prints two lines.";
      const chunks = chunkify(more, 12);
      let delay = 120;
      chunks.forEach((c) => { sched(() => { if (!stopped) handle({ type: "assistant.delta", id: aId2, text: c }); }, delay); delay += 40; });
      sched(() => { if (stopped) return; handle({ type: "assistant.end", id: aId2 }); }, delay + 60);
      sched(() => {
        if (stopped) return;
        handle({
          type: "turn.result",
          sessionId: state.sessionId,
          costUsd: 0.0123,
          tokens: { input: 1200, output: 340, total: 1540 },
          contextTokens: 26540, // ≈ 13% of the 200k window
          durationMs: 4200,
          limits: mockLimits(15, 35),
        });
        handle({ type: "status", state: "ready" });
      }, delay + 140);
    }

    function onDecision(msg) {
      if (msg.behavior === "deny")
        sendIn("system.note", { id: nid("n"), text: "Permission denied by user" }, 0); // decision #20
      // close the working/permission state and continue (per-request continuation)
      const r = pendingResolvers[msg.requestId];
      if (r) { delete pendingResolvers[msg.requestId]; r(msg.behavior); }
    }

    function chunkify(str, n) {
      const size = Math.max(1, Math.ceil(str.length / n));
      const out = [];
      for (let i = 0; i < str.length; i += size) out.push(str.slice(i, i + size));
      return out;
    }

    // listen to prefers-color-scheme changes for auto
    if (window.matchMedia) {
      const mq = window.matchMedia("(prefers-color-scheme: light)");
      const onChange = () => { if (state.themeMode === "auto") onThemeMode("auto"); };
      if (mq.addEventListener) mq.addEventListener("change", onChange);
      else if (mq.addListener) mq.addListener(onChange);
    }

    return { receive };
  }

  // -------------------------------------------------------------------------
  // Boot
  // -------------------------------------------------------------------------
  function boot() {
    renderEmptyOrSignin();
    updateModelModeLabel();
    updateStatusbar();
    applyStatus("ready");
    recomputeMaxInputHeight();
    applyHeightClass();
    renderAttachments();
    post("ready", {});
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", boot);
  } else {
    boot();
  }
})();
