import { invoke } from "./bridge.js";
import { showToast } from "./app.js";
import { renderPianoKeyboard, updateActivePianoKeys } from "./piano-keyboard.js";

let profileState = null;
let songList = [];
let selectedSongId = "";
let pianoPlaybackState = null;
let gtpPianoSong = null;
let pendingActiveNotes = [];
let keyboardUpdateFrame = 0;
let lastDockStateSignature = "";

export function initGamePiano() {
  document.getElementById("piano-song-select")?.addEventListener("change", (event) => {
    selectedSongId = event.target.value || "";
    renderSongCaption();
    publishPianoDockState();
  });

  document.getElementById("btn-piano-import-midi")?.addEventListener("click", () => {
    window.dispatchEvent(new CustomEvent("nte:open-piano-import"));
  });
  document.getElementById("btn-piano-recommended")?.addEventListener("click", () => {
    document.querySelector('.nav-item[data-page="recommended-scores"]')?.click();
    const filter = document.getElementById("recommended-instrument-filter");
    if (filter) {
      filter.value = "piano";
      filter.dispatchEvent(new Event("change", { bubbles: true }));
    }
  });

  document.getElementById("piano-speed")?.addEventListener("input", (event) => {
    const value = Number(event.target.value) || 100;
    setText("piano-speed-value", `${value}%`);
  });
  document.getElementById("piano-layout")?.addEventListener("change", async (event) => {
    const picker = event.currentTarget;
    try {
      const result = await invoke("piano.setLayout", { layoutKind: picker.value });
      profileState = { ...profileState, activeProfileId: result.profile.id, activeProfile: result.profile };
      renderPianoKeyboard(document.getElementById("piano-keyboard"), picker.value);
      publishPianoDockState();
      showToast(`已切换到${picker.value === "SevenKeyScale" ? "21 键" : "36 键"}钢琴`, "success");
    } catch (error) {
      picker.value = getLayoutKind(profileState?.activeProfile);
      renderPianoKeyboard(document.getElementById("piano-keyboard"), picker.value);
      showToast(`切换钢琴布局失败：${error.message}`, "danger");
    }
  });

  window.addEventListener("nte:select-piano-song", (event) => {
    const songId = event.detail?.songId;
    if (songId) selectSong(songId);
  });
  window.addEventListener("nte:gtp-piano-song", (event) => {
    gtpPianoSong = event.detail || null;
  });
  window.addEventListener("nte:piano-song-imported", (event) => {
    const songId = event.detail?.id;
    if (songId) void refreshState(songId);
  });
  window.addEventListener("nte:reload-library", () => void refreshState());
  window.addEventListener("nte:piano-dock-command", (event) => {
    const action = event.detail?.action;
    const isGtpPage = document.getElementById("page-gtp")?.classList.contains("active") === true;
    if (isGtpPage && gtpPianoSong?.id) {
      if (action === "stop") return void control("piano.stop");
      if (action !== "toggle") return;
      if (["playing", "countdown"].includes(pianoPlaybackState?.status)) void control("piano.pause");
      else if (pianoPlaybackState?.status === "paused") void control("piano.resume");
      else void invoke("gtp.play", { id: gtpPianoSong.id }).catch((error) => showToast(`启动 GTP 游戏钢琴失败：${error.message}`, "danger"));
      return;
    }
    if (action === "stop") return void control("piano.stop");
    if (action !== "toggle") return;
    if (["playing", "countdown"].includes(pianoPlaybackState?.status)) void control("piano.pause");
    else if (pianoPlaybackState?.status === "paused") void control("piano.resume");
    else void startPlayback();
  });
  window.addEventListener("nte:piano-playback-state", (event) => renderPlayback(event.detail));
  window.addEventListener("nte:page-changed", (event) => {
    if (event.detail?.pageId === "game-piano") void refreshState();
    else if (event.detail?.pageId === "gtp") void refreshState();
    else if (["playing", "countdown"].includes(pianoPlaybackState?.status)) void control("piano.pause");
  });

  setText("piano-speed-value", `${document.getElementById("piano-speed")?.value || 100}%`);
  void refreshState();
}

async function refreshState(preferredSongId = "") {
  try {
    const [state, library] = await Promise.all([
      invoke("piano.getState"),
      invoke("library.list", { filter: "piano", query: "" })
    ]);
    profileState = state.profile;
    const layoutPicker = document.getElementById("piano-layout");
    if (layoutPicker) layoutPicker.value = getLayoutKind(profileState?.activeProfile);
    renderPianoKeyboard(document.getElementById("piano-keyboard"), getLayoutKind(profileState?.activeProfile));
    pianoPlaybackState = state.playback;
    songList = library.items || [];
    const candidate = preferredSongId || selectedSongId;
    selectedSongId = songList.some((song) => song.id === candidate)
      ? candidate
      : songList[0]?.id || "";
    renderSongs();
    renderPlayback({ state: state.playback });
  } catch (error) {
    showToast(`游戏钢琴初始化失败：${error.message}`, "danger");
  }
}

function renderSongs() {
  const select = document.getElementById("piano-song-select");
  if (!select) return;
  select.replaceChildren();
  if (!songList.length) {
    const empty = document.createElement("option");
    empty.value = "";
    empty.textContent = "还没有曲目，上传一份 MIDI 开始吧";
    select.append(empty);
    selectedSongId = "";
  } else {
    songList.forEach((song) => {
      const option = document.createElement("option");
      option.value = song.id;
      option.textContent = song.name;
      option.selected = song.id === selectedSongId;
      select.append(option);
    });
  }
  select.value = selectedSongId;
  renderSongCaption();
  publishPianoDockState();
}

function selectSong(songId) {
  if (!songList.some((song) => song.id === songId)) {
    void refreshState(songId);
    return;
  }
  selectedSongId = songId;
  const select = document.getElementById("piano-song-select");
  if (select) select.value = songId;
  renderSongCaption();
  publishPianoDockState();
}

function renderSongCaption() {
  const song = songList.find((item) => item.id === selectedSongId);
  setText("piano-song-caption", song
    ? `${song.durationFormatted || "MIDI"} · ${Number(song.noteCount || 0).toLocaleString()} 个音符 · 自动转换为游戏键位`
    : "上传 MIDI 后会自动加入 Piano 曲库");
}

function readOptions() {
  return {
    profileId: profileState?.activeProfileId || profileState?.activeProfile?.id || "game-piano-twelve",
    transposeSemitones: 0,
    speedPercent: Number(document.getElementById("piano-speed")?.value) || 100,
    filterDrumChannel10: true,
    unplayableNotePolicy: "AutoTransposeMaxCoverage"
  };
}

async function startPlayback() {
  if (!selectedSongId) {
    showToast("先上传或选择一首 MIDI 曲目。", "info");
    return;
  }
  try {
    const result = await invoke("piano.start", { songId: selectedSongId, ...readOptions() });
    renderPlayback({ state: result });
  } catch (error) {
    showToast(`无法开始游戏钢琴：${error.message}`, "danger");
  }
}

async function control(action) {
  try {
    const state = await invoke(action);
    renderPlayback({ state });
  } catch (error) {
    showToast(`钢琴控制失败：${error.message}`, "danger");
  }
}

function renderPlayback(event) {
  const state = event?.state || event;
  if (!state) return;
  pianoPlaybackState = state;
  window.ntePianoPlaybackState = state;
  pendingActiveNotes = event?.activeNotes || [];
  if (!keyboardUpdateFrame) {
    keyboardUpdateFrame = requestAnimationFrame(() => {
      keyboardUpdateFrame = 0;
      const page = document.getElementById("page-game-piano");
      if (!page || page.classList.contains("active")) {
        updateActivePianoKeys(document.getElementById("piano-keyboard"), pendingActiveNotes);
      }
    });
  }
  const noteLine = document.getElementById("piano-note-line");
  if (noteLine) {
    const text = state.status === "countdown" ? `请切回游戏窗口 · ${state.countdownSeconds || 3} 秒后开始演奏` :
      state.status === "playing" ? "正在演奏 · MIDI 已自动适配" :
      state.status === "paused" ? `已暂停 · 点击底部播放或按 ${window.nteStartHotkeyLabel || "F8"} 继续` :
      event?.message || "钢琴谱自动编译 · 音域自动适配 · 和弦自动处理";
    if (noteLine.lastElementChild?.textContent !== text) noteLine.lastElementChild.textContent = text;
  }
  const dockSignature = `${state.status}|${state.countdownSeconds || 0}|${event?.message || ""}`;
  if (dockSignature !== lastDockStateSignature) {
    lastDockStateSignature = dockSignature;
    publishPianoDockState(event?.message);
  }
}

function publishPianoDockState(message = null) {
  const isGtpPage = document.getElementById("page-gtp")?.classList.contains("active") === true;
  window.dispatchEvent(new CustomEvent("nte:piano-dock-state", {
    detail: {
      song: isGtpPage && gtpPianoSong
        ? gtpPianoSong
        : songList.find((item) => item.id === selectedSongId) || null,
      state: pianoPlaybackState,
      hasPreview: false,
      canStart: isGtpPage && gtpPianoSong ? true : !!selectedSongId,
      blockReason: "",
      message
    }
  }));
}

function setText(id, value) {
  const element = document.getElementById(id);
  if (element) element.textContent = value ?? "";
}

function getLayoutKind(profile) {
  if (profile?.kind === "SevenKeyScale" || profile?.kind === 0 || profile?.id === "game-piano-seven") return "SevenKeyScale";
  return "TwelveKeyChromatic";
}
