// wwwroot/js/midi-roll.js - optimized drop-in replacement
const PLAYHEAD_RATIO = 0.18;
const LOOK_AHEAD_US = 4_500_000;
const KEYBOARD_WIDTH = 58;
const RULER_HEIGHT = 25;

// Performance knobs
const MAX_DPR = 1.25;                // Keep canvas raster work down on HiDPI displays.
const LIGHT_RENDER_GAP_MS = 16;      // Keep sparse songs near 60fps.
const SEEK_HARD_SYNC_US = 60_000;
const SOFT_SYNC_RATIO = 0.06;

// Four velocity buckets instead of one CSS color string per note.
const NORMAL_ALPHA = [0.48, 0.62, 0.76, 0.9];
const HIT_STYLE = "#ff3f91";
const HIT_INNER_STYLE = "rgba(255, 255, 255, 0.85)";
const GRID_FALLBACK = "rgba(148, 163, 184, 0.16)";
const BLACK_PITCH_CLASSES = new Set([1, 3, 6, 8, 10]);
const PITCH_NAMES = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

let canvas = null;
let ctx = null;
let logicalWidth = 0;
let logicalHeight = 0;
let currentDpr = 1;
let expandButton = null;
let currentHost = "playback";
let mainStage = null;
const rollHosts = {
  playback: { panel: null, restore: null, collapsed: false, fullscreen: false },
  editor: { panel: null, collapsed: false, fullscreen: false }
};

// Cached DOM refs
let timeEl = null;
let dotEl = null;
let modeTag = null;

// Notes and binary-search prefix data
let notes = [];
let maxEndPrefixUs = [];
let minPitch = 48;
let maxPitch = 84;

// Piano-roll layout cache. Rebuilt only when canvas size or note range changes.
let headX = 0;
let pxPerUs = 0;
let pitchSpan = 12;
let keyWidth = KEYBOARD_WIDTH;
let rulerHeight = RULER_HEIGHT;
let rowHeight = 4;
let gridYs = [];
let accentColor = "#818cf8";
let mutedColor = "#94a3b8";
let borderColor = "rgba(148, 163, 184, 0.24)";
let viewportBgColor = "rgba(10, 13, 23, 0.24)";
let lastThemeSyncPerfMs = -Infinity;
let chromeCanvas = null;
let chromeCtx = null;
let chromeCacheKey = "";

// Playback clock state
let anchorPositionUs = 0;
let anchorPerfMs = 0;
let currentSpeed = 1.0;
let isPlaying = false;
let rafId = 0;
let lastRenderedUs = 0;
let lastPaintPerfMs = -Infinity;
let renderGapMs = LIGHT_RENDER_GAP_MS;

// DOM throttling/state cache
let lastClockText = "";
let lastClockUpdatePerfMs = 0;
let lastIndicatorPlaying = null;
let lastPausedRenderedUs = -1;

// Reused geometry buffers. Resetting length does not create one object per bar.
const normalBarRuns = [];                  // flat [bucket, x, y, w, h, ...], source order
const activeBars = [];                     // flat [x, y, w, h, ...]

export function initMidiRoll() {
  canvas = document.getElementById("midi-roll-canvas");
  if (!canvas) return;

  // desynchronized is a Chromium hint; unsupported browsers simply ignore it.
  ctx = canvas.getContext("2d", { alpha: false, desynchronized: true });

  timeEl = document.getElementById("midi-roll-time");
  dotEl = document.getElementById("midi-roll-live-dot");
  modeTag = document.getElementById("midi-roll-mode-tag");
  expandButton = document.getElementById("btn-midi-roll-expand");
  mainStage = document.querySelector(".main-stage");
  rollHosts.playback.panel = document.getElementById("midi-roll-panel");
  rollHosts.playback.restore = document.getElementById("midi-roll-restore");
  rollHosts.editor.panel = document.getElementById("piano-editor-panel");
  if (expandButton) {
    const panel = rollHosts.playback.panel;
    const expanded = Boolean(panel?.classList.contains("is-expanded"));
    expandButton.setAttribute("aria-expanded", String(expanded));
    expandButton.textContent = expanded ? "缩小" : "放大";
    expandButton.setAttribute("aria-label", expanded ? "缩小钢琴卷帘" : "放大钢琴卷帘");
    expandButton.addEventListener("click", () => {
      const panel = rollHosts.playback.panel;
      if (!panel) return;
      const expanded = panel.classList.toggle("is-expanded");
      expandButton.setAttribute("aria-expanded", String(expanded));
      expandButton.textContent = expanded ? "缩小" : "放大";
      expandButton.setAttribute("aria-label", expanded ? "缩小钢琴卷帘" : "放大钢琴卷帘");
    });
  }

  bindHostControls("playback", "btn-midi-roll-collapse", "btn-midi-roll-restore", "btn-midi-roll-fullscreen");
  bindHostControls("editor", null, null, "midi-editor-fullscreen");
  syncHostControls("playback");
  syncHostControls("editor");
  document.addEventListener("keydown", handleRollKeydown);
  window.addEventListener("resize", () => {
    updateFullscreenBounds();
    scheduleCanvasRefresh();
  }, { passive: true });
  window.addEventListener("nte:page-changed", () => {
    leaveFullscreen(currentHost);
    scheduleCanvasRefresh();
  });

  const ro = new ResizeObserver((entries) => {
    for (const entry of entries) {
      const width = Math.max(1, entry.contentRect.width);
      const height = Math.max(1, entry.contentRect.height);
      const dpr = Math.min(window.devicePixelRatio || 1, MAX_DPR);

      // ResizeObserver may emit duplicate values. Avoid reallocating the backing store.
      if (
        Math.abs(width - logicalWidth) < 0.25 &&
        Math.abs(height - logicalHeight) < 0.25 &&
        Math.abs(dpr - currentDpr) < 0.01
      ) {
        continue;
      }

      resizeCanvas(width, height, dpr);
    }
  });
  ro.observe(canvas);

  document.addEventListener("visibilitychange", () => {
    if (document.hidden) {
      if (rafId) {
        cancelAnimationFrame(rafId);
        rafId = 0;
      }
      return;
    }

    if (isPlaying && !rafId) {
      // Catch up elapsed hidden time BEFORE moving the perf anchor.
      const now = performance.now();
      const caughtUpUs = getInterpolatedPositionUs(now);
      anchorPositionUs = caughtUpUs;
      anchorPerfMs = now;
      lastRenderedUs = caughtUpUs;
      lastPaintPerfMs = -Infinity;
      rafId = requestAnimationFrame(tick);
    }
  });
}

let canvasRefreshFrame = 0;
function scheduleCanvasRefresh() {
  if (canvasRefreshFrame) cancelAnimationFrame(canvasRefreshFrame);
  canvasRefreshFrame = requestAnimationFrame(() => {
    canvasRefreshFrame = requestAnimationFrame(() => {
      canvasRefreshFrame = 0;
      if (!canvas?.isConnected) return;
      const rect = canvas.getBoundingClientRect();
      if (rect.width <= 0 || rect.height <= 0) return;
      resizeCanvas(rect.width, rect.height, Math.min(window.devicePixelRatio || 1, MAX_DPR), true);
    });
  });
}

function resizeCanvas(width, height, dpr, force = false) {
  logicalWidth = Math.max(1, width);
  logicalHeight = Math.max(1, height);
  currentDpr = dpr;
  const backingWidth = Math.max(1, Math.round(logicalWidth * dpr));
  const backingHeight = Math.max(1, Math.round(logicalHeight * dpr));
  if (force || canvas.width !== backingWidth || canvas.height !== backingHeight) {
    canvas.width = backingWidth;
    canvas.height = backingHeight;
  }
  ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
  chromeCacheKey = "";
  syncThemeColors();
  rebuildProjectionCache();
  renderFrame(getInterpolatedPositionUs());
}

export function setMidiRollNotes(rawNotes, modeLabel = "吉他/键盘") {
  if (!modeTag) modeTag = document.getElementById("midi-roll-mode-tag");
  if (modeTag) modeTag.textContent = modeLabel;

  if (!Array.isArray(rawNotes) || rawNotes.length === 0) {
    notes = [];
    maxEndPrefixUs = [];
    minPitch = 48;
    maxPitch = 84;
    lastRenderedUs = 0;
    rebuildProjectionCache();
    renderFrame(0);
    return;
  }

  notes = rawNotes
    .map((n) => {
      const startUs = Math.max(0, Number(n.startUs ?? (n.startMs * 1000)) || 0);
      const durationUs = Math.max(15_000, Number(n.durationUs ?? (n.durationMs * 1000)) || 80_000);
      const pitch = Number(n.pitch ?? n.noteNumber ?? 60);
      const velocity = Math.max(0, Math.min(127, Number(n.velocity ?? 90) || 0));

      return {
        startUs,
        durationUs,
        endUs: startUs + durationUs,
        pitch,
        velocityBucket: Math.min(3, velocity >>> 5),

        // Screen projection, recalculated only on resize/range changes.
        startPx: 0,
        widthPx: 4,
        yPx: 0
      };
    })
    .sort((a, b) => a.startUs - b.startUs);

  let lo = 127;
  let hi = 0;
  let runningMaxEnd = 0;
  maxEndPrefixUs = new Array(notes.length);

  for (let i = 0; i < notes.length; i++) {
    const n = notes[i];
    if (n.pitch < lo) lo = n.pitch;
    if (n.pitch > hi) hi = n.pitch;
    if (n.endUs > runningMaxEnd) runningMaxEnd = n.endUs;
    maxEndPrefixUs[i] = runningMaxEnd;
  }

  minPitch = Math.max(21, lo - 2);
  maxPitch = Math.min(108, Math.max(minPitch + 12, hi + 2));
  rebuildProjectionCache();

  anchorPositionUs = 0;
  anchorPerfMs = performance.now();
  lastRenderedUs = 0;
  lastPausedRenderedUs = -1;
  renderFrame(0);
}

// Track which page's roll controls own fullscreen Escape handling. Playback
// preview and editable MIDI use separate canvases and always render independently.
export function setMidiRollEditorMode(enabled) {
  if (enabled) currentHost = "editor";
  else {
    leaveFullscreen("editor");
    currentHost = "playback";
  }
  syncHostControls("playback");
  syncHostControls("editor");
  if (!enabled) renderFrame(getInterpolatedPositionUs());
}

function bindHostControls(hostName, collapseId, restoreId, fullscreenId) {
  const host = rollHosts[hostName];
  if (collapseId) document.getElementById(collapseId)?.addEventListener("click", () => {
    if (host.fullscreen) leaveFullscreen(hostName);
    setCollapsed(hostName, true);
  });
  if (restoreId) document.getElementById(restoreId)?.addEventListener("click", () => setCollapsed(hostName, false));
  document.getElementById(fullscreenId)?.addEventListener("click", () => {
    if (host.collapsed) setCollapsed(hostName, false);
    if (host.fullscreen) leaveFullscreen(hostName);
    else enterFullscreen(hostName);
  });
}

function setCollapsed(hostName, collapsed) {
  const host = rollHosts[hostName];
  if (!host?.panel) return;
  host.collapsed = Boolean(collapsed);
  host.panel.classList.toggle("is-collapsed", host.collapsed);
  if (host.restore) host.restore.hidden = !host.collapsed;
  syncHostControls(hostName);
  const focusTarget = host.collapsed
    ? host.restore?.querySelector("button")
    : document.getElementById("btn-midi-roll-collapse");
  focusTarget?.focus();
}

function syncHostControls(hostName) {
  const host = rollHosts[hostName];
  if (!host?.panel) return;
  const collapseButton = hostName === "playback" ? document.getElementById("btn-midi-roll-collapse") : null;
  const fullscreenButton = document.getElementById(hostName === "editor" ? "midi-editor-fullscreen" : "btn-midi-roll-fullscreen");
  const restoreButton = host.restore?.querySelector("button");
  if (collapseButton) {
    collapseButton.setAttribute("aria-expanded", String(!host.collapsed));
    collapseButton.setAttribute("aria-label", host.collapsed ? "展开钢琴卷帘" : "收起钢琴卷帘");
  }
  restoreButton?.setAttribute("aria-expanded", String(!host.collapsed));
  restoreButton?.setAttribute("aria-controls", host.panel.id);
  if (fullscreenButton) {
    fullscreenButton.setAttribute("aria-pressed", String(host.fullscreen));
    fullscreenButton.setAttribute("aria-label", host.fullscreen ? "退出钢琴卷帘全屏" : "钢琴卷帘全屏显示");
    fullscreenButton.textContent = host.fullscreen ? "退出全屏" : "全屏";
  }
}

function enterFullscreen(hostName) {
  const host = rollHosts[hostName];
  if (!host?.panel || !mainStage) return;
  if (rollHosts[hostName === "editor" ? "playback" : "editor"].fullscreen) {
    leaveFullscreen(hostName === "editor" ? "playback" : "editor");
  }
  host.fullscreen = true;
  host.panel.classList.add("is-roll-fullscreen");
  mainStage.classList.add("has-roll-fullscreen");
  updateFullscreenBounds();
  syncHostControls(hostName);
}

function leaveFullscreen(hostName) {
  const host = rollHosts[hostName];
  if (!host?.fullscreen) return;
  host.fullscreen = false;
  host.panel?.classList.remove("is-roll-fullscreen");
  host.panel?.style.removeProperty("--roll-fullscreen-top");
  host.panel?.style.removeProperty("--roll-fullscreen-left");
  host.panel?.style.removeProperty("--roll-fullscreen-width");
  host.panel?.style.removeProperty("--roll-fullscreen-height");
  if (!rollHosts.playback.fullscreen && !rollHosts.editor.fullscreen) mainStage?.classList.remove("has-roll-fullscreen");
  syncHostControls(hostName);
}

function updateFullscreenBounds() {
  const host = rollHosts[currentHost];
  if (!host?.fullscreen || !host.panel || !mainStage) return;
  const stageRect = mainStage.getBoundingClientRect();
  const headerRect = mainStage.querySelector(".stage-header")?.getBoundingClientRect();
  const top = Math.max(0, (headerRect?.bottom ?? stageRect.top) - stageRect.top);
  host.panel.style.setProperty("--roll-fullscreen-top", `${top}px`);
  host.panel.style.setProperty("--roll-fullscreen-left", "0px");
  host.panel.style.setProperty("--roll-fullscreen-width", "100%");
  host.panel.style.setProperty("--roll-fullscreen-height", `${Math.max(0, stageRect.height - top)}px`);
}

function handleRollKeydown(event) {
  if (event.key !== "Escape") return;
  const host = rollHosts[currentHost];
  if (!host?.fullscreen) return;
  leaveFullscreen(currentHost);
}

export function updateMidiRollPlayback({ positionUs, speed = 1.0, playing = false }) {
  const incomingUs = Math.max(0, Number(positionUs) || 0);
  const nowPerf = performance.now();
  const nextSpeed = Number(speed) > 0 ? Number(speed) : 1.0;
  const wasPlaying = isPlaying;

  isPlaying = Boolean(playing);
  currentSpeed = nextSpeed;
  updatePlayingIndicator(isPlaying);

  if (!isPlaying) {
    if (rafId) {
      cancelAnimationFrame(rafId);
      rafId = 0;
    }

    anchorPositionUs = incomingUs;
    anchorPerfMs = nowPerf;
    lastRenderedUs = incomingUs;

    // If the backend keeps polling the same paused position, do not repaint the canvas.
    if (Math.abs(incomingUs - lastPausedRenderedUs) >= 1_000 || wasPlaying) {
      lastPausedRenderedUs = incomingUs;
      renderFrame(incomingUs, nowPerf);
    }
    return;
  }

  lastPausedRenderedUs = -1;

  if (!wasPlaying) {
    anchorPositionUs = incomingUs;
    anchorPerfMs = nowPerf;
    lastRenderedUs = incomingUs;
    lastPaintPerfMs = -Infinity;
  } else {
    const predictedUs = getInterpolatedPositionUs(nowPerf);
    const driftUs = incomingUs - predictedUs;

    if (Math.abs(driftUs) > SEEK_HARD_SYNC_US) {
      anchorPositionUs = incomingUs;
      anchorPerfMs = nowPerf;
      lastRenderedUs = incomingUs;
    } else {
      anchorPositionUs = predictedUs + driftUs * SOFT_SYNC_RATIO;
      anchorPerfMs = nowPerf;
    }
  }

  if (!rafId && !document.hidden) {
    rafId = requestAnimationFrame(tick);
  }
}

function updatePlayingIndicator(playing) {
  if (lastIndicatorPlaying === playing) return;
  lastIndicatorPlaying = playing;

  if (!dotEl) dotEl = document.getElementById("midi-roll-live-dot");
  if (!dotEl) return;

  dotEl.style.background = playing ? "var(--success)" : "var(--muted)";
  dotEl.style.boxShadow = playing ? "0 0 8px var(--success)" : "none";
}

function getInterpolatedPositionUs(nowPerf = performance.now()) {
  if (!isPlaying) return anchorPositionUs;
  const elapsedMs = Math.max(0, nowPerf - anchorPerfMs);
  return anchorPositionUs + elapsedMs * 1000 * currentSpeed;
}

function tick(nowPerf) {
  rafId = 0;
  if (!isPlaying) return;

  // Avoid repainting at 120/144/165/240Hz when the visual does not need it.
  if (nowPerf - lastPaintPerfMs >= renderGapMs) {
    const rawUs = getInterpolatedPositionUs(nowPerf);
    const curUs = Math.max(lastRenderedUs, rawUs);
    lastRenderedUs = curUs;
    lastPaintPerfMs = nowPerf;
    renderFrame(curUs, nowPerf);
  }

  if (isPlaying && !document.hidden) {
    rafId = requestAnimationFrame(tick);
  }
}

function rebuildProjectionCache() {
  if (!logicalWidth || !logicalHeight) return;

  const w = logicalWidth;
  const h = logicalHeight;

  keyWidth = Math.min(KEYBOARD_WIDTH, Math.max(44, w * 0.14));
  rulerHeight = Math.min(RULER_HEIGHT, Math.max(20, h * 0.14));
  headX = Math.max(keyWidth + 8, w * PLAYHEAD_RATIO);
  headX = Math.min(headX, w - 8);
  pxPerUs = Math.max(0.00001, (w - headX) / LOOK_AHEAD_US);
  pitchSpan = Math.max(12, maxPitch - minPitch + 1);
  rowHeight = Math.max(1, (h - rulerHeight) / pitchSpan);

  gridYs = [];
  for (let p = minPitch; p <= maxPitch; p++) {
    if (p % 12 === 0) {
      gridYs.push(Math.round(h - ((p - minPitch + 0.5) / pitchSpan) * (h - rulerHeight)) + 0.5);
    }
  }

  // Turn time/pitch math into cached screen-space values.
  for (let i = 0; i < notes.length; i++) {
    const n = notes[i];
    const pitchRatio = (n.pitch - minPitch) / pitchSpan;
    n.startPx = n.startUs * pxPerUs;
    n.widthPx = Math.max(2, n.durationUs * pxPerUs);
    n.yPx = h - rulerHeight - (pitchRatio + 1) * (h - rulerHeight) / pitchSpan;
  }
}

function findUpperBoundIndex(targetUs) {
  let low = 0;
  let high = notes.length;
  while (low < high) {
    const mid = (low + high) >>> 1;
    if (notes[mid].startUs <= targetUs) low = mid + 1;
    else high = mid;
  }
  return low;
}

function findFirstPossibleIndex(windowStartUs) {
  let low = 0;
  let high = maxEndPrefixUs.length;
  while (low < high) {
    const mid = (low + high) >>> 1;
    if (maxEndPrefixUs[mid] < windowStartUs) low = mid + 1;
    else high = mid;
  }
  return low;
}

function renderFrame(curUs, nowPerf = performance.now()) {
  if (!ctx || !logicalWidth || !logicalHeight) return;

  const w = logicalWidth;
  const h = logicalHeight;
  ctx.clearRect(0, 0, w, h);

  if (nowPerf - lastThemeSyncPerfMs >= 1_000) {
    syncThemeColors();
    lastThemeSyncPerfMs = nowPerf;
  }
  const plotTop = rulerHeight;
  const plotLeft = keyWidth;
  const plotWidth = Math.max(0, w - plotLeft);

  // Clip every dynamic mark to the pitch/time plot. The keyboard and ruler stay fixed.
  drawPianoChrome(curUs, w, h, plotTop, plotLeft, plotWidth);

  const visibleBehindUs = headX / pxPerUs;
  const visibleAheadUs = Math.max(0, (w - headX) / pxPerUs);
  const windowStartUs = curUs - visibleBehindUs;
  const windowEndUs = curUs + visibleAheadUs;
  const startIdx = findFirstPossibleIndex(windowStartUs);
  const endIdx = findUpperBoundIndex(windowEndUs);
  const visibleNoteCandidates = endIdx - startIdx;
  // Dense charts used to rebuild thousands of note rectangles on every display
  // refresh. Pace those charts at 20–30 fps so playback/input work keeps room.
  renderGapMs = visibleNoteCandidates > 1_800 ? 50 : visibleNoteCandidates > 600 ? 33 : LIGHT_RENDER_GAP_MS;

  // Reuse flat buffers instead of allocating per-frame bar objects. Keep normal
  // notes in source order so overlapping notes retain their original layering.
  normalBarRuns.length = 0;
  activeBars.length = 0;

  // All notes translate by exactly the same amount each frame.
  const timeShiftPx = headX - curUs * pxPerUs;

  // ONE visible-note pass. Active notes are only saved for the tiny overlay pass.
  for (let i = startIdx; i < endIdx; i++) {
    const n = notes[i];
    if (n.endUs < windowStartUs) continue;

    if (n.pitch < minPitch || n.pitch > maxPitch) continue;
    const rawX = timeShiftPx + n.startPx;
    const x = Math.max(plotLeft, rawX);
    const right = Math.min(w, rawX + n.widthPx);
    const width = right - x;
    if (width <= 0) continue;
    const pitchIndex = n.pitch - minPitch;
    const y = h - rulerHeight - (pitchIndex + 1) * (h - rulerHeight) / pitchSpan + Math.max(1, rowHeight * 0.14);
    const noteHeight = Math.max(2, rowHeight * 0.72);

    if (n.startUs <= curUs && n.endUs >= curUs) {
      activeBars.push(x, y, width, noteHeight);
    } else {
      normalBarRuns.push(n.velocityBucket, x, y, width, noteHeight);
    }
  }

  // Batch adjacent notes of the same velocity while preserving overlap order.
  ctx.save();
  ctx.beginPath();
  ctx.rect(plotLeft, plotTop, plotWidth, h - plotTop);
  ctx.clip();
  drawOrderedRectRuns(normalBarRuns);

  // Active overlay stays above ordinary notes. Usually only a handful of bars.
  if (activeBars.length) {
    drawRectBatch(activeBars, HIT_STYLE);

    // Small white center highlight, batched too.
    ctx.fillStyle = HIT_INNER_STYLE;
    ctx.beginPath();
    let hasInner = false;
    for (let i = 0; i < activeBars.length; i += 4) {
      const x = activeBars[i];
      const y = activeBars[i + 1];
      const bw = activeBars[i + 2];
      const bh = activeBars[i + 3];
      if (bw >= 8 && bh >= 5) {
        ctx.rect(x + 2, y + 1, bw - 4, Math.max(1, bh - 2));
        hasInner = true;
      }
    }
    if (hasInner) ctx.fill();
  }
  ctx.restore();

  if (nowPerf - lastClockUpdatePerfMs >= 60 || !isPlaying) {
    lastClockUpdatePerfMs = nowPerf;
    const clockStr = formatUsToClock(curUs);
    if (clockStr !== lastClockText) {
      lastClockText = clockStr;
      if (!timeEl) timeEl = document.getElementById("midi-roll-time");
      if (timeEl) timeEl.textContent = clockStr;
    }
  }
}

function drawPianoChrome(curUs, w, h, plotTop, plotLeft, plotWidth) {
  ensureStaticChrome(w, h, plotTop, plotLeft, plotWidth);
  ctx.drawImage(chromeCanvas, 0, 0, w, h);

  // Only the time ruler moves. Keep it out of the cached keyboard/grid layer.
  drawTimeRuler(curUs, w, h, plotTop, plotLeft);

  // Active playback marker is painted in the canvas so it stays aligned in all themes/layouts.
  ctx.strokeStyle = HIT_STYLE;
  ctx.lineWidth = 2;
  ctx.beginPath();
  ctx.moveTo(Math.round(headX) + 0.5, 0);
  ctx.lineTo(Math.round(headX) + 0.5, h);
  ctx.stroke();

  if (!notes.length) {
    const plotHeight = h - plotTop;
    ctx.fillStyle = mutedColor;
    ctx.font = "12px sans-serif";
    ctx.textAlign = "center";
    ctx.textBaseline = "middle";
    ctx.fillText("选择曲目后显示音符", plotLeft + plotWidth / 2, plotTop + plotHeight / 2);
    ctx.textAlign = "start";
  }
}

function ensureStaticChrome(w, h, plotTop, plotLeft, plotWidth) {
  const cacheKey = `${w}|${h}|${currentDpr}|${minPitch}|${maxPitch}|${keyWidth}|${rulerHeight}|${accentColor}|${mutedColor}|${borderColor}|${viewportBgColor}`;
  if (cacheKey === chromeCacheKey && chromeCanvas) return;

  if (!chromeCanvas) {
    chromeCanvas = document.createElement("canvas");
    chromeCtx = chromeCanvas.getContext("2d", { alpha: true, desynchronized: true });
  }
  chromeCanvas.width = Math.max(1, Math.round(w * currentDpr));
  chromeCanvas.height = Math.max(1, Math.round(h * currentDpr));
  chromeCtx.setTransform(currentDpr, 0, 0, currentDpr, 0, 0);
  const target = ctx;
  ctx = chromeCtx;
  drawStaticPianoChrome(w, h, plotTop, plotLeft, plotWidth);
  ctx = target;
  chromeCacheKey = cacheKey;
}

function drawStaticPianoChrome(w, h, plotTop, plotLeft, plotWidth) {
  ctx.fillStyle = viewportBgColor;
  ctx.fillRect(0, 0, w, h);

  const plotHeight = h - plotTop;
  ctx.fillStyle = "rgba(99, 102, 241, 0.045)";
  ctx.fillRect(plotLeft, plotTop, plotWidth, plotHeight);

  // Pitch separators and octave bands.
  ctx.lineWidth = 1;
  ctx.strokeStyle = borderColor;
  ctx.beginPath();
  for (let i = 0; i <= pitchSpan; i++) {
    const y = Math.round(h - plotHeight * i / pitchSpan) + 0.5;
    ctx.moveTo(plotLeft, y);
    ctx.lineTo(w, y);
  }
  ctx.stroke();
  if (gridYs.length) {
    ctx.strokeStyle = "rgba(148, 163, 184, 0.24)";
    ctx.beginPath();
    for (let i = 0; i < gridYs.length; i++) {
      ctx.moveTo(plotLeft, gridYs[i]);
      ctx.lineTo(w, gridYs[i]);
    }
    ctx.stroke();
  }

  // Piano keys: white/black key lanes and compact octave labels.
  for (let i = 0; i < pitchSpan; i++) {
    const pitch = minPitch + i;
    const y = h - plotHeight * (i + 1) / pitchSpan;
    const cellH = plotHeight / pitchSpan;
    const isBlack = BLACK_PITCH_CLASSES.has(pitch % 12);
    ctx.fillStyle = isBlack ? "rgba(15, 18, 30, 0.94)" : "rgba(235, 239, 249, 0.94)";
    ctx.fillRect(0, y, keyWidth, Math.max(1, cellH));
    ctx.strokeStyle = isBlack ? "rgba(255,255,255,0.08)" : "rgba(15,23,42,0.17)";
    ctx.beginPath();
    ctx.moveTo(0, Math.round(y) + 0.5);
    ctx.lineTo(keyWidth, Math.round(y) + 0.5);
    ctx.stroke();
    if (pitch % 12 === 0 || cellH >= 13) {
      ctx.fillStyle = isBlack ? "rgba(255,255,255,0.8)" : "rgba(15,23,42,0.78)";
      ctx.font = `${pitch % 12 === 0 ? "600 " : "400 "}${Math.max(8, Math.min(10, cellH * 0.62))}px sans-serif`;
      ctx.textBaseline = "middle";
      ctx.fillText(pitch % 12 === 0 ? `C${Math.floor(pitch / 12) - 1}` : PITCH_NAMES[pitch % 12], 5, y + cellH / 2, keyWidth - 8);
    }
  }
  ctx.fillStyle = viewportBgColor;
  ctx.fillRect(0, 0, keyWidth, plotTop);
  ctx.fillRect(plotLeft, 0, plotWidth, plotTop);

  ctx.strokeStyle = borderColor;
  ctx.beginPath();
  ctx.moveTo(plotLeft + 0.5, 0);
  ctx.lineTo(plotLeft + 0.5, h);
  ctx.moveTo(0, plotTop + 0.5);
  ctx.lineTo(w, plotTop + 0.5);
  ctx.stroke();
}

function drawTimeRuler(curUs, w, h, plotTop, plotLeft) {
  // Real-time ruler in seconds; no tempo or beat assumptions are made.
  const firstSec = Math.floor(curUs / 1_000_000 - headX / pxPerUs / 1_000_000);
  const lastSec = Math.ceil(curUs / 1_000_000 + (w - headX) / pxPerUs / 1_000_000);
  ctx.font = "10px sans-serif";
  ctx.textBaseline = "middle";
  for (let sec = firstSec; sec <= lastSec; sec++) {
    if (sec < 0) continue;
    const x = headX + (sec * 1_000_000 - curUs) * pxPerUs;
    if (x < plotLeft || x > w) continue;
    ctx.strokeStyle = "rgba(148, 163, 184, 0.3)";
    ctx.beginPath();
    ctx.moveTo(Math.round(x) + 0.5, plotTop);
    ctx.lineTo(Math.round(x) + 0.5, h);
    ctx.stroke();
    ctx.fillStyle = mutedColor;
    ctx.fillText(formatRulerSecond(sec), x + 4, plotTop / 2, 42);
  }
  for (let half = firstSec * 2; half <= lastSec * 2; half++) {
    if (half % 2 === 0 || half < 0) continue;
    const x = headX + (half * 500_000 - curUs) * pxPerUs;
    if (x < plotLeft || x > w) continue;
    ctx.strokeStyle = "rgba(148, 163, 184, 0.11)";
    ctx.beginPath();
    ctx.moveTo(Math.round(x) + 0.5, plotTop);
    ctx.lineTo(Math.round(x) + 0.5, h);
    ctx.stroke();
  }

}

function syncThemeColors() {
  const theme = getComputedStyle(document.documentElement);
  accentColor = theme.getPropertyValue("--accent").trim() || accentColor;
  mutedColor = theme.getPropertyValue("--text-muted").trim() || theme.getPropertyValue("--text-secondary").trim() || mutedColor;
  borderColor = theme.getPropertyValue("--border").trim() || GRID_FALLBACK;
  const viewport = canvas?.parentElement;
  const background = viewport ? getComputedStyle(viewport).backgroundColor : "";
  if (background && background !== "rgba(0, 0, 0, 0)") viewportBgColor = background;
}

function drawRectBatch(buf, style) {
  if (buf.length === 0) return;

  ctx.fillStyle = style;
  ctx.beginPath();
  for (let i = 0; i < buf.length; i += 4) {
    ctx.rect(buf[i], buf[i + 1], buf[i + 2], buf[i + 3]);
  }
  ctx.fill();
}

function drawOrderedRectRuns(buf) {
  if (buf.length === 0) return;

  let bucket = -1;
  let hasRects = false;
  ctx.beginPath();
  for (let i = 0; i < buf.length; i += 5) {
    const nextBucket = buf[i];
    if (nextBucket !== bucket) {
      if (hasRects) ctx.fill();
      bucket = nextBucket;
      ctx.fillStyle = accentColor;
      ctx.globalAlpha = NORMAL_ALPHA[bucket] ?? 0.7;
      ctx.beginPath();
      hasRects = false;
    }
    ctx.rect(buf[i + 1], buf[i + 2], buf[i + 3], buf[i + 4]);
    hasRects = true;
  }
  if (hasRects) ctx.fill();
  ctx.globalAlpha = 1;
}

function formatRulerSecond(totalSeconds) {
  const safe = Math.max(0, totalSeconds);
  const mins = Math.floor(safe / 60);
  const secs = String(safe % 60).padStart(2, "0");
  return `${mins}:${secs}`;
}

function formatUsToClock(us) {
  const totalMs = Math.floor(Math.max(0, us) / 1000);
  const mins = String(Math.floor(totalMs / 60000)).padStart(2, "0");
  const secs = String(Math.floor((totalMs % 60000) / 1000)).padStart(2, "0");
  const ms = String(totalMs % 1000).padStart(3, "0");
  return `${mins}:${secs}.${ms}`;
}
