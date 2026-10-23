// wwwroot/js/playback.js
import { invoke } from "./bridge.js";
import { showToast } from "./app.js";
import { initMidiRoll, setMidiRollNotes, updateMidiRollPlayback } from "./midi-roll.js";
import { setSimPage } from "./guitar-simulator.js";

const startHotkeyLabel = () => window.nteStartHotkeyLabel || "F8";
const DOCK_COVERS = Array.from({ length: 19 }, (_, index) =>
  `assets/player-covers/cover-${String(index + 1).padStart(2, "0")}.webp`
);

let isScrubbing = false;
let currentSpeedValue = "1";
let currentAdvicePage = "P1";
let progressRafId = 0;
let progressAnchorMs = 0;
let progressAnchorPerfMs = 0;
let progressDurationMs = 0;
let progressSpeed = 1;
let progressAdvancing = false;

let elBtnPlay = null;
let elBtnPause = null;
let elBtnStop = null;
let elBtnAudition = null;
let elTitle = null;
let elMeta = null;
let elCover = null;
let elCoverControl = null;
let currentDockCoverSongKey = null;
let currentDockCoverIndex = readDockCoverIndex();
let dockCoverLocked = readDockCoverLocked();
let elTimeCur = null;
let elTimeTot = null;
let elSlider = null;
let elBadge = null;
let elSpeedSelect = null;
let elPlayMode = null;

const PLAY_MODES = ["single", "sequence", "random", "stop"];
const PLAY_MODE_LABELS = {
  single: { text: "🔂 单曲循环", description: "单曲循环" },
  sequence: { text: "🔁 顺序播放", description: "顺序播放" },
  random: { text: "🔀 随机播放", description: "随机播放" },
  stop: { text: "⏹ 播完即停", description: "播完即停" }
};
let playMode = readPlayMode();
let handlingCompletion = false;
let auditionWasActive = false;
let isGamePianoPage = false;
let pianoDockSnapshot = null;
let gtpPianoSong = null;

function readPlayMode() {
  try {
    const saved = localStorage.getItem("nte-play-mode");
    return PLAY_MODES.includes(saved) ? saved : "sequence";
  } catch {
    return "sequence";
  }
}

function renderPlayMode() {
  if (!elPlayMode) return;
  const mode = PLAY_MODE_LABELS[playMode];
  elPlayMode.textContent = mode.text;
  elPlayMode.title = `当前：${mode.description}，点击切换播放模式`;
  elPlayMode.setAttribute("aria-label", `播放模式：${mode.description}`);
}

function readDockCoverIndex() {
  try {
    const saved = localStorage.getItem("nte-dock-cover-index");
    if (saved === null) return -1;
    const index = Number(saved);
    return Number.isInteger(index) && index >= 0 && index < DOCK_COVERS.length ? index : -1;
  } catch {
    return -1;
  }
}

function readDockCoverLocked() {
  try { return localStorage.getItem("nte-dock-cover-locked") === "true"; }
  catch { return false; }
}

function initDockCoverControl() {
  if (!elCoverControl) return;
  const render = () => {
    elCoverControl.classList.toggle("is-locked", dockCoverLocked);
    elCoverControl.setAttribute("aria-pressed", String(dockCoverLocked));
    elCoverControl.setAttribute("aria-label", dockCoverLocked ? "头像已锁定，点击解锁" : "点击锁定头像");
    elCoverControl.title = dockCoverLocked ? "头像已锁定，点击解锁" : "点击锁定头像";
  };
  render();
  elCoverControl.addEventListener("click", () => {
    dockCoverLocked = !dockCoverLocked;
    try { localStorage.setItem("nte-dock-cover-locked", String(dockCoverLocked)); } catch { /* Storage may be unavailable. */ }
    render();
  });
}

function updateDockCover(song) {
  if (!elCover) return;

  const songKey = song ? `${song.instrument ?? ""}:${song.id ?? song.name ?? ""}` : "";
  if (songKey === currentDockCoverSongKey && elCover.getAttribute("src")) return;

  currentDockCoverSongKey = songKey;
  if (dockCoverLocked && currentDockCoverIndex >= 0) {
    elCover.src = DOCK_COVERS[currentDockCoverIndex];
    return;
  }
  if (DOCK_COVERS.length > 1) {
    let nextIndex = Math.floor(Math.random() * DOCK_COVERS.length);
    if (nextIndex === currentDockCoverIndex) nextIndex = (nextIndex + 1) % DOCK_COVERS.length;
    currentDockCoverIndex = nextIndex;
  } else {
    currentDockCoverIndex = 0;
  }
  elCover.src = DOCK_COVERS[currentDockCoverIndex];
  try { localStorage.setItem("nte-dock-cover-index", String(currentDockCoverIndex)); } catch { /* Storage may be unavailable. */ }
}

function selectRandomPlaybackCandidates(songs, currentSong) {
  if (!Array.isArray(songs) || !currentSong?.id) return [];

  const instrumentKey = currentSong.instrument === "吉他" || currentSong.instrument === "guitar"
    ? "guitar"
    : currentSong.instrument === "键盘钢琴" || currentSong.instrument === "keyboard"
      ? "keyboard"
      : null;
  if (!instrumentKey) return [];

  return songs.filter((song) => song.id !== currentSong.id && song.instrument === instrumentKey);
}

async function handlePlaybackCompleted(state) {
  if (handlingCompletion || state?.isAudition || state?.song?.sourceFormat === "GTP" || !state?.song?.id) return;
  handlingCompletion = true;

  try {
    if (playMode === "stop") return;

    if (playMode === "single") {
      await invoke("playback.load", { id: state.song.id });
      await invoke("playback.play");
      return;
    }

    const library = await invoke("library.list", { filter: "all", query: "" });
    const songs = Array.isArray(library?.items) ? library.items : [];
    if (songs.length === 0) return;

    let nextIndex = -1;
    const currentIndex = songs.findIndex((song) => song.id === state.song.id);
    if (playMode === "random") {
      const candidates = selectRandomPlaybackCandidates(songs, state.song);
      if (candidates.length > 0) {
        const nextSong = candidates[Math.floor(Math.random() * candidates.length)];
        await invoke("playback.load", { id: nextSong.id });
        await invoke("playback.play");
      }
      return;
    }

    if (currentIndex >= 0 && currentIndex < songs.length - 1) nextIndex = currentIndex + 1;
    if (nextIndex >= 0) {
      await invoke("playback.load", { id: songs[nextIndex].id });
      await invoke("playback.play");
    }
  } catch (error) {
    showToast(`自动切换曲目失败：${error.message}`, "danger");
  }
}

export function initPlayback() {
  initMidiRoll();

  elBtnPlay = document.getElementById("dock-btn-play");
  elBtnPause = document.getElementById("dock-btn-pause");
  elBtnStop = document.getElementById("dock-btn-stop");
  elBtnAudition = document.getElementById("dock-btn-audition");
  elTitle = document.getElementById("dock-song-title");
  elMeta = document.getElementById("dock-song-meta");
  elCover = document.getElementById("dock-cover");
  elCoverControl = document.getElementById("dock-cover-control");
  initDockCoverControl();
  updateDockCover(null);
  elTimeCur = document.getElementById("dock-time-cur");
  elTimeTot = document.getElementById("dock-time-tot");
  elSlider = document.getElementById("dock-slider");
  elBadge = document.getElementById("dock-status-badge");
  elSpeedSelect = document.getElementById("dock-speed-select");
  if (elSpeedSelect) {
    elSpeedSelect.replaceChildren();
    elSpeedSelect.add(new Option("自动配速", "auto"));
    for (let percent = 25; percent <= 200; percent += 5) {
      elSpeedSelect.add(new Option(`${(percent / 100).toFixed(2)}×`, String(percent / 100)));
    }
    elSpeedSelect.value = "1";
  }
  elPlayMode = document.getElementById("dock-btn-play-mode");
  renderPlayMode();

  elPlayMode?.addEventListener("click", () => {
    playMode = PLAY_MODES[(PLAY_MODES.indexOf(playMode) + 1) % PLAY_MODES.length];
    try { localStorage.setItem("nte-play-mode", playMode); } catch { /* Storage may be unavailable in restricted WebView contexts. */ }
    renderPlayMode();
    showToast(`播放模式：${PLAY_MODE_LABELS[playMode].description}`, "info");
  });

  // Single unified Play/Pause toggle button: clicking start icon again will pause immediately!
  elBtnPlay?.addEventListener("click", () => {
    if (isGamePianoPage) {
      window.dispatchEvent(new CustomEvent("nte:piano-dock-command", { detail: { action: "toggle" } }));
      return;
    }
    invoke("playback.togglePlay");
  });

  elBtnPause?.addEventListener("click", () => {
    invoke("playback.pause");
  });

  elBtnStop?.addEventListener("click", () => {
    if (isGamePianoPage) {
      window.dispatchEvent(new CustomEvent("nte:piano-dock-command", { detail: { action: "stop" } }));
      return;
    }
    invoke("playback.stop");
  });

  elBtnAudition?.addEventListener("click", () => {
    invoke("playback.audition");
  });

  elSlider?.addEventListener("mousedown", () => {
    isScrubbing = true;
  });
  elSlider?.addEventListener("pointerdown", () => {
    isScrubbing = true;
  });

  elSlider?.addEventListener("input", (e) => {
    isScrubbing = true;
    const targetMs = parseInt(e.target.value, 10);
    const sec = Math.floor(targetMs / 1000);
    const m = Math.floor(sec / 60).toString().padStart(2, "0");
    const s = (sec % 60).toString().padStart(2, "0");
    if (elTimeCur) elTimeCur.textContent = `${m}:${s}`;
  });

  // Unified Seek: whether auditioning or game playing, scrubs the same timeline
  elSlider?.addEventListener("change", (e) => {
    isScrubbing = false;
    if (isGamePianoPage) return;
    const targetMs = parseInt(e.target.value, 10);
    invoke("playback.seek", { positionMs: targetMs });
  });
  elSlider?.addEventListener("pointerup", () => {
    // Some WebView range controls do not emit change when released without moving.
    isScrubbing = false;
  });
  elSlider?.addEventListener("pointercancel", () => {
    isScrubbing = false;
  });

  // 变速选择事件（后端确认成功才更新，若存在可达性风险自动弹出调优建议）
  elSpeedSelect?.addEventListener("change", async () => {
    const selected = elSpeedSelect.value;
    const multiplier = parseFloat(selected);
    elSpeedSelect.disabled = true;
    try {
      const result = selected === "auto"
        ? await invoke("playback.autoSpeed")
        : await invoke("playback.setSpeed", { multiplier });
      currentSpeedValue = selected;
      if (result && result.isFullyReachable === false) {
        showToast(
          `⚠️ 速度已设为 ${multiplier.toFixed(2)}×，但检测到 ${result.issueCount} 处翻页/选槽过快风险：${result.firstIssueSummary}`,
          "warning"
        );
      } else {
        showToast(selected === "auto" ? "已恢复吉他自动配速" : `⚡ 演奏速度已设为 ${multiplier.toFixed(2)}×（动作时间轴已同步重编译）`, "success");
      }
    } catch (err) {
      elSpeedSelect.value = currentSpeedValue;
      showToast(`修改速度失败: ${err.message}`, "danger");
    } finally {
      elSpeedSelect.disabled = false;
    }
  });

  // 监听后端播放状态与 MIDI 音轨快照事件
  window.addEventListener("nte:playback-state", (e) => {
    renderPlaybackState(e.detail);
  });

  window.addEventListener("nte:playback-start-confirmation-required", (e) => {
    const message = e.detail?.message;
    if (message) showToast(message, "info", 12000);
  });

  window.addEventListener("nte:piano-dock-state", (event) => {
    pianoDockSnapshot = event.detail || null;
    if (isGamePianoPage) renderPianoDockState(pianoDockSnapshot);
  });
  window.addEventListener("nte:gtp-piano-song", (event) => {
    gtpPianoSong = event.detail || null;
    const isGtpPage = document.getElementById("page-gtp")?.classList.contains("active") === true;
    if (!isGtpPage) return;
    isGamePianoPage = !!gtpPianoSong;
    if (!isGamePianoPage) {
      restoreGeneralDockControls();
      invoke("playback.getState").then(renderPlaybackState).catch(() => {});
      return;
    }
    pianoDockSnapshot = { song: gtpPianoSong, state: pianoDockSnapshot?.state || {} };
    invoke("piano.getState").then((result) => {
      pianoDockSnapshot = { ...pianoDockSnapshot, state: result?.playback };
      renderPianoDockState(pianoDockSnapshot);
    }).catch(() => renderPianoDockState(pianoDockSnapshot));
  });
  window.addEventListener("nte:page-changed", (event) => {
    const pageId = event.detail?.pageId;
    isGamePianoPage = pageId === "game-piano" || (pageId === "gtp" && !!gtpPianoSong);
    if (isGamePianoPage) {
      if (pianoDockSnapshot) renderPianoDockState(pianoDockSnapshot);
      invoke("piano.getState").then((result) => {
        pianoDockSnapshot = { ...(pianoDockSnapshot || {}), state: result?.playback };
        renderPianoDockState(pianoDockSnapshot);
      }).catch(() => {});
    } else {
      restoreGeneralDockControls();
      invoke("playback.getState").then(renderPlaybackState).catch(() => {});
    }
  });

  window.addEventListener("nte:midi-roll-loaded", (e) => {
    const payload = e.detail || {};
    setMidiRollNotes(payload.notes || [], payload.modeLabel || "实时音轨");
  });

  window.addEventListener("nte:piano-playback-state", (event) => {
    const state = event.detail?.state || event.detail;
    if (!state) return;
    updateMidiRollPlayback({
      positionUs: state.currentUs,
      speed: 1,
      playing: state.status === "playing"
    });
  });

  // 绑定吉他指引中的起始页徽章点击事件，直接对齐下方模拟器轮盘
  const pageBadge = document.getElementById("guitar-hint-page-badge");
  pageBadge?.addEventListener("click", () => {
    if (currentAdvicePage) {
      setSimPage(currentAdvicePage);
      showToast(`🎯 已将模拟轮盘切换至推荐起始页: ${currentAdvicePage}`, "info");
    }
  });

  // Initial state fetch
  invoke("playback.getState").then(renderPlaybackState).catch(() => {});
}

export function renderPlaybackState(state) {
  if (!state) return;
  if (isGamePianoPage) return;

  if (state.isAudition || state.state === "auditioning") auditionWasActive = true;
  if (state.state === "playing") handlingCompletion = false;
  if (state.state === "completed") {
    if (auditionWasActive) auditionWasActive = false;
    else void handlePlaybackCompleted(state);
  }
  if (state.state === "stopped") auditionWasActive = false;

  const speed = Number(state.speed || 1.0);

  if (state.song) {
    updateDockCover(state.song);
    if (elTitle && elTitle.textContent !== state.song.name) elTitle.textContent = state.song.name;
    if (elMeta) {
      const metaStr = `${state.song.instrument} · ${state.song.mode} · 倍速: ${speed.toFixed(2)}× · 移调: ${state.song.transpose >= 0 ? "+" : ""}${state.song.transpose} · ${state.song.noteCount} 音符`;
      if (elMeta.textContent !== metaStr) elMeta.textContent = metaStr;
    }
  } else {
    updateDockCover(null);
    const titleStr = state.isAudition ? "声卡监听试听中" : "未选择曲目";
    if (elTitle && elTitle.textContent !== titleStr) elTitle.textContent = titleStr;
    const metaStr = state.isAudition ? "电脑声卡合成器预览 (无实机按键发送)" : "请在曲库选择歌曲开始演奏";
    if (elMeta && elMeta.textContent !== metaStr) elMeta.textContent = metaStr;
  }

  const posStr = state.positionFormatted || "00:00";
  if (elTimeCur && elTimeCur.textContent !== posStr) elTimeCur.textContent = posStr;
  const totStr = state.durationFormatted || "00:00";
  if (elTimeTot && elTimeTot.textContent !== totStr) elTimeTot.textContent = totStr;

  // Shared timeline for audition and game playback. Animate between backend clock
  // snapshots so the slider advances smoothly even when input events are sparse.
  const durationMs = Math.max(0, Number(state.durationMs) || 0);
  const positionMs = Math.max(0, Math.min(durationMs || Infinity, Number(state.positionMs) || 0));
  if (elSlider && !isScrubbing) {
    const maxVal = durationMs || 100;
    if (elSlider.max !== String(maxVal)) elSlider.max = maxVal;
    if (elSlider.value !== String(positionMs)) elSlider.value = positionMs;
  }
  syncProgressAnimation(positionMs, durationMs, speed, state.state === "playing" || state.state === "auditioning");

  // 播放或倒计时期间锁定变速下拉框，防止运行中队列冲突
  if (elSpeedSelect) {
    const isBusy = state.state === "playing" || state.state === "countdown";
    if (elSpeedSelect.disabled !== isBusy) elSpeedSelect.disabled = isBusy;
    if (state.speed) {
      currentSpeedValue = state.isAutoSpeed ? "auto" : String(state.speed);
      const autoOption = elSpeedSelect.querySelector('option[value="auto"]');
      if (autoOption) autoOption.textContent = `自动配速 · ${state.autoRecommendedSpeedPercent || 100}%`;
      if (currentSpeedValue !== "auto" && !Array.from(elSpeedSelect.options).some(o => o.value === currentSpeedValue)) {
        elSpeedSelect.add(new Option(`${(Number(state.speed)).toFixed(2)}×`, currentSpeedValue));
      }
      if (elSpeedSelect.value !== currentSpeedValue) elSpeedSelect.value = currentSpeedValue;
    }
  }

  // 同步驱动 MIDI 下落可视化画布
  const posUs = state.positionUs !== undefined ? state.positionUs : (Number(state.positionMs || 0) * 1000);
  updateMidiRollPlayback({
    positionUs: posUs,
    speed,
    playing: state.state === "playing" || state.state === "auditioning"
  });

  const isAudition = !!state.isAudition;
  const isPlayingOrCountdown = state.state === "playing" || state.state === "countdown" || state.state === "auditioning";
  const isPaused = state.state === "paused";

  // Update Hero Button: icon dynamically switches between ▶ and ⏸
  if (elBtnPlay) {
    if (isPlayingOrCountdown) {
      if (elBtnPlay.innerHTML !== "⏸") elBtnPlay.innerHTML = "⏸";
      elBtnPlay.title = isAudition ? "暂停监听" : `暂停演奏 (${startHotkeyLabel()})`;
      elBtnPlay.classList.add("btn-primary");
    } else if (isPaused) {
      if (elBtnPlay.innerHTML !== "▶") elBtnPlay.innerHTML = "▶";
      elBtnPlay.title = isAudition ? "继续监听" : `恢复演奏 (${startHotkeyLabel()})`;
      elBtnPlay.classList.add("btn-primary");
    } else {
      if (elBtnPlay.innerHTML !== "▶") elBtnPlay.innerHTML = "▶";
      elBtnPlay.title = `开始演奏 (${startHotkeyLabel()})`;
      elBtnPlay.classList.add("btn-primary");
    }
    elBtnPlay.style.display = "inline-flex";
  }

  if (elBtnPause) {
    elBtnPause.style.display = "none";
  }

  // Update Audition Button state
  if (elBtnAudition) {
    if (isAudition) {
      elBtnAudition.textContent = "⏹ 结束监听";
      elBtnAudition.title = "结束监听并回到开头";
      elBtnAudition.classList.add("btn-danger");
      elBtnAudition.classList.remove("btn-primary");
    } else {
      elBtnAudition.textContent = "🎧 电脑监听";
      elBtnAudition.title = "开始电脑监听";
      elBtnAudition.classList.remove("btn-danger");
    }
  }

  // Update Status Badge
  if (elBadge) {
    elBadge.className = "badge";
    const textEl = elBadge.querySelector(".badge-text");

    switch (state.state) {
      case "playing":
        elBadge.classList.add("success");
        if (textEl) textEl.textContent = `正在演奏 (${speed.toFixed(2)}×)... [点击 ⏸ 或按 ${startHotkeyLabel()} 暂停]`;
        break;
      case "countdown":
        elBadge.classList.add("warning");
        if (textEl) textEl.textContent = state.message ||
          (state.countdownSeconds <= 2
            ? `正在校准吉他界面 · ${state.countdownSeconds}s 后开始`
            : `准备中: 倒计时 ${state.countdownSeconds}s 切回游戏`);
        break;
      case "paused":
        elBadge.classList.add("warning");
        if (textEl) textEl.textContent = isAudition ? `监听已暂停 (${speed.toFixed(2)}× · 点击 ▶ 继续)` : `演奏已暂停 (${speed.toFixed(2)}× · 点击 ▶ 或按 ${startHotkeyLabel()} 恢复)`;
        break;
      case "auditioning":
        elBadge.classList.add("info");
        if (textEl) textEl.textContent = `正在声卡监听 (${speed.toFixed(2)}× · 无游戏按键发送)`;
        break;
      default:
        elBadge.classList.add("info");
        if (textEl) textEl.textContent = state.song ? `就绪 (${speed.toFixed(2)}×) | 点击 ▶ 或按 ${startHotkeyLabel()} 开弹` : "等待选曲";
        break;
    }
  }

  // Update Guitar Playback Advice Banner (Now inside Guitar Simulator page)
  updateGuitarHint(state);
}

function renderPianoDockState(snapshot) {
  if (!snapshot) return;
  const song = snapshot.song;
  updateDockCover(song);
  const state = snapshot.state || {};
  const status = state.status || "stopped";
  const durationUs = Math.max(0, Number(state.totalUs) || 0);
  const positionUs = Math.max(0, Math.min(durationUs || Infinity, Number(state.currentUs) || 0));
  const durationMs = Math.round(durationUs / 1000);
  const positionMs = Math.round(positionUs / 1000);

  updateMidiRollPlayback({
    positionUs,
    speed: 1,
    playing: status === "playing"
  });

  if (elTitle) elTitle.textContent = song?.name || "未选择 Piano 曲目";
  if (elMeta) elMeta.textContent = song
    ? `Piano · 游戏钢琴 · ${Number(song.noteCount || 0).toLocaleString()} 音符`
    : "选择 Piano 曲目后，点击下方 ▶ 自动编译并演奏";
  if (elTimeCur) elTimeCur.textContent = formatDockTime(positionMs);
  if (elTimeTot) elTimeTot.textContent = formatDockTime(durationMs);
  if (elSlider) {
    elSlider.disabled = true;
    elSlider.title = "游戏钢琴播放进度";
    elSlider.max = String(Math.max(100, durationMs));
    elSlider.value = String(positionMs);
  }
  syncProgressAnimation(positionMs, durationMs, 1, status === "playing");

  if (elBtnPlay) {
    const playing = status === "playing" || status === "countdown";
    elBtnPlay.innerHTML = playing ? "⏸" : "▶";
    elBtnPlay.title = playing ? "暂停游戏钢琴" : status === "paused" ? "继续游戏钢琴" : "开始游戏钢琴（先等待 3 秒切回游戏）";
    elBtnPlay.disabled = !song;
  }
  if (elBtnStop) elBtnStop.disabled = !["playing", "paused", "countdown"].includes(status);
  if (elBtnAudition) elBtnAudition.style.display = "none";
  if (elPlayMode) elPlayMode.style.display = "none";
  const speedBox = elSpeedSelect?.closest(".speed-pill-box");
  if (speedBox) speedBox.style.display = "none";
  if (elBadge) {
    elBadge.className = "badge";
    const text = elBadge.querySelector(".badge-text");
    if (status === "countdown") {
      elBadge.classList.add("warning");
      if (text) text.textContent = state.message ||
        (state.countdownSeconds <= 2
          ? `正在校准吉他界面 · ${state.countdownSeconds || 1} 秒后开始演奏`
          : `请切回游戏窗口 · ${state.countdownSeconds || 3} 秒后开始演奏`);
    } else if (status === "playing") {
      elBadge.classList.add("success");
      if (text) text.textContent = "游戏钢琴正在演奏 · 下方 ▶ 可暂停";
    } else if (status === "paused") {
      elBadge.classList.add("warning");
      if (text) text.textContent = "游戏钢琴已暂停 · 下方 ▶ 可继续";
    } else {
      elBadge.classList.add(snapshot.blockReason ? "warning" : "info");
      if (text) text.textContent = !song
        ? "等待选择 Piano 曲目"
        : snapshot.message || snapshot.blockReason || `就绪 · 点击下方 ▶ 或按 ${startHotkeyLabel()} 开始演奏`;
    }
  }
  const hotkey = document.getElementById("footer-global-hotkey");
  if (hotkey) hotkey.textContent = `游戏钢琴：下方 ▶ / ■ 或 ${startHotkeyLabel()} 控制`;
}

function restoreGeneralDockControls() {
  if (elSlider) {
    elSlider.disabled = false;
    elSlider.title = "";
  }
  if (elBtnPlay) elBtnPlay.disabled = false;
  if (elBtnStop) elBtnStop.disabled = false;
  if (elBtnAudition) elBtnAudition.style.display = "";
  if (elPlayMode) elPlayMode.style.display = "";
  const speedBox = elSpeedSelect?.closest(".speed-pill-box");
  if (speedBox) speedBox.style.display = "";
  const hotkey = document.getElementById("footer-global-hotkey");
  if (hotkey) hotkey.textContent = "⚡ 全局热键: F8 播放 / 暂停 / 急停";
}

function formatDockTime(milliseconds) {
  const seconds = Math.max(0, Math.floor(milliseconds / 1000));
  return `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
}

function syncProgressAnimation(positionMs, durationMs, speed, advancing) {
  progressAnchorMs = positionMs;
  progressAnchorPerfMs = performance.now();
  progressDurationMs = durationMs;
  progressSpeed = speed;
  progressAdvancing = Boolean(advancing);

  if (!progressAdvancing) {
    if (progressRafId) cancelAnimationFrame(progressRafId);
    progressRafId = 0;
    if (elSlider && !isScrubbing) elSlider.value = String(positionMs);
    return;
  }

  if (!progressRafId) progressRafId = requestAnimationFrame(renderProgressFrame);
}

function renderProgressFrame(nowPerfMs) {
  progressRafId = 0;
  if (!progressAdvancing) return;

  if (elSlider && !isScrubbing) {
    const estimate = progressAnchorMs + Math.max(0, nowPerfMs - progressAnchorPerfMs) * progressSpeed;
    const positionMs = Math.min(progressDurationMs || Infinity, estimate);
    elSlider.value = String(Math.floor(positionMs));
    if (progressDurationMs > 0 && positionMs >= progressDurationMs) {
      progressAdvancing = false;
      return;
    }
  }

  if (progressAdvancing) progressRafId = requestAnimationFrame(renderProgressFrame);
}

export function updateGuitarHint(state) {
  if (!state) return;

  const banner = document.getElementById("guitar-hint-banner") || document.getElementById("guitar-home-hint-banner");
  if (!banner) return;

  const iconEl = document.getElementById("guitar-hint-icon");
  const titleEl = document.getElementById("guitar-hint-title");
  const subEl = document.getElementById("guitar-hint-sub");
  const pageBadge = document.getElementById("guitar-hint-page-badge");
  const pageText = document.getElementById("guitar-hint-page-text");
  const modeBadge = document.getElementById("guitar-hint-mode-badge");
  const modeText = document.getElementById("guitar-hint-mode-text");
  const firstNoteEl = document.getElementById("guitar-hint-first-note");
  const keysEl = document.getElementById("guitar-hint-keys");

  if (state.song && state.song.instrument === "鼓组") {
    if (iconEl) iconEl.textContent = "🥁";
    if (titleEl) titleEl.textContent = `鼓点指引: ${state.song.name}`;
    if (pageBadge) pageBadge.style.display = "none";
    if (modeBadge) modeBadge.style.display = "inline-flex";
    if (modeText) modeText.textContent = "四键鼓点";
    if (firstNoteEl) firstNoteEl.textContent = `D/F/J/K 或 A/S/;/' · 切回游戏按 ${startHotkeyLabel()} 开始演奏`;
    return;
  }

  if (state.song && state.song.instrument === "键盘钢琴") {
    if (iconEl) iconEl.textContent = "🎹";
    if (titleEl) titleEl.textContent = `钢琴指引: ${state.song.name}`;
    if (pageBadge) pageBadge.style.display = "none";
    if (modeText) modeText.textContent = "36键键盘";
    if (firstNoteEl) firstNoteEl.textContent = `A~', Q~], 1~= 键盘全音域映射 (${startHotkeyLabel()} 开弹)`;
    return;
  }

  // Guitar mode (Mode 1 / Mode 2)
  if (iconEl) iconEl.textContent = "🎸";
  if (pageBadge) pageBadge.style.display = "inline-flex";

  if (state.advice) {
    if (titleEl) {
      titleEl.textContent = state.song ? `吉他指引: ${state.song.name}` : `吉他指引 (${state.advice.modeName || "吉他模式"})`;
    }
    const pageKey = state.advice.initialPage !== undefined ? `P${state.advice.initialPage}` : "P1";
    currentAdvicePage = pageKey;
    if (pageText) pageText.textContent = `起点: ${state.advice.initialModeName || "吉他品格模式"} ${pageKey} · 首音目标页: ${state.advice.pageName || `${pageKey} (第1页)`}`;
    if (modeText) modeText.textContent = state.advice.modeName || (state.song?.mode || "品格独奏");

    if (state.advice.firstItem) {
      if (firstNoteEl) firstNoteEl.textContent = state.advice.firstItem;
    } else if (state.advice.summary) {
      const lines = state.advice.summary.split(/\r?\n/).filter(l => l.trim().length > 0);
      const noteLine = lines.find(l => l.includes("第一音:") || l.includes("第一和弦:"));
      if (firstNoteEl && noteLine) {
        firstNoteEl.textContent = noteLine.replace("第一音:", "").replace("第一和弦:", "").trim();
      }
    } else if (firstNoteEl) {
      firstNoteEl.textContent = `已就绪，切回游戏按 ${startHotkeyLabel()} 开始演奏`;
    }
  } else if (state.song) {
    if (titleEl) titleEl.textContent = `吉他指引: ${state.song.name}`;
    if (modeText) modeText.textContent = state.song.mode || "吉他模式";
    if (pageText) pageText.textContent = "起始页: P1";
    if (firstNoteEl) firstNoteEl.textContent = `已就绪，切回游戏按 ${startHotkeyLabel()} 开始演奏`;
  } else {
    if (titleEl) titleEl.textContent = "吉他弹奏指引";
    if (pageText) pageText.textContent = "起始页: P1";
    if (modeText) modeText.textContent = `待命 (${startHotkeyLabel()})`;
    if (firstNoteEl) {
      firstNoteEl.innerHTML = `未载入 (<span style="text-decoration: underline; cursor: pointer; color: var(--accent);" id="link-goto-lib">曲库选歌</span>)`;
      document.getElementById("link-goto-lib")?.addEventListener("click", () => {
        window.app?.showPage("library");
      });
    }
  }
}
