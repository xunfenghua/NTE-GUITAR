import { invoke } from "./bridge.js";
import { setMidiRollEditorMode } from "./midi-roll.js";

const MAX_HISTORY = 80;
const KEY_WIDTH = 58;
const RULER_HEIGHT = 28;
const ROW_HEIGHT = 15;
const MIN_DURATION_US = 30_000;
const DEFAULT_DURATION_US = 500_000;
let beatUs = 500_000;
let beatsPerBar = 4;

let canvas;
let ctx;
let panel;
let canvasWrap;
let editor = null;
let width = 0;
let height = 0;
let dpr = 1;
let pxPerSecond = 70;
let viewStartUs = 0;
let topPitch = 84;
let selectedIds = new Set();
let history = [];
let historyIndex = -1;
let pointerEdit = null;
let auditionScrub = null;
let nextNoteId = 1;
let audioContext = null;
let auditionNodes = [];
let activeAudioPath = "";
let activeTranscriptionId = null;
let transcriptionCancelable = false;
let transcriptionCancelRequested = false;
let audioPreflightPending = false;
let audioFlowBusy = false;
let cudaRuntimeDownloadActive = false;
let cudaRuntimeDownloadRequestId = null;
let cudaRuntimeDownloadStatusTimer = 0;
let cudaRuntimeSupported = false;
let transcriptionStartedAt = 0;
let currentTranscriptionStep = 0;
let environmentPreparing = false;
let transcriptionLastProgressAt = 0;
let transcriptionActivityTimer = 0;
let transcriptionLogLines = [];
let transcriptionProgressValue = 0;
let playbackLocked = false;
let playbackRaf = 0;
let playbackPositionUs = 0;
let auditionFirstUs = 0;
let auditionDurationUs = 0;
let auditionOffsetUs = 0;
let auditionStartedAt = 0;
let auditionStartedOffsetUs = 0;
let auditionPlaying = false;
let auditionStarting = false;
let auditionStartToken = 0;
let auditionFrame = 0;
let auditionEndTimer = 0;
let auditionResumeAfterSeek = false;
let terminalEvents = new Map();
let terminalWaiters = new Map();

export function initMidiEditor() {
  canvas = document.getElementById("midi-editor-canvas");
  panel = document.getElementById("piano-editor-panel");
  canvasWrap = document.getElementById("midi-editor-canvas-wrap");
  if (!canvas || !panel || !canvasWrap) return;
  canvas.tabIndex = 0;
  canvas.setAttribute("role", "application");
  canvas.setAttribute("aria-label", "钢琴卷帘。空白处单击新增音符，拖动音符移动，拖动边缘调整时值，Delete 删除。");
  ctx = canvas.getContext("2d", { alpha: false, desynchronized: true });
  observeCanvas();
  bindEditorControls();
  bindAudioTranscription();
  void inspectTranscriptionEnvironment();
  window.nteMidiEditorHasUnsavedChanges = () => isDirty();
  bindLibrarySongPicker();
  window.addEventListener("nte:page-changed", (event) => {
    if (event.detail?.pageId !== "midi-editor" && editor) clearEditorSnapshot();
  });

  window.addEventListener("nte:editor-open-song", (event) => {
    void openSongFromEvent(event.detail?.songId);
  });
  window.addEventListener("nte:piano-playback-state", (event) => {
    const state = event.detail?.state || event.detail || {};
    playbackLocked = ["playing", "countdown"].includes(state.status);
    playbackPositionUs = Math.max(0, Number(state.currentUs ?? state.positionUs) || 0);
    if (pointerEdit && playbackLocked) editor.notes = pointerEdit.origin;
    if (pointerEdit && playbackLocked) pointerEdit = null;
    if (playbackLocked) { auditionResumeAfterSeek = false; stopAudition(); }
    updatePlaybackLock();
    if (editor) schedulePlaybackPositionDraw();
  });
}

function observeCanvas() {
  const observer = new ResizeObserver((entries) => {
    const rect = entries[0]?.contentRect;
    if (!rect) return;
    width = Math.max(1, rect.width);
    height = Math.max(1, rect.height);
    dpr = Math.min(window.devicePixelRatio || 1, 1.5);
    canvas.width = Math.round(width * dpr);
    canvas.height = Math.round(height * dpr);
    ctx.setTransform(dpr, 0, 0, dpr, 0, 0);
    draw();
  });
  observer.observe(canvas);
}

function bindEditorControls() {
  canvas.addEventListener("pointerdown", onPointerDown);
  canvas.addEventListener("pointermove", onPointerMove);
  canvas.addEventListener("pointerup", finishPointerEdit);
  canvas.addEventListener("pointercancel", cancelPointerEdit);
  canvas.addEventListener("dblclick", (event) => {
    const point = eventPoint(event);
    if (point.x >= KEY_WIDTH && point.y >= RULER_HEIGHT && !hitNote(point.x, point.y)) insertNote(point);
  });
  canvas.addEventListener("wheel", (event) => {
    if (!editor) return;
    event.preventDefault();
    if (event.ctrlKey || event.metaKey) {
      zoomAt(event.deltaY < 0 ? 1.12 : 1 / 1.12, eventPoint(event).x);
    } else if (event.shiftKey) {
      topPitch = clamp(topPitch + Math.sign(event.deltaY) * 4, 24, 127);
      draw();
    } else {
      viewStartUs = Math.max(0, viewStartUs + (event.deltaY + event.deltaX) * 1_000_000 / pxPerSecond);
      draw();
    }
  }, { passive: false });
  document.addEventListener("keydown", onEditorKeyDown);

  document.getElementById("midi-editor-source")?.addEventListener("change", (event) => {
    if (!confirmDiscardIfDirty()) {
      event.target.value = editor?.sourceKey || "source";
      return;
    }
    selectSource(event.target.value);
  });
  document.getElementById("midi-editor-bpm")?.addEventListener("change", applyBpmChange);
  document.getElementById("midi-editor-track")?.addEventListener("change", draw);
  document.getElementById("midi-editor-snap")?.addEventListener("change", draw);
  document.getElementById("midi-editor-time-zoom")?.addEventListener("input", (event) => {
    const next = Number(event.target.value) || 70;
    setZoom(next, width / 2);
  });
  document.getElementById("midi-editor-zoom-in")?.addEventListener("click", () => zoomAt(1.2, width / 2));
  document.getElementById("midi-editor-zoom-out")?.addEventListener("click", () => zoomAt(1 / 1.2, width / 2));
  document.getElementById("midi-editor-prev")?.addEventListener("click", () => pan(-0.75));
  document.getElementById("midi-editor-next")?.addEventListener("click", () => pan(0.75));
  document.getElementById("midi-editor-undo")?.addEventListener("click", undo);
  document.getElementById("midi-editor-redo")?.addEventListener("click", redo);
  document.getElementById("midi-editor-save")?.addEventListener("click", saveEditor);
  document.getElementById("midi-editor-export")?.addEventListener("click", exportEditor);
  document.getElementById("midi-editor-preview")?.addEventListener("click", previewEditor);
  const auditionProgress = document.getElementById("midi-editor-audition-progress");
  auditionProgress?.addEventListener("input", () => {
    if (!editor) return;
    if (auditionPlaying) {
      auditionResumeAfterSeek = true;
      stopAudition();
    }
    auditionOffsetUs = clamp(Number(auditionProgress.value) || 0, 0, auditionDurationUs);
    syncAuditionControls();
    draw();
  });
  auditionProgress?.addEventListener("change", () => {
    if (!auditionResumeAfterSeek) return;
    auditionResumeAfterSeek = false;
    void startAuditionFrom(auditionOffsetUs);
  });
  auditionProgress?.addEventListener("wheel", (event) => {
    const delta = Math.abs(event.deltaY) >= Math.abs(event.deltaX) ? event.deltaY : event.deltaX;
    if (!delta || !editor || auditionProgress.disabled) return;
    event.preventDefault();
    scrubAuditionBy(delta > 0 ? beatUs : -beatUs);
  }, { passive: false });
  document.getElementById("midi-editor-close")?.addEventListener("click", closeEditor);
  document.getElementById("btn-midi-editor-open-local")?.addEventListener("click", openLocalMidi);
  updatePlaybackLock();
}

let editorSongRequestVersion = 0;

function bindLibrarySongPicker() {
  document.getElementById("midi-editor-mode-filter")?.addEventListener("change", () => void refreshEditorSongList());
  document.getElementById("midi-editor-song")?.addEventListener("change", (event) => {
    if (event.target.value) void loadSong(event.target.value);
  });
  window.addEventListener("nte:page-changed", (event) => {
    if (event.detail?.pageId === "midi-editor") void refreshEditorSongList(editor?.id || "");
  });
}

async function refreshEditorSongList(selectedId = "") {
  const requestVersion = ++editorSongRequestVersion;
  const filter = document.getElementById("midi-editor-mode-filter")?.value || "all";
  const select = document.getElementById("midi-editor-song");
  const empty = document.getElementById("midi-editor-empty-state");
  if (!select) return;
  select.disabled = true;
  select.replaceChildren(new Option("正在读取曲库…", ""));
  try {
    const data = await invoke("library.list", { filter, query: "" });
    if (requestVersion !== editorSongRequestVersion) return;
    const items = Array.isArray(data?.items) ? data.items : [];
    select.replaceChildren(new Option(items.length ? "选择歌曲…" : "当前模式没有曲目", ""));
    for (const song of items) select.append(new Option(song.name || song.displayName || "未命名曲目", song.id));
    if (selectedId && items.some((song) => song.id === selectedId)) select.value = selectedId;
    else if (editor?.id && items.some((song) => song.id === editor.id)) select.value = editor.id;
    select.disabled = items.length === 0;
    if (empty) empty.hidden = Boolean(editor);
  } catch (error) {
    if (requestVersion !== editorSongRequestVersion) return;
    select.replaceChildren(new Option(`读取曲库失败：${error.message}`, ""));
    select.disabled = true;
    if (empty) { empty.hidden = false; empty.textContent = `读取曲库失败：${error.message}`; }
  }
}

function bindAudioTranscription() {
  document.getElementById("btn-piano-transcribe")?.addEventListener("click", () => void chooseAudioAndTranscribe());
  document.getElementById("btn-audio-transcription-select")?.addEventListener("click", () => void chooseAudioAndTranscribe());
  document.getElementById("btn-transcription-inspect")?.addEventListener("click", () => void inspectTranscriptionEnvironment());
  document.getElementById("btn-transcription-prepare")?.addEventListener("click", () => void prepareEnvironmentFromPage());
  document.getElementById("btn-transcription-install-cuda")?.addEventListener("click", () => void installCudaRuntimeFromPage());
  document.getElementById("btn-transcription-use-cpu")?.addEventListener("click", () => void switchToCpuRuntimeFromPage());
  document.getElementById("btn-transcription-prepare-cancel")?.addEventListener("click", () => void cancelTranscription());
  document.getElementById("audio-confirm-license-check")?.addEventListener("change", (event) => {
    document.getElementById("audio-confirm-continue").disabled = !event.target.checked;
  });
  document.getElementById("btn-transcription-start")?.addEventListener("click", () => void startAudioTranscription());
  document.getElementById("btn-transcription-cancel")?.addEventListener("click", () => void cancelTranscription());
  document.getElementById("btn-transcription-model-path")?.addEventListener("click", async () => {
    try {
      const result = await invoke("audioTranscription.pickModelFolder");
      if (result?.error) throw new Error(result.error);
      if (!result?.canceled) setTranscriptionMessage(`已指定模型目录：${result.path}`, "ready");
    } catch (error) { setTranscriptionError(error.message || "无法指定模型目录。"); }
  });
  window.addEventListener("nte:audio-transcription-progress", (event) => {
    const data = event.detail || {};
    renderCudaRuntimeDownloadProgress(data);
    renderTranscriptionProgress(data);
  });
  window.addEventListener("nte:audio-transcription-log", (event) => {
    const data = event.detail || {};
    if (data.requestId != null && activeTranscriptionId != null && Number(data.requestId) !== Number(activeTranscriptionId)) return;
    if (data.message) appendTranscriptionLog(`后台日志：${data.message}`);
  });
  window.addEventListener("nte:audio-transcription-cpu-confirmation", (event) => void confirmCpu(event.detail || {}));
  window.addEventListener("nte:audio-transcription-completed", (event) => {
    const data = event.detail || {};
    rememberTerminal(data.requestId, { ok: true, data });
    if (data.command === "prepare") {
      setTranscriptionMessage("模型已就绪，开始转写音频…", "transcribing");
    } else if (data.command === "cuda_install") {
      document.getElementById("transcription-prepare-message").textContent = "CUDA 推理环境已安装并启用。";
      finishCudaRuntimeDownload(true, "CUDA 推理环境已下载并启用。");
      void inspectTranscriptionEnvironment();
    } else if (data.command === "saved") {
      const category = ({ 0: "键盘谱", 1: "吉他谱", 2: "Piano" })[Number(data.targetInstrument)] || "本地";
      const fallbackLabel = data.fallbackSource === "vocal" ? "（采用备用人声主旋律）" : data.fallbackSource === "instrumental" ? "（采用备用器乐主旋律）" : "";
      rememberTerminal(data.requestId, { ok: true, savedToLibrary: true, data });
      window.dispatchEvent(new CustomEvent("nte:reload-library"));
      setTranscriptionMessage(`旋律已保存到${category}曲库${fallbackLabel}。当前停留在音频转谱页面；需要编辑时再从曲库打开 MIDI。`, "saved");
      setProgress(100, true, "转换完成");
      appendTranscriptionLog(`转换完成，MIDI 已保存到${category}曲库；没有自动打开游戏钢琴编辑器`);
    }
  });
  window.addEventListener("nte:audio-transcription-failed", (event) => {
    const data = event.detail || {};
    rememberTerminal(data.requestId, { ok: false, data });
    if (cudaRuntimeDownloadActive && (cudaRuntimeDownloadRequestId == null || Number(data.requestId) === Number(cudaRuntimeDownloadRequestId))) {
      finishCudaRuntimeDownload(false, data.error || "CUDA 推理环境下载失败。");
    }
    if (data.requestId === activeTranscriptionId) {
      if (transcriptionCancelRequested) {
        setTranscriptionMessage("已取消转写，后台推理进程已停止。", "cancelled");
        appendTranscriptionLog("取消已完成，Python 推理进程已停止；环境和模型文件已保留");
        setProgress(null, false);
      } else if (environmentPreparing) {
        document.getElementById("transcription-prepare-message").textContent = `准备失败：${errorMessage(data) || "请查看诊断信息后重试。"}`;
        const log = document.getElementById("transcription-prepare-log");
        if (log) log.textContent = `${log.textContent}\n${errorMessage(data) || JSON.stringify(data)}`.trim().split("\n").slice(-200).join("\n");
      }
      else setTranscriptionError(errorMessage(data) || "转写失败。可检查 Python 3.12 与 ffmpeg 配置后重试。");
      const detailLines = [...(Array.isArray(data.stderrLogTail) ? data.stderrLogTail : []), data.localJobDirectory ? `保留的转写文件：${data.localJobDirectory}` : ""].filter(Boolean);
      if (!environmentPreparing && detailLines.length) {
        detailLines.forEach((line) => appendTranscriptionLog(`后台诊断：${line}`));
        const diagnostics = document.getElementById("piano-transcription-diagnostics");
        const output = document.getElementById("piano-transcription-diagnostics-text");
        output.textContent = detailLines.join("\n"); diagnostics.removeAttribute("hidden");
      }
      activeTranscriptionId = null;
      audioFlowBusy = false;
      audioPreflightPending = false;
      updateTranscriptionButtons();
    }
  });
  window.addEventListener("nte:audio-transcription-shutdown-failed", (event) => {
    setTranscriptionMessage(event.detail?.error || "转谱后台关闭异常。", "error");
  });
}

async function inspectTranscriptionEnvironment() {
  const summary = document.getElementById("transcription-runtime-summary");
  const list = document.getElementById("transcription-runtime-items");
  if (!summary || !list) return null;
  summary.textContent = "正在检查本地运行环境…";
  list.replaceChildren();
  try {
    const state = await invoke("audioTranscription.inspect");
    const items = [
      ["Python / PyTorch", state.runtimeReady ? `就绪 · ${state.torchVersion || "PyTorch"}` : state.runtimeBundled ? "已内置 · 确认后解压至本地运行目录" : "缺失 · 初始化时准备本地 Python 3.12 和推理依赖"],
      ["运行模式", state.selectedRuntime === "cuda" ? state.cudaAvailable ? `GPU · ${state.device}` : `GPU 环境已选 · ${state.deviceError || "设备初始化失败"}` : "CPU · 转写前会再次确认"],
      ["GPU 环境", state.cudaRuntimeReady ? `已下载 · ${state.compatibleCudaIndex || state.selectedTorchIndex}` : state.compatibleCudaIndex ? `可选 · ${state.compatibleCudaIndex}（需下载）` : state.nvidiaGpuDetected ? "检测到 NVIDIA 显卡，但驱动版本不匹配受支持的 CUDA 环境" : "未检测到受支持的 NVIDIA CUDA 环境"],
      ["ffmpeg", state.ffmpegReady ? `就绪 · ${state.ffmpegPath}` : "缺失 · 需安装并加入 PATH"],
      ["SheetSage2 / MERT", state.modelReady ? `就绪 · ${state.modelRoot}` : "缺失 · 模型约 2.8 GB，初始化前会确认下载"]
    ];
    for (const [name, detail] of items) {
      const row = document.createElement("div"); row.className = "audio-runtime-item";
      const title = document.createElement("strong"); title.textContent = name;
      const value = document.createElement("span"); value.textContent = detail;
      row.append(title, value); list.append(row);
    }
    const installGpu = document.getElementById("btn-transcription-install-cuda");
    const useCpu = document.getElementById("btn-transcription-use-cpu");
    const prepare = document.getElementById("btn-transcription-prepare");
    cudaRuntimeSupported = Boolean(state.compatibleCudaIndex);
    if (installGpu) {
      installGpu.hidden = (!state.compatibleCudaIndex && !state.nvidiaGpuDetected) || (state.selectedRuntime === "cuda" && state.cudaRuntimeReady);
      installGpu.disabled = audioFlowBusy || !state.compatibleCudaIndex;
      installGpu.textContent = state.cudaRuntimeReady
        ? "切换到 CUDA 推理"
        : state.compatibleCudaIndex ? "一键下载 CUDA 环境" : "CUDA 驱动不兼容";
      installGpu.title = state.compatibleCudaIndex
        ? state.cudaRuntimeReady ? "CUDA 环境已下载，点击启用 GPU 推理" : "一键下载并安装 CUDA 推理环境；页面会显示实时进度"
        : "检测到 NVIDIA 显卡，但驱动版本不满足当前支持的 CUDA 版本";
    }
    if (useCpu) useCpu.hidden = state.selectedRuntime !== "cuda";
    if (prepare) prepare.textContent = state.runtimeReady && state.ffmpegReady && state.modelReady ? "检查环境" : "准备 CPU + 虚拟环境";
    summary.textContent = state.runtimeReady && state.ffmpegReady && state.modelReady
      ? `本地转谱环境已就绪 · 当前使用${state.selectedRuntime === "cuda" ? " GPU" : " CPU"}。`
      : "检测完成；初始化只会准备缺少的组件。";
    return state;
  } catch (error) {
    summary.textContent = `环境检查失败：${error.message}`;
    return null;
  }
}

async function confirmEnvironmentPreparation(state) {
  if (state?.runtimeReady && state?.modelReady && state?.ffmpegReady) return true;
  const missing = [];
  if (!state?.runtimeReady) missing.push(state?.runtimeBundled ? "内置 Python 3.12、PyTorch / torchaudio 运行环境（解压到本地应用数据，约需 6 GB 空间）" : "Python 3.12、PyTorch / torchaudio 与转写依赖（来源 PyTorch 官方 wheel，安装过程需下载，约需 6 GB 空间）");
  if (!state?.modelReady) missing.push("固定版本 SheetSage2 与 MERT 模型（约 2.8 GB，来源为 Hugging Face / ModelScope）");
  if (!state?.ffmpegReady) missing.push("ffmpeg（需自行安装并加入 PATH）");
  const list = document.getElementById("audio-confirm-items");
  list.replaceChildren(...missing.map((description) => { const item = document.createElement("li"); item.textContent = description; return item; }));
  const modal = document.getElementById("audio-preparation-confirm");
  const checkbox = document.getElementById("audio-confirm-license-check");
  const proceed = document.getElementById("audio-confirm-continue");
  checkbox.checked = false; proceed.disabled = true; modal.hidden = false;
  document.getElementById("audio-confirm-cancel").focus();
  return new Promise((resolve) => {
    const finish = (accepted) => {
      modal.hidden = true;
      proceed.removeEventListener("click", accept);
      document.getElementById("audio-confirm-cancel").removeEventListener("click", cancel);
      document.removeEventListener("keydown", escape);
      resolve(accepted);
    };
    const accept = () => finish(true);
    const cancel = () => finish(false);
    const escape = (event) => { if (event.key === "Escape") finish(false); };
    proceed.addEventListener("click", accept);
    document.getElementById("audio-confirm-cancel").addEventListener("click", cancel);
    document.addEventListener("keydown", escape);
  });
}

async function confirmCudaRuntimeDownload(cudaIndex) {
  const modal = document.getElementById("audio-cuda-confirm");
  document.getElementById("audio-cuda-confirm-index").textContent = `检测到兼容驱动，将下载 ${cudaIndex} 对应的 PyTorch CUDA 环境。`;
  modal.hidden = false;
  document.getElementById("audio-cuda-confirm-cancel").focus();
  return new Promise((resolve) => {
    const proceed = document.getElementById("audio-cuda-confirm-continue");
    const cancel = document.getElementById("audio-cuda-confirm-cancel");
    const finish = (accepted) => {
      modal.hidden = true;
      proceed.removeEventListener("click", accept);
      cancel.removeEventListener("click", decline);
      document.removeEventListener("keydown", escape);
      resolve(accepted);
    };
    const accept = () => finish(true);
    const decline = () => finish(false);
    const escape = (event) => { if (event.key === "Escape") finish(false); };
    proceed.addEventListener("click", accept);
    cancel.addEventListener("click", decline);
    document.addEventListener("keydown", escape);
  });
}

async function installCudaRuntimeFromPage() {
  if (audioFlowBusy) return;
  const state = await inspectTranscriptionEnvironment();
  if (!state?.compatibleCudaIndex) {
    setTranscriptionMessage("没有检测到支持的 NVIDIA CUDA 驱动；可继续使用 CPU。", "error");
    return;
  }
  if (state.cudaRuntimeReady) {
    audioFlowBusy = true;
    try {
      const result = await invoke("audioTranscription.useCuda");
      if (result?.selected !== "cuda") throw new Error("GPU 环境未能启用。");
      await inspectTranscriptionEnvironment();
    } catch (error) { setTranscriptionMessage(error.message || "切换 GPU 环境失败。", "error"); }
    finally { audioFlowBusy = false; updateTranscriptionButtons(); }
    return;
  }
  if (!state.cudaRuntimeReady && !await confirmCudaRuntimeDownload(state.compatibleCudaIndex)) return;

  audioFlowBusy = true; environmentPreparing = true; transcriptionCancelable = false;
  cudaRuntimeDownloadActive = true;
  cudaRuntimeDownloadRequestId = null;
  showCudaRuntimeDownloadProgress("正在启动 CUDA 推理环境下载…");
  transcriptionStartedAt = Date.now(); transcriptionLastProgressAt = transcriptionStartedAt;
  clearInterval(transcriptionActivityTimer); transcriptionActivityTimer = window.setInterval(renderTranscriptionActivity, 1000);
  document.getElementById("transcription-prepare-panel")?.removeAttribute("hidden");
  document.getElementById("transcription-prepare-log").textContent = "";
  document.getElementById("transcription-prepare-message").textContent = "等待开始下载 CUDA 推理环境…";
  updateTranscriptionButtons();
  try {
    const install = await invoke("audioTranscription.installCuda");
    activeTranscriptionId = install?.requestId;
    if (!activeTranscriptionId) throw new Error("后台未返回 CUDA 推理环境安装任务编号。");
    cudaRuntimeDownloadRequestId = activeTranscriptionId;
    transcriptionCancelable = true; updateTranscriptionButtons();
    const result = await waitForTerminal(activeTranscriptionId, 3 * 60 * 60_000);
    if (!result.ok) throw new Error(errorMessage(result.data) || "CUDA 推理环境安装失败。");
    await inspectTranscriptionEnvironment();
  } catch (error) {
    document.getElementById("transcription-prepare-message").textContent = `CUDA 推理环境准备失败：${error.message}`;
    if (cudaRuntimeDownloadActive) finishCudaRuntimeDownload(false, error.message || "CUDA 推理环境下载失败。");
  } finally {
    environmentPreparing = false; audioFlowBusy = false; activeTranscriptionId = null; transcriptionCancelable = false;
    clearInterval(transcriptionActivityTimer); transcriptionActivityTimer = 0; updateTranscriptionButtons();
  }
}

async function switchToCpuRuntimeFromPage() {
  if (audioFlowBusy) return;
  audioFlowBusy = true; updateTranscriptionButtons();
  try {
    await invoke("audioTranscription.useCpu");
    await inspectTranscriptionEnvironment();
  } catch (error) { setTranscriptionMessage(error.message || "切换 CPU 环境失败。", "error"); }
  finally { audioFlowBusy = false; updateTranscriptionButtons(); }
}

async function prepareEnvironmentFromPage() {
  if (audioFlowBusy) return;
  audioFlowBusy = true; transcriptionCancelable = false;
  transcriptionStartedAt = Date.now(); transcriptionLastProgressAt = transcriptionStartedAt;
  clearInterval(transcriptionActivityTimer); transcriptionActivityTimer = window.setInterval(renderTranscriptionActivity, 1000);
  environmentPreparing = true;
  document.getElementById("transcription-prepare-panel")?.removeAttribute("hidden");
  document.getElementById("transcription-prepare-message").textContent = "正在准备本地环境…";
  updateTranscriptionButtons();
  try {
    const state = await inspectTranscriptionEnvironment();
    if (!state || !await confirmEnvironmentPreparation(state)) {
      setTranscriptionMessage("已取消环境准备。", "cancelled"); return;
    }
    const prepare = await invoke("audioTranscription.prepare");
    activeTranscriptionId = prepare?.requestId;
    transcriptionCancelable = activeTranscriptionId != null; updateTranscriptionButtons();
    const result = await waitForTerminal(activeTranscriptionId, 3 * 60 * 60_000);
    if (!result.ok) throw new Error(errorMessage(result.data) || "环境准备失败。");
    document.getElementById("transcription-prepare-message").textContent = "环境准备完成。"; await inspectTranscriptionEnvironment();
  } catch (error) { document.getElementById("transcription-prepare-message").textContent = `环境准备失败：${error.message}`; }
  finally { environmentPreparing = false; activeTranscriptionId = null; audioFlowBusy = false; transcriptionCancelable = false; updateTranscriptionButtons(); }
}

async function chooseAudioAndTranscribe() {
  if (audioFlowBusy) return;
  try {
    audioFlowBusy = true;
    audioPreflightPending = false;
    activeAudioPath = "";
    showTranscriptionPanel();
    clearTranscriptionLog();
    document.getElementById("piano-transcription-live-log")?.setAttribute("open", "");
    setProgress(null, false);
    hideTranscriptionDiagnostics();
    setTranscriptionMessage("请选择要提取主旋律的歌曲文件…", "preparing");
    setTranscriptionActivity("等待选择音频文件");
    updateTranscriptionButtons();
    const selection = await invoke("audioTranscription.pickAudio");
    if (!selection || selection.canceled || !selection.audioPath) {
      audioFlowBusy = false;
      document.getElementById("piano-transcription-panel")?.setAttribute("hidden", "");
      return;
    }
    activeAudioPath = selection.audioPath;
    audioPreflightPending = true;
    transcriptionCancelable = true;
    appendTranscriptionLog(`已选择音频：${selection.name || "音频文件"}`);
    document.getElementById("piano-transcription-options")?.removeAttribute("hidden");
    setTranscriptionMessage(`已选择：${selection.name || "音频"}。选择要写入 MIDI 的旋律后开始。`, "ready");
    setTranscriptionActivity("人声和器乐可同时选择，并合并为两条音轨");
    updateTranscriptionButtons();
  } catch (error) {
    setTranscriptionError(error.message || "无法选择音频文件。");
    audioFlowBusy = false;
    audioPreflightPending = false;
    transcriptionCancelable = false;
    updateTranscriptionButtons();
  }
}

async function startAudioTranscription() {
  if (!audioPreflightPending || !activeAudioPath) return;
  const includeVocal = Boolean(document.getElementById("transcription-include-vocal")?.checked);
  const includeInstrumental = Boolean(document.getElementById("transcription-include-instrumental")?.checked);
  const targetInstrument = Number(document.getElementById("transcription-target-instrument")?.value ?? 2);
  if (!includeVocal && !includeInstrumental) {
    setTranscriptionMessage("至少选择一种旋律：人声或器乐。", "error");
    return;
  }
  audioPreflightPending = false;
  transcriptionCancelable = false;
  transcriptionStartedAt = Date.now();
  transcriptionLastProgressAt = transcriptionStartedAt;
  transcriptionProgressValue = 0;
  transcriptionCancelRequested = false;
  appendTranscriptionLog("开始转谱，正在检查运行设备和本地环境");
  clearInterval(transcriptionActivityTimer);
  transcriptionActivityTimer = window.setInterval(renderTranscriptionActivity, 1000);
  document.getElementById("piano-transcription-options")?.setAttribute("hidden", "");
  hideTranscriptionDiagnostics();
  setProgress(null, true);
  setTranscriptionMessage("正在启动本地转谱环境；首次安装依赖和模型可能需要几分钟。", "preparing");
  setTranscriptionActivity("等待本地环境响应");
  updateTranscriptionButtons();
  try {
    const runtimeState = await inspectTranscriptionEnvironment();
    if (!runtimeState || !await confirmEnvironmentPreparation(runtimeState)) {
      setTranscriptionMessage("已取消环境准备，可先初始化环境后再转换。", "cancelled");
      return;
    }
    const deviceLabel = document.getElementById("transcription-device-label");
    if (deviceLabel) deviceLabel.textContent = runtimeState.cudaAvailable ? `运行设备：GPU · ${runtimeState.device}` : "运行设备：CPU（速度较慢，已等待用户确认）";
    environmentPreparing = true;
    document.getElementById("transcription-prepare-panel")?.removeAttribute("hidden");
    document.getElementById("transcription-prepare-message").textContent = "开始检查和准备依赖…";
    const prepare = await invoke("audioTranscription.prepare");
    if (!prepare?.requestId) throw new Error("后台未返回模型准备任务编号。");
    activeTranscriptionId = prepare.requestId;
    transcriptionCancelable = true;
    updateTranscriptionButtons();
    // Preparation can take a while while pinned model files download. Its terminal event
    // is emitted by the backend when the worker's prepare request completes.
    const prepared = await waitForTerminal(prepare.requestId, 3 * 60 * 60_000);
    if (!prepared.ok) throw new Error(errorMessage(prepared.data) || "模型准备失败。");
    environmentPreparing = false;
    document.getElementById("transcription-prepare-panel")?.setAttribute("hidden", "");
    await inspectTranscriptionEnvironment();

    setTranscriptionMessage("本地环境与模型已就绪，正在检查运行设备…", "transcribing");
    const started = await invoke("audioTranscription.transcribe", { audioPath: activeAudioPath, includeVocal, includeInstrumental, targetInstrument });
    activeTranscriptionId = started?.requestId ?? null;
    appendTranscriptionLog(`后台任务已启动${started?.requestId != null ? `（任务 ${started.requestId}）` : ""}`);
    transcriptionCancelable = activeTranscriptionId != null;
    updateTranscriptionButtons();
    if (!activeTranscriptionId) throw new Error("后台未返回转写任务编号。");
    const completion = await waitForTerminal(activeTranscriptionId, 90 * 60_000);
    if (!completion.ok) throw new Error(errorMessage(completion.data) || "歌曲转谱失败。");
    if (completion.savedToLibrary) return;

    // The worker can finish before the backend has atomically saved the song to the library.
    const saved = await waitForLibrarySave(activeTranscriptionId, 5 * 60_000);
    if (saved?.savedToLibrary) {
      const category = ({ 0: "键盘谱", 1: "吉他谱", 2: "Piano" })[Number(saved.data?.targetInstrument)] || "本地";
      setTranscriptionMessage(`旋律已保存到${category}曲库。当前停留在音频转谱页面；需要编辑时再从曲库打开 MIDI。`, "saved");
      setProgress(null, false);
      return;
    }
    if (!saved?.savedToLibrary) throw new Error("转写已完成，但没有收到曲库保存结果。请检查曲库后重试。");
  } catch (error) {
    const detail = error.message || "歌曲转谱失败。";
    const runtimeHint = /Python runtime is not installed|Python 3\.12|ffmpeg/i.test(detail)
      ? " 请先配置 Python 3.12 运行环境与 ffmpeg，再重试；说明见 AudioTranscription/README.md。"
      : "";
    if (transcriptionCancelRequested) {
      setTranscriptionMessage("已取消转写，后台推理进程已停止。", "cancelled");
      appendTranscriptionLog("已取消转写");
    } else if (environmentPreparing) document.getElementById("transcription-prepare-message").textContent = `准备失败：${detail}${runtimeHint}`;
    else setTranscriptionError(`${detail}${runtimeHint}`);
  } finally {
    environmentPreparing = false;
    audioFlowBusy = false;
    audioPreflightPending = false;
    activeTranscriptionId = null;
    transcriptionCancelable = false;
    transcriptionCancelRequested = false;
    clearInterval(transcriptionActivityTimer);
    transcriptionActivityTimer = 0;
    setProgress(null, false);
    updateTranscriptionButtons();
  }
}

async function confirmCpu(data) {
  const requestId = Number(data.requestId);
  if (!Number.isFinite(requestId)) return;
  document.getElementById("transcription-device-label").textContent = "运行设备：CPU（速度较慢）";
  appendTranscriptionLog("当前使用 CPU 推理；等待用户确认后继续");
  const accepted = window.confirm(`${data.message || "当前没有可用 CUDA 显卡。"}\n\n${data.estimatedPerformance || "CPU 转写可能需要较长时间。"}\n\n确认后将继续使用 CPU；取消会停止本次任务。是否继续？`);
  try {
    if (accepted) {
      await invoke("audioTranscription.allowCpu", { requestId });
      setTranscriptionMessage("已确认使用 CPU，正在转写；长音频可能需要较长时间…", "transcribing");
      appendTranscriptionLog("用户已确认 CPU 推理，开始处理音频");
    } else {
      await invoke("audioTranscription.cancel", { requestId });
      rememberTerminal(requestId, { ok: false, data: { error: "已取消 CPU 转写。" } });
      setTranscriptionMessage("已取消转写。", "cancelled");
    }
  } catch (error) {
    rememberTerminal(requestId, { ok: false, data: { error: error.message } });
  }
}

async function cancelTranscription() {
  if (audioPreflightPending) {
    audioPreflightPending = false;
    audioFlowBusy = false;
    activeAudioPath = "";
    transcriptionCancelable = false;
    document.getElementById("piano-transcription-panel")?.setAttribute("hidden", "");
    document.getElementById("piano-transcription-options")?.setAttribute("hidden", "");
    updateTranscriptionButtons();
    return;
  }
  if (!transcriptionCancelable || activeTranscriptionId == null) {
    setTranscriptionMessage("等待后台任务编号，准备后即可取消。", "preparing");
    return;
  }
  const button = document.getElementById("btn-transcription-cancel");
  if (button) button.disabled = true;
  transcriptionCancelRequested = true;
  setTranscriptionMessage("正在立即停止本地推理进程…", "cancelling");
  appendTranscriptionLog("已请求取消，正在停止 Python 推理进程");
  try {
    await invoke("audioTranscription.cancel", { requestId: activeTranscriptionId });
    transcriptionCancelable = false;
  } catch (error) {
    transcriptionCancelRequested = false;
    setTranscriptionMessage(`取消失败：${error.message}`, "error");
    appendTranscriptionLog(`取消失败：${error.message}`);
  } finally {
    if (button) button.disabled = false;
    updateTranscriptionButtons();
  }
}

function showCudaRuntimeDownloadProgress(message) {
  if (cudaRuntimeDownloadStatusTimer) clearTimeout(cudaRuntimeDownloadStatusTimer);
  cudaRuntimeDownloadStatusTimer = 0;
  const card = document.getElementById("cuda-download-background-status");
  const bar = document.getElementById("cuda-download-background-bar");
  const fill = document.getElementById("cuda-download-background-fill");
  if (card) card.hidden = false;
  if (bar) {
    bar.dataset.indeterminate = "true";
    bar.removeAttribute("aria-valuenow");
    bar.setAttribute("aria-valuetext", "正在准备 CUDA 推理下载");
  }
  if (fill) fill.style.width = "0%";
  const phase = document.getElementById("cuda-download-background-phase");
  const output = document.getElementById("cuda-download-background-message");
  const footer = document.getElementById("cuda-download-background-state");
  const percentLabel = document.getElementById("cuda-download-background-percent");
  if (phase) phase.textContent = "正在准备";
  if (output) output.textContent = message;
  if (footer) footer.textContent = "后台下载中 · 可切换页面";
  if (percentLabel) percentLabel.textContent = "准备中";
}

function renderCudaRuntimeDownloadProgress(data) {
  if (!cudaRuntimeDownloadActive) return;
  if (cudaRuntimeDownloadRequestId != null && Number(data.requestId ?? data.operationId) !== Number(cudaRuntimeDownloadRequestId)) return;
  const card = document.getElementById("cuda-download-background-status");
  if (card) card.hidden = false;
  const phase = document.getElementById("cuda-download-background-phase");
  const output = document.getElementById("cuda-download-background-message");
  const footer = document.getElementById("cuda-download-background-state");
  const percentLabel = document.getElementById("cuda-download-background-percent");
  const bar = document.getElementById("cuda-download-background-bar");
  const fill = document.getElementById("cuda-download-background-fill");
  const stageName = data.stageName || (data.stage === "runtime_setup" ? "下载 CUDA 推理依赖" : data.stage || "准备 CUDA 推理环境");
  const message = data.message || stageName;
  if (phase) phase.textContent = stageName;
  if (output) output.textContent = message;
  if (footer) footer.textContent = "后台下载中 · 可切换页面";

  const downloaded = Number(data.downloadedBytes ?? data.downloaded ?? data.bytesDownloaded);
  const total = Number(data.totalBytes ?? data.bytesTotal ?? data.total ?? data.fileSize);
  const match = String(message).match(/(?:^|\s)(\d{1,3})%/);
  let percent = data.percent != null && Number.isFinite(Number(data.percent)) ? Number(data.percent) : NaN;
  if (!Number.isFinite(percent) && data.progress != null && Number.isFinite(Number(data.progress))) {
    const progress = Number(data.progress);
    percent = progress >= 0 && progress <= 1 ? progress * 100 : progress;
  }
  if (!Number.isFinite(percent) && Number.isFinite(downloaded) && Number.isFinite(total) && total > 0)
    percent = downloaded * 100 / total;
  if (!Number.isFinite(percent) && match) percent = Number(match[1]);

  if (Number.isFinite(percent) && percent >= 0) {
    const rounded = Math.max(0, Math.min(100, Math.round(percent)));
    if (footer) footer.textContent = Number.isFinite(downloaded) && Number.isFinite(total) && total > 0
      ? `当前下载 · ${formatBytes(downloaded)} / ${formatBytes(total)}`
      : "当前依赖包进度 · 后台下载中";
    if (bar) {
      bar.dataset.indeterminate = "false";
      bar.setAttribute("aria-valuenow", String(rounded));
      bar.setAttribute("aria-valuetext", `${rounded}% · ${stageName}`);
    }
    if (fill) fill.style.width = `${rounded}%`;
    if (percentLabel) percentLabel.textContent = `${rounded}%`;
  } else {
    if (footer) footer.textContent = "后台下载中 · 可切换页面";
    if (bar) {
      bar.dataset.indeterminate = "true";
      bar.removeAttribute("aria-valuenow");
      bar.setAttribute("aria-valuetext", `${stageName} · 后台处理中`);
    }
    if (fill) fill.style.width = "0%";
    if (percentLabel) percentLabel.textContent = "处理中";
  }
}

function finishCudaRuntimeDownload(success, message) {
  cudaRuntimeDownloadActive = false;
  cudaRuntimeDownloadRequestId = null;
  const card = document.getElementById("cuda-download-background-status");
  const phase = document.getElementById("cuda-download-background-phase");
  const output = document.getElementById("cuda-download-background-message");
  const footer = document.getElementById("cuda-download-background-state");
  const percentLabel = document.getElementById("cuda-download-background-percent");
  const bar = document.getElementById("cuda-download-background-bar");
  const fill = document.getElementById("cuda-download-background-fill");
  if (card) card.hidden = false;
  if (phase) phase.textContent = success ? "已完成" : "未完成";
  if (output) output.textContent = message;
  if (footer) footer.textContent = success ? "CUDA 推理已启用" : "可查看音频转谱页的准备日志";
  if (percentLabel) percentLabel.textContent = success ? "100%" : "已停止";
  if (bar) {
    bar.dataset.indeterminate = "false";
    bar.setAttribute("aria-valuenow", success ? "100" : "0");
    bar.setAttribute("aria-valuetext", success ? "100% · 已完成" : "下载未完成");
  }
  if (fill) fill.style.width = success ? "100%" : "0%";
  if (cudaRuntimeDownloadStatusTimer) clearTimeout(cudaRuntimeDownloadStatusTimer);
  cudaRuntimeDownloadStatusTimer = window.setTimeout(() => {
    if (card) card.hidden = true;
    cudaRuntimeDownloadStatusTimer = 0;
  }, success ? 6000 : 12000);
}

function renderTranscriptionProgress(data) {
  if (!audioFlowBusy) return;
  transcriptionLastProgressAt = Date.now();
  if (data.step != null) {
    const step = Math.max(1, Math.min(5, Number(data.step) || 1));
    currentTranscriptionStep = step;
    const label = document.getElementById("transcription-step-label");
    if (label) label.textContent = `第 ${step} / 5 步`;
    const device = document.getElementById("transcription-device-label");
    if (device && data.deviceName) device.textContent = `运行设备：${data.deviceName}`;
    if (device && data.device === "cpu") device.textContent = "运行设备：CPU（速度较慢）";
  }
  if (data.stageName) document.getElementById("piano-transcription-title").textContent = data.stageName;
  const stageNames = {
    runtime_setup: "安装本地 Python 运行环境",
    runtime_ready: "本地 Python 环境已就绪",
    cuda_runtime_setup: "下载并安装 CUDA 推理环境",
    cuda_runtime_ready: "CUDA 推理环境已就绪",
    preparing: "检查模型与 ffmpeg",
    disk_check: "检查模型下载所需磁盘空间",
    model_listing: "读取模型文件清单",
    model_file: "检查模型文件",
    model_download: "下载模型文件",
    model_ready: "模型文件已就绪",
    audio_decode: "解码音频",
    model_loading: "载入本地模型",
    model_code_prepare: "准备模型代码缓存",
    model_code_ready: "模型代码缓存已就绪",
    model_import: "加载推理组件",
    model_weights_loading: "读取 SheetSage2 与 MERT 权重",
    model_weights_loaded: "模型权重已载入",
    device_transfer: "准备推理设备",
    model_ready: "推理模型已就绪",
    transcribing: "提取所选主旋律",
    inference: "分析音频片段",
    encoding: "编码音频窗口",
    decoding: "解码旋律音符",
    window_complete: "完成一个推理窗口",
    notation: "生成 MIDI 音符",
    midi_output: "写入 MIDI 文件",
    writing_midi: "写入 MIDI 文件",
    midi_import: "导入 MIDI",
    library_save: "保存到曲库"
  };
  let message = data.stageName || data.message || stageNames[data.stage] || data.stage || "正在处理音频…";
  const logLine = formatTranscriptionProgress(data, stageNames);
  if (logLine) appendTranscriptionLog(logLine);
  if (environmentPreparing) {
    const stageName = stageNames[data.stage] || data.stageName || data.stage || "检查依赖";
    document.getElementById("transcription-prepare-stage").textContent = stageName;
    const log = document.getElementById("transcription-prepare-log");
    if (log && message) log.textContent = `${log.textContent}\n${message}`.trim().split("\n").slice(-200).join("\n");
    if (data.file) message += ` · ${data.file}`;
    const downloaded = Number(data.downloadedBytes ?? data.downloaded ?? data.bytesDownloaded);
    const total = Number(data.totalBytes ?? data.bytesTotal ?? data.total ?? data.fileSize);
    if (Number.isFinite(downloaded) && Number.isFinite(total) && total > 0) message += ` · ${formatBytes(downloaded)} / ${formatBytes(total)}`;
    document.getElementById("transcription-prepare-message").textContent = message;
    const rate = Number(data.bytesPerSecond);
    const eta = Number(data.remainingSeconds);
    if (Number.isFinite(rate) && rate > 0) message += ` · ${formatBytes(rate)}/秒`;
    if (Number.isFinite(eta) && eta >= 0) message += ` · 约剩 ${Math.ceil(eta)} 秒`;
    document.getElementById("transcription-prepare-message").textContent = message;
    renderTranscriptionActivity();
    const bar = document.getElementById("transcription-prepare-progress");
    const fill = document.getElementById("transcription-prepare-progress-fill");
    const percentLabel = document.getElementById("transcription-prepare-progress-percent");
    const percent = data.percent != null && Number.isFinite(Number(data.percent)) ? Number(data.percent) : Number.isFinite(downloaded) && total > 0 ? downloaded * 100 / total : NaN;
    bar.dataset.indeterminate = String(!Number.isFinite(percent));
    if (Number.isFinite(percent)) {
      const rounded = Math.round(Math.max(0, Math.min(100, percent)));
      bar.setAttribute("aria-valuenow", String(rounded));
      bar.setAttribute("aria-valuetext", `${rounded}%`);
      fill.style.width = `${rounded}%`;
      if (percentLabel) percentLabel.textContent = `${rounded}%`;
    }
    else {
      bar.removeAttribute("aria-valuenow");
      bar.setAttribute("aria-valuetext", `${stageName} · 进度待后台回报`);
      fill.style.width = "0%";
      if (percentLabel) percentLabel.textContent = "阶段准备中";
    }
    return;
  }
  if (data.stage === "disk_check" && Number.isFinite(Number(data.requiredBytes)) && Number.isFinite(Number(data.availableBytes))) {
    message = `模型需要 ${formatBytes(Number(data.requiredBytes))} · 当前可用 ${formatBytes(Number(data.availableBytes))}`;
  }
  if (data.file) message += ` · ${data.file}`;
  const downloaded = Number(data.downloadedBytes ?? data.downloaded ?? data.bytesDownloaded);
  const total = Number(data.totalBytes ?? data.bytesTotal ?? data.total ?? data.fileSize);
  const progressMatch = String(data.message || "").match(/(?:^|\s)(\d+(?:\.\d+)?)%/);
  let percent = data.percent != null && Number.isFinite(Number(data.percent)) ? Number(data.percent) : progressMatch ? Number(progressMatch[1]) : NaN;
  let progressLabel = "实际进度";
  if (!Number.isFinite(percent) && Number.isFinite(downloaded) && Number.isFinite(total) && total > 0) {
    percent = downloaded * 100 / total;
    progressLabel = "文件处理进度";
  }
  if (!Number.isFinite(percent)) {
    const phase = transcriptionPhaseProgress(data);
    percent = phase.percent;
    progressLabel = phase.label;
  }
  if (Number.isFinite(percent) && percent >= 0) {
    percent = Math.max(transcriptionProgressValue, percent);
    transcriptionProgressValue = percent;
    setProgress(percent, true, progressLabel);
    message += ` · ${Math.round(percent)}%`;
    if (Number.isFinite(downloaded) && Number.isFinite(total) && total > 0) {
      message += ` · ${formatBytes(downloaded)} / ${formatBytes(total)}`;
    }
  } else {
    const phase = transcriptionPhaseProgress(data);
    setProgress(null, true, phase.label);
    if (phase.label) message += ` · ${phase.label}`;
  }
  setTranscriptionMessage(message, data.stage || "working");
  renderTranscriptionActivity();
}

function showTranscriptionPanel() {
  document.getElementById("piano-transcription-panel")?.removeAttribute("hidden");
}

function setTranscriptionMessage(message, state) {
  showTranscriptionPanel();
  const title = document.getElementById("piano-transcription-title");
  const output = document.getElementById("piano-transcription-message");
  if (title) title.textContent = state === "error" ? "歌曲转谱未完成" : state === "saved" ? "转换完成" : state === "cancelled" ? "已取消" : state === "ready" ? "选择转谱模式" : "歌曲转主旋律 MIDI";
  if (output) output.textContent = message;
}

function setTranscriptionError(detail) {
  const lines = String(detail || "转写失败。").split(/\r?\n/);
  setTranscriptionMessage(lines[0] || "转写失败。", "error");
  appendTranscriptionLog(`错误：${lines[0] || "转写失败"}`);
  const diagnostics = document.getElementById("piano-transcription-diagnostics");
  const diagnosticsText = document.getElementById("piano-transcription-diagnostics-text");
  if (diagnostics && diagnosticsText && lines.length > 1) {
    diagnosticsText.textContent = detail;
    diagnostics.removeAttribute("hidden");
  }
  setTranscriptionActivity("可展开诊断信息查看具体失败步骤");
}

function hideTranscriptionDiagnostics() {
  document.getElementById("piano-transcription-diagnostics")?.setAttribute("hidden", "");
  const diagnosticsText = document.getElementById("piano-transcription-diagnostics-text");
  if (diagnosticsText) diagnosticsText.textContent = "";
}

function setTranscriptionActivity(message) {
  const activity = document.getElementById("piano-transcription-activity");
  if (activity && message) activity.textContent = message;
}

function renderTranscriptionActivity() {
  if (!audioFlowBusy || !transcriptionStartedAt) return;
  const elapsed = Math.floor((Date.now() - transcriptionStartedAt) / 1000);
  const silent = Math.floor((Date.now() - transcriptionLastProgressAt) / 1000);
  const format = (seconds) => `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
  if (environmentPreparing) {
    const activity = document.getElementById("transcription-prepare-activity");
    if (activity) activity.textContent = `已用 ${format(elapsed)} · ${silent > 20 ? `最近活动 ${format(silent)} 前` : "正在处理"}`;
    return;
  }
  const activity = document.getElementById("piano-transcription-activity");
  if (!activity) return;
  activity.textContent = silent >= 45
    ? `已运行 ${format(elapsed)} · ${format(silent)} 秒无新进度，可等待或取消`
    : `已运行 ${format(elapsed)} · 最近进度 ${format(silent)} 秒前`;
  activity.classList.toggle("is-stale", silent >= 45);
}

function setProgress(value, active = audioFlowBusy, label = "") {
  const bar = document.getElementById("piano-transcription-progress");
  if (!bar) return;
  const percentLabel = document.getElementById("piano-transcription-progress-percent");
  if (value == null || !Number.isFinite(value)) {
    bar.dataset.indeterminate = "true";
    bar.dataset.active = String(active);
    bar.setAttribute("aria-valuetext", label || "处理中，暂无百分比");
    bar.removeAttribute("aria-valuenow");
    if (percentLabel) percentLabel.textContent = label || "正在处理";
  } else {
    const percent = Math.max(0, Math.min(100, value));
    bar.dataset.indeterminate = "false";
    bar.dataset.active = String(active);
    bar.setAttribute("aria-valuenow", String(Math.round(percent)));
    const description = label ? `${Math.round(percent)}% · ${label}` : `${Math.round(percent)}%`;
    bar.setAttribute("aria-valuetext", description);
    const fill = document.getElementById("piano-transcription-progress-fill");
    if (fill) fill.style.width = `${percent}%`;
    if (percentLabel) percentLabel.textContent = description;
  }
}

function transcriptionPhaseProgress(data) {
  const stage = String(data.stage || "");
  if (["model_loading", "model_code_prepare", "model_code_ready", "model_import", "model_weights_loading", "model_weights_loaded", "device_transfer", "model_ready"].includes(stage)) {
    const detail = data.message || "模型载入中；模型加载器不提供可用的百分比，此阶段显示动态状态";
    return { percent: NaN, label: detail };
  }
  if (stage === "audio_decode" || stage === "decode_audio") return { percent: 16, label: "阶段预估 · 解码音频" };
  if (["transcribing", "encoding", "decoding", "window", "inference"].includes(stage)) {
    return { percent: 20, label: "推理阶段 · 等待首个窗口完成" };
  }
  if (stage === "window_complete") {
    const window = Number(data.window);
    const windows = Number(data.windows);
    if (Number.isFinite(window) && Number.isFinite(windows) && windows > 0) {
      const completed = Math.min(window, windows);
      return { percent: 20 + completed / windows * 68, label: `推理窗口 ${completed} / ${windows}` };
    }
    return { percent: NaN, label: "推理窗口已完成" };
  }
  if (stage === "notation") return { percent: 92, label: "生成乐谱" };
  if (stage === "midi_output" || stage === "writing_midi") return { percent: 96, label: "写入 MIDI" };
  if (stage === "library_save") return { percent: 99, label: "保存到曲库" };
  return { percent: NaN, label: "等待后台进度" };
}

function formatTranscriptionProgress(data, stageNames) {
  const stage = String(data.stage || "");
  const label = data.stageName || stageNames[stage] || stage || "后台状态";
  const parts = [label];
  const window = Number(data.window);
  const windows = Number(data.windows);
  if (Number.isFinite(window) && Number.isFinite(windows) && windows > 0) parts.push(`窗口 ${window}/${windows}`);
  if (data.deviceName) parts.push(`设备 ${data.deviceName}`);
  else if (data.device) parts.push(`设备 ${String(data.device).toUpperCase()}`);
  if (data.tokens != null && Number.isFinite(Number(data.tokens))) parts.push(`${Number(data.tokens).toLocaleString()} tokens`);
  if (data.file) parts.push(String(data.file));
  if (data.message && data.message !== label) parts.push(String(data.message));
  return parts.join(" · ");
}

function appendTranscriptionLog(message) {
  const text = String(message || "").trim();
  if (!text) return;
  const time = new Date().toLocaleTimeString("zh-CN", { hour12: false });
  transcriptionLogLines.push(`[${time}] ${text}`);
  if (transcriptionLogLines.length > 160) transcriptionLogLines = transcriptionLogLines.slice(-160);
  const output = document.getElementById("piano-transcription-live-log-text");
  const count = document.getElementById("piano-transcription-log-count");
  if (output) {
    output.textContent = transcriptionLogLines.join("\n");
    output.scrollTop = output.scrollHeight;
  }
  if (count) count.textContent = `${transcriptionLogLines.length} 条`;
}

function clearTranscriptionLog() {
  transcriptionLogLines = [];
  const output = document.getElementById("piano-transcription-live-log-text");
  const count = document.getElementById("piano-transcription-log-count");
  if (output) output.textContent = "等待后台状态…";
  if (count) count.textContent = "0 条";
}

function updateTranscriptionButtons() {
  const transcribe = document.getElementById("btn-piano-transcribe");
  const cancel = document.getElementById("btn-transcription-cancel");
  if (transcribe) transcribe.disabled = audioFlowBusy;
  const select = document.getElementById("btn-audio-transcription-select");
  const prepare = document.getElementById("btn-transcription-prepare");
  const inspect = document.getElementById("btn-transcription-inspect");
  const installCuda = document.getElementById("btn-transcription-install-cuda");
  const useCpu = document.getElementById("btn-transcription-use-cpu");
  if (select) select.disabled = audioFlowBusy;
  if (prepare) prepare.disabled = audioFlowBusy;
  if (inspect) inspect.disabled = audioFlowBusy;
  if (installCuda) installCuda.disabled = audioFlowBusy || !cudaRuntimeSupported;
  if (useCpu) useCpu.disabled = audioFlowBusy;
  const prepareCancel = document.getElementById("btn-transcription-prepare-cancel");
  if (prepareCancel) prepareCancel.disabled = !audioFlowBusy || !transcriptionCancelable || activeTranscriptionId == null;
  if (cancel) {
    cancel.disabled = !audioFlowBusy || (!audioPreflightPending && (!transcriptionCancelable || activeTranscriptionId == null));
    cancel.title = audioPreflightPending ? "取消当前歌曲选择" : !transcriptionCancelable && audioFlowBusy ? "等待后台任务编号" : "取消模型准备或当前转写";
  }
}

function rememberTerminal(requestId, result) {
  if (requestId == null) return;
  const previous = terminalEvents.get(Number(requestId));
  if (previous?.savedToLibrary && !result.savedToLibrary) return;
  terminalEvents.set(Number(requestId), result);
  while (terminalEvents.size > 24) terminalEvents.delete(terminalEvents.keys().next().value);
  const waiter = terminalWaiters.get(Number(requestId));
  if (waiter && (result.ok === false || result.savedToLibrary || result.data?.command === "prepare" ||
      result.data?.command === "transcribe" || result.data?.command === "cuda_install")) {
    terminalWaiters.delete(Number(requestId));
    waiter.resolve(result);
  }
}

function waitForTerminal(requestId, timeoutMs) {
  const existing = terminalEvents.get(Number(requestId));
  if (existing) return Promise.resolve(existing);
  return new Promise((resolve, reject) => {
    const timer = setTimeout(() => {
      terminalWaiters.delete(Number(requestId));
      reject(new Error("等待本地转谱后台超时。"));
    }, timeoutMs);
    terminalWaiters.set(Number(requestId), { resolve: (value) => { clearTimeout(timer); resolve(value); } });
  });
}

function waitForLibrarySave(requestId, timeoutMs) {
  const existing = terminalEvents.get(Number(requestId));
  if (existing?.savedToLibrary) return Promise.resolve(existing);
  if (existing?.ok === false) return Promise.resolve(false);
  return new Promise((resolve) => {
    const timer = setTimeout(() => { terminalWaiters.delete(Number(requestId)); resolve(false); }, timeoutMs);
    terminalWaiters.set(Number(requestId), { resolve: (value) => { clearTimeout(timer); resolve(value); } });
  });
}

async function openSongFromEvent(songId) {
  if (!songId) return false;
  const acknowledged = confirmDiscardIfDirty();
  if (!acknowledged) return false;
  const baseline = currentNotesSignature();
  try {
    const snapshot = await invoke("library.editorLoad", { id: songId });
    if (isDirty() && currentNotesSignature() !== baseline && !confirmDiscardIfDirty()) return false;
    const canNavigate = window.app?.showPage("midi-editor", { discardConfirmed: true });
    if (canNavigate === false) return false;
    openSnapshot(snapshot);
    const modeFilter = document.getElementById("midi-editor-mode-filter");
    if (modeFilter) modeFilter.value = "all";
    const songSelect = document.getElementById("midi-editor-song");
    if (songSelect) songSelect.value = songId;
    void refreshEditorSongList(songId);
    return true;
  } catch (error) {
    setStatus(`载入编辑曲目失败：${error.message}`);
    return false;
  }
}

async function openLocalMidi() {
  try {
    const selected = await invoke("library.pickFile");
    if (!selected?.filePath) return;
    if (!confirmDiscardIfDirty()) return;
    const baseline = currentNotesSignature();
    const snapshot = await invoke("library.editorOpenMidi", { filePath: selected.filePath });
    if (isDirty() && currentNotesSignature() !== baseline && !confirmDiscardIfDirty()) return;
    openSnapshot(snapshot);
    const songSelect = document.getElementById("midi-editor-song");
    if (songSelect) songSelect.value = "";
    const empty = document.getElementById("midi-editor-empty-state");
    if (empty) empty.hidden = true;
  } catch (error) { setStatus(`无法打开 MIDI：${error.message}`); }
}

async function loadSong(songId) {
  if (!songId) return;
  if (!confirmDiscardIfDirty()) {
    const songSelect = document.getElementById("midi-editor-song");
    if (songSelect && editor?.id) songSelect.value = editor.id;
    return;
  }
  const baseline = currentNotesSignature();
  try {
    const snapshot = await invoke("library.editorLoad", { id: songId });
    if (isDirty() && currentNotesSignature() !== baseline && !confirmDiscardIfDirty()) return;
    openSnapshot(snapshot);
    const songSelect = document.getElementById("midi-editor-song");
    if (songSelect) songSelect.value = songId;
  } catch (error) { setStatus(`载入编辑曲目失败：${error.message}`); }
}

function currentNotesSignature() {
  return editor ? JSON.stringify({ notes: plainNotes(editor.notes), tempo: editor.tempoMicrosecondsPerQuarterNote }) : "";
}

function syncBpmInput() {
  const input = document.getElementById("midi-editor-bpm");
  if (!input) return;
  input.value = editor ? String(Math.round(60_000_000 / editor.tempoMicrosecondsPerQuarterNote)) : "120";
  input.disabled = playbackLocked || !editor;
}

function applyBpmChange(event) {
  if (!editor) return;
  const input = event.currentTarget;
  const bpm = Number(input.value);
  if (!Number.isFinite(bpm) || bpm < 20 || bpm > 300) {
    syncBpmInput();
    setStatus("BPM 范围为 20–300，请输入有效速度。");
    return;
  }
  const nextTempo = Math.round(60_000_000 / bpm);
  const previousTempo = editor.tempoMicrosecondsPerQuarterNote;
  if (nextTempo === previousTempo) { syncBpmInput(); return; }
  const ratio = nextTempo / previousTempo;
  editor.notes = editor.notes.map((note) => ({
    ...note,
    startUs: Math.max(0, Math.round(note.startUs * ratio)),
    durationUs: Math.max(1, Math.round(note.durationUs * ratio))
  }));
  editor.tempoMicrosecondsPerQuarterNote = nextTempo;
  beatUs = nextTempo * 4 / editor.beatUnitDenominator;
  syncBpmInput();
  selectedIds.clear();
  pushHistory();
  renderTrackOptions();
  draw();
  setStatus(`速度已调整为 ${Math.round(60_000_000 / nextTempo)} BPM；保存或导出后写入 MIDI。`);
}

function createEditorHistoryState() {
  return { notes: cloneNotes(editor?.notes || []), tempoUsPerQuarter: editor?.tempoMicrosecondsPerQuarterNote || 500_000 };
}

function restoreEditorHistoryState(state) {
  editor.notes = cloneNotes(state.notes);
  editor.tempoMicrosecondsPerQuarterNote = state.tempoUsPerQuarter;
  beatUs = editor.tempoMicrosecondsPerQuarterNote * 4 / editor.beatUnitDenominator;
  syncBpmInput();
}

function openSnapshot(snapshot) {
  if (!snapshot || !Array.isArray(snapshot.notes)) throw new Error("后端返回的 MIDI 快照不完整。");
  setMidiRollEditorMode(true);
  stopAudition();
  auditionOffsetUs = 0;
  editor = {
    id: snapshot.id || null,
    name: snapshot.name || snapshot.sourceName || "未命名 MIDI",
    sourceName: snapshot.sourceName || "",
    sourcePath: snapshot.sourcePath || "",
    sourceHash: snapshot.sourceHash || "",
    sourceKey: snapshot.selectedSourceKey === "alternate" ? "alternate" : "source",
    tempoMicrosecondsPerQuarterNote: Number(snapshot.tempoUsPerQuarter) > 0 ? Number(snapshot.tempoUsPerQuarter) : 500_000,
    originalTempoMicrosecondsPerQuarterNote: Number(snapshot.tempoUsPerQuarter) > 0 ? Number(snapshot.tempoUsPerQuarter) : 500_000,
    beatsPerBar: Number(snapshot.timeSignature?.numerator) > 0 ? Number(snapshot.timeSignature.numerator) : 4,
    beatUnitDenominator: Number(snapshot.timeSignature?.denominator) > 0 ? Number(snapshot.timeSignature.denominator) : 4,
    isNew: Boolean(snapshot.isNew),
    sources: Array.isArray(snapshot.sources) && snapshot.sources.length ? snapshot.sources : [{ key: "source", name: snapshot.sourceName || "MIDI", notes: snapshot.notes, trackLabels: [] }],
    notes: assignIds(snapshot.notes),
    original: []
  };
  playbackLocked = ["playing", "countdown"].includes(window.ntePianoPlaybackState?.status);
  beatUs = editor.tempoMicrosecondsPerQuarterNote * 4 / editor.beatUnitDenominator;
  beatsPerBar = editor.beatsPerBar;
  editor.original = cloneNotes(editor.notes);
  selectedIds.clear();
  history = [createEditorHistoryState()];
  historyIndex = 0;
  viewStartUs = 0;
  const pitches = editor.notes.map((note) => note.noteNumber);
  topPitch = clamp(Math.max(72, ...pitches) + 5, 36, 127);
  renderSourceOptions();
  renderTrackOptions();
  updateEditorMeta();
  panel.hidden = false;
  const emptyState = document.getElementById("midi-editor-empty-state");
  if (emptyState) emptyState.hidden = true;
  syncAuditionTimeline();
  document.getElementById("midi-editor-source").value = editor.sourceKey;
  document.getElementById("midi-editor-time-zoom").value = String(pxPerSecond);
  syncBpmInput();
  const tempoBpm = Math.round(60_000_000 / editor.tempoMicrosecondsPerQuarterNote);
  setStatus(`${editor.notes.length.toLocaleString()} 个音符 · ${tempoBpm} BPM / ${editor.beatsPerBar}/${editor.beatUnitDenominator} 网格 · ${editor.isNew ? "本地文件，首次保存会加入 Piano 曲库" : "曲库曲目"}`);
  draw();
  canvas.focus();
  panel.scrollIntoView({ behavior: "smooth", block: "nearest" });
}

function selectSource(sourceKey) {
  const source = editor?.sources.find((item) => item.key === sourceKey);
  if (!source) return;
  stopAudition();
  editor.sourceKey = sourceKey;
  editor.notes = assignIds(source.notes || []);
  editor.original = cloneNotes(editor.notes);
  const sourceTempo = Number(source.tempoUsPerQuarter);
  const signature = source.timeSignature || {};
  if (sourceTempo > 0) editor.tempoMicrosecondsPerQuarterNote = sourceTempo;
  editor.originalTempoMicrosecondsPerQuarterNote = editor.tempoMicrosecondsPerQuarterNote;
  const sourceNumerator = Number(signature.numerator);
  const sourceDenominator = Number(signature.denominator);
  if (sourceNumerator > 0) editor.beatsPerBar = sourceNumerator;
  if (sourceDenominator > 0) editor.beatUnitDenominator = sourceDenominator;
  beatUs = editor.tempoMicrosecondsPerQuarterNote * 4 / editor.beatUnitDenominator;
  beatsPerBar = editor.beatsPerBar;
  const pitches = editor.notes.map((note) => note.noteNumber);
  topPitch = clamp(Math.max(72, ...pitches) + 5, 36, 127);
  selectedIds.clear();
  history = [createEditorHistoryState()];
  historyIndex = 0;
  renderTrackOptions();
  updateEditorMeta();
  syncBpmInput();
  syncAuditionTimeline();
  setStatus(`${source.name || sourceKey} · ${editor.notes.length.toLocaleString()} 个音符 · 初始 ${Math.round(60_000_000 / editor.tempoMicrosecondsPerQuarterNote)} BPM / ${editor.beatsPerBar}/${editor.beatUnitDenominator} 网格`);
  draw();
}

function renderSourceOptions() {
  const select = document.getElementById("midi-editor-source");
  select.replaceChildren();
  for (const source of editor.sources) {
    const option = document.createElement("option");
    option.value = source.key;
    const label = source.key === "source" ? `${source.name || "主旋律"}（当前版本）` : `${source.name || "替代旋律"}（另一版本）`;
    option.textContent = label;
    select.append(option);
  }
  select.disabled = editor.sources.length < 2;
}

function renderTrackOptions() {
  const select = document.getElementById("midi-editor-track");
  select.replaceChildren();
  addOption(select, "all", "全部音轨");
  const source = editor?.sources.find((item) => item.key === editor.sourceKey);
  const labels = source?.trackLabels || [];
  const tracks = new Map();
  for (const note of editor?.notes || []) if (!tracks.has(note.trackId)) tracks.set(note.trackId, note);
  for (const [trackId, note] of tracks) {
    const meta = labels.find((item) => item.id === trackId);
    const label = meta?.name || `Track #${trackId}`;
    addOption(select, String(trackId), `${label} · ${trackId} · ${(meta?.instrumentName || "MIDI 音轨")}`);
  }
  select.value = "all";
  select.disabled = tracks.size < 2;
}

function addOption(select, value, label) {
  const option = document.createElement("option");
  option.value = value;
  option.textContent = label;
  select.append(option);
}

function draw() {
  if (!ctx || !width || !height) return;
  const style = getComputedStyle(document.documentElement);
  const surface = style.getPropertyValue("--surface-2").trim() || "#17191f";
  const accent = style.getPropertyValue("--accent").trim() || "#8d8f97";
  const text = style.getPropertyValue("--text").trim() || "#e5e7eb";
  const muted = style.getPropertyValue("--muted").trim() || "#9398a2";
  ctx.clearRect(0, 0, width, height);
  ctx.fillStyle = surface;
  ctx.fillRect(0, 0, width, height);
  if (!editor) return;

  const visibleRows = Math.ceil((height - RULER_HEIGHT) / ROW_HEIGHT);
  const bottomPitch = Math.max(0, topPitch - visibleRows + 1);
  const firstBeat = Math.floor(viewStartUs / beatUs);
  const lastBeat = Math.ceil((viewStartUs + (width - KEY_WIDTH) * 1_000_000 / pxPerSecond) / beatUs);

  ctx.fillStyle = "rgba(128, 132, 142, .10)";
  ctx.fillRect(0, 0, KEY_WIDTH, height);
  for (let pitch = topPitch; pitch >= bottomPitch; pitch--) {
    const y = RULER_HEIGHT + (topPitch - pitch) * ROW_HEIGHT;
    const black = [1, 3, 6, 8, 10].includes(pitch % 12);
    if (pitch % 12 === 0) {
      ctx.fillStyle = "rgba(255,255,255,.035)";
      ctx.fillRect(KEY_WIDTH, y, width - KEY_WIDTH, ROW_HEIGHT);
    }
    ctx.strokeStyle = "rgba(148,163,184,.11)";
    ctx.beginPath(); ctx.moveTo(KEY_WIDTH, y + ROW_HEIGHT); ctx.lineTo(width, y + ROW_HEIGHT); ctx.stroke();
    ctx.fillStyle = black ? "rgba(26,28,34,.88)" : "rgba(220,223,228,.9)";
    ctx.fillRect(4, y + 1, KEY_WIDTH - 8, ROW_HEIGHT - 2);
    ctx.fillStyle = black ? "#d8d9dd" : "#343840";
    ctx.font = "10px sans-serif";
    ctx.textAlign = "right";
    ctx.textBaseline = "middle";
    ctx.fillText(noteName(pitch), KEY_WIDTH - 9, y + ROW_HEIGHT / 2);
  }

  ctx.fillStyle = "rgba(20,22,27,.95)";
  ctx.fillRect(0, 0, width, RULER_HEIGHT);
  ctx.font = "10px sans-serif";
  ctx.textAlign = "left";
  ctx.textBaseline = "middle";
  for (let beat = firstBeat; beat <= lastBeat; beat++) {
    const x = KEY_WIDTH + (beat * beatUs - viewStartUs) / 1_000_000 * pxPerSecond;
    const bar = beat % beatsPerBar === 0;
    ctx.strokeStyle = bar ? "rgba(148,163,184,.34)" : "rgba(148,163,184,.17)";
    ctx.beginPath(); ctx.moveTo(x, RULER_HEIGHT); ctx.lineTo(x, height); ctx.stroke();
    if (bar) {
      ctx.fillStyle = muted;
      ctx.fillText(`小节 ${Math.floor(beat / beatsPerBar) + 1}`, x + 4, 14);
    }
  }

  const trackFilter = document.getElementById("midi-editor-track")?.value || "all";
  const auditionPositionUs = auditionPlaying ? getAuditionPositionUs() : auditionOffsetUs;
  const auditionAbsoluteUs = auditionFirstUs + auditionPositionUs;
  for (const note of editor.notes) {
    if (trackFilter !== "all" && String(note.trackId) !== trackFilter) continue;
    const x = KEY_WIDTH + (note.startUs - viewStartUs) / 1_000_000 * pxPerSecond;
    const y = RULER_HEIGHT + (topPitch - note.noteNumber) * ROW_HEIGHT + 2;
    const w = Math.max(4, note.durationUs / 1_000_000 * pxPerSecond);
    if (x + w < KEY_WIDTH || x > width || y + ROW_HEIGHT < RULER_HEIGHT || y > height) continue;
    const selected = selectedIds.has(note._editorId);
    const sounding = auditionPlaying && auditionAbsoluteUs >= note.startUs && auditionAbsoluteUs < note.startUs + note.durationUs;
    ctx.fillStyle = sounding ? "#ffb454" : selected ? accent : `rgba(126, 148, 176, ${Math.max(.55, Math.min(.9, (note.velocity || 82) / 140))})`;
    roundedRect(ctx, x, y, w, ROW_HEIGHT - 4, 3);
    ctx.fill();
    if (selected || sounding) {
      ctx.strokeStyle = sounding ? "#fff3d6" : "rgba(255,255,255,.84)";
      ctx.lineWidth = sounding ? 1.5 : 1;
      ctx.stroke();
    }
  }
  if (playbackLocked || (editor && editor.notes.length)) {
    const playheadUs = playbackLocked ? playbackPositionUs : auditionAbsoluteUs;
    const playheadX = KEY_WIDTH + (playheadUs - viewStartUs) / 1_000_000 * pxPerSecond;
    if (playheadX >= KEY_WIDTH && playheadX <= width) {
      ctx.strokeStyle = playbackLocked ? "#ff4793" : "#ffb454";
      ctx.lineWidth = 2;
      ctx.beginPath(); ctx.moveTo(playheadX, RULER_HEIGHT); ctx.lineTo(playheadX, height); ctx.stroke();
    }
  }
  ctx.fillStyle = "rgba(20,22,27,.96)";
  ctx.fillRect(0, 0, KEY_WIDTH, RULER_HEIGHT);
  ctx.fillStyle = text;
  ctx.font = "10px sans-serif";
  ctx.fillText("音高", 13, 15);
  const startSec = viewStartUs / 1_000_000;
  const endSec = startSec + (width - KEY_WIDTH) / pxPerSecond;
  const range = document.getElementById("midi-editor-range");
  if (range) range.textContent = `${formatClock(startSec)} – ${formatClock(endSec)} · ${noteName(bottomPitch)}–${noteName(topPitch)}`;
}

function onPointerDown(event) {
  if (!editor || isPlaybackActive()) return;
  const point = eventPoint(event);
  if (point.y < RULER_HEIGHT && point.x >= KEY_WIDTH) {
    beginAuditionScrub(event, point);
    return;
  }
  if (auditionPlaying) stopAudition();
  canvas.focus();
  if (point.x < KEY_WIDTH || point.y < RULER_HEIGHT) return;
  const found = hitNote(point.x, point.y);
  if (!found) {
    if (event.button === 0) insertNote(point);
    return;
  }
  const note = found.note;
  if (event.ctrlKey || event.metaKey) {
    if (selectedIds.has(note._editorId)) selectedIds.delete(note._editorId);
    else selectedIds.add(note._editorId);
    draw();
    return;
  }
  if (!selectedIds.has(note._editorId)) {
    selectedIds.clear();
    selectedIds.add(note._editorId);
  }
  const resizeZone = Math.min(12, Math.max(3, found.width * 0.35));
  const resize = point.x >= found.x + found.width - resizeZone;
  pointerEdit = {
    pointerId: event.pointerId,
    mode: resize ? "resize" : "move",
    startX: point.x,
    startY: point.y,
    origin: cloneNotes(editor.notes),
    targetId: note._editorId,
    moving: editor.notes.filter((item) => selectedIds.has(item._editorId)).map((item) => ({ id: item._editorId, startUs: item.startUs, noteNumber: item.noteNumber })),
    changed: false
  };
  canvas.setPointerCapture(event.pointerId);
  draw();
}

function onPointerMove(event) {
  if (auditionScrub?.pointerId === event.pointerId) {
    updateAuditionScrub(eventPoint(event));
    return;
  }
  if (!pointerEdit || pointerEdit.pointerId !== event.pointerId) {
    if (editor) {
      const point = eventPoint(event);
      const found = point.x >= KEY_WIDTH && point.y >= RULER_HEIGHT ? hitNote(point.x, point.y) : null;
      const resizeZone = found ? Math.min(12, Math.max(3, found.width * 0.35)) : 0;
      canvas.style.cursor = found && point.x >= found.x + found.width - resizeZone
        ? "ew-resize"
        : found ? "grab" : "crosshair";
    }
    return;
  }
  const point = eventPoint(event);
  const timeDelta = Math.round((point.x - pointerEdit.startX) * 1_000_000 / pxPerSecond);
  const pitchDelta = Math.round((pointerEdit.startY - point.y) / ROW_HEIGHT);
  if (Math.abs(timeDelta) < 1 && pitchDelta === 0) return;
  const original = new Map(pointerEdit.moving.map((item) => [item.id, item]));
  if (pointerEdit.mode === "resize") {
    const target = editor.notes.find((item) => item._editorId === pointerEdit.targetId);
    const before = pointerEdit.origin.find((item) => item._editorId === pointerEdit.targetId);
    if (target && before) target.durationUs = resizeDuration(before.durationUs, timeDelta);
  } else {
    for (const note of editor.notes) {
      const before = original.get(note._editorId);
      if (!before) continue;
      note.startUs = Math.max(0, snapTime(before.startUs + timeDelta));
      note.noteNumber = clamp(before.noteNumber + pitchDelta, 0, 127);
    }
  }
  pointerEdit.changed = true;
  draw();
}

function finishPointerEdit(event) {
  if (auditionScrub?.pointerId === event.pointerId) {
    const resume = auditionScrub.resume;
    auditionScrub = null;
    try { canvas.releasePointerCapture(event.pointerId); } catch { }
    if (resume) void startAuditionFrom(auditionOffsetUs);
    return;
  }
  if (!pointerEdit || pointerEdit.pointerId !== event.pointerId) return;
  const changed = pointerEdit.changed;
  pointerEdit = null;
  if (changed) pushHistory();
  updateEditorMeta();
  draw();
}

function cancelPointerEdit(event) {
  if (auditionScrub?.pointerId === event.pointerId) {
    const resume = auditionScrub.resume;
    auditionScrub = null;
    try { canvas.releasePointerCapture(event.pointerId); } catch { }
    if (resume) void startAuditionFrom(auditionOffsetUs);
    return;
  }
  if (!pointerEdit || pointerEdit.pointerId !== event.pointerId) return;
  editor.notes = pointerEdit.origin;
  pointerEdit = null;
  draw();
}

function insertNote(point) {
  if (!editor || isPlaybackActive()) return;
  const trackValue = document.getElementById("midi-editor-track")?.value || "all";
  const matchingTrackNote = editor.notes.find((item) => trackValue === "all" || String(item.trackId) === trackValue);
  const trackId = trackValue === "all" ? (matchingTrackNote?.trackId ?? 0) : Number(trackValue);
  const note = {
    startUs: snapTime(viewStartUs + Math.max(0, point.x - KEY_WIDTH) * 1_000_000 / pxPerSecond),
    durationUs: Math.max(MIN_DURATION_US, snapTime(DEFAULT_DURATION_US)),
    noteNumber: clamp(topPitch - Math.floor((point.y - RULER_HEIGHT) / ROW_HEIGHT), 0, 127),
    velocity: 88,
    channel: matchingTrackNote?.channel ?? 0,
    program: matchingTrackNote?.program ?? -1,
    trackId: Number.isFinite(trackId) ? trackId : 0,
    barIndex: 0,
    beatInBar: 0,
    beatsPerBar: 4,
    beatUnitDenominator: 4,
    hasMeterPosition: false,
    sourceFormat: "MIDI",
    _editorId: nextNoteId++
  };
  editor.notes.push(note);
  selectedIds.clear();
  selectedIds.add(note._editorId);
  pushHistory();
  renderTrackOptions();
  updateEditorMeta();
  draw();
}

function hitNote(x, y) {
  if (!editor) return null;
  const trackFilter = document.getElementById("midi-editor-track")?.value || "all";
  for (let index = editor.notes.length - 1; index >= 0; index--) {
    const note = editor.notes[index];
    if (trackFilter !== "all" && String(note.trackId) !== trackFilter) continue;
    const nx = KEY_WIDTH + (note.startUs - viewStartUs) / 1_000_000 * pxPerSecond;
    const ny = RULER_HEIGHT + (topPitch - note.noteNumber) * ROW_HEIGHT + 2;
    const nw = Math.max(4, note.durationUs / 1_000_000 * pxPerSecond);
    if (x >= nx && x <= nx + nw && y >= ny && y <= ny + ROW_HEIGHT - 2) return { note, x: nx, width: nw };
  }
  return null;
}

function onEditorKeyDown(event) {
  if (!editor || !document.getElementById("page-midi-editor")?.classList.contains("active") || isEditableTarget(event.target)) return;
  if ((event.code === "Space" || event.key === " ") && !event.repeat && !(event.target instanceof HTMLElement && event.target.closest("button, a"))) {
    event.preventDefault();
    previewEditor();
    return;
  }
  if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
    event.preventDefault();
    const direction = event.key === "ArrowLeft" ? -1 : 1;
    if (event.shiftKey && !event.repeat) scrubAuditionBy(direction * 5_000_000);
    else pan(direction * (event.altKey ? 1.5 : 0.75));
    return;
  }
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "z") {
    event.preventDefault();
    event.shiftKey ? redo() : undo();
  } else if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === "y") {
    event.preventDefault(); redo();
  } else if (event.key === "Delete" || event.key === "Backspace") {
    event.preventDefault(); deleteSelected();
  } else if (event.key === "Escape") {
    selectedIds.clear(); draw();
  }
}

function resizeDuration(originalDurationUs, deltaUs) {
  const snapFraction = Number(document.getElementById("midi-editor-snap")?.value) || 0;
  const stepUs = snapFraction > 0 ? beatUs * snapFraction : 0;
  const rawDuration = originalDurationUs + deltaUs;
  if (stepUs <= 0) return Math.max(MIN_DURATION_US, Math.round(rawDuration));
  // Keep drag direction intuitive even at a snap boundary.
  const snappedDuration = deltaUs < 0
    ? Math.floor(rawDuration / stepUs) * stepUs
    : Math.ceil(rawDuration / stepUs) * stepUs;
  return Math.max(MIN_DURATION_US, Math.round(snappedDuration));
}

function deleteSelected() {
  if (!editor || !selectedIds.size || isPlaybackActive()) return;
  editor.notes = editor.notes.filter((note) => !selectedIds.has(note._editorId));
  selectedIds.clear();
  pushHistory();
  renderTrackOptions();
  updateEditorMeta();
  draw();
}

function pushHistory() {
  stopAudition();
  history = history.slice(0, historyIndex + 1);
  history.push(createEditorHistoryState());
  if (history.length > MAX_HISTORY) history.shift();
  historyIndex = history.length - 1;
  updateEditorMeta();
  syncAuditionTimeline();
}

function undo() {
  if (historyIndex <= 0 || !editor) return;
  stopAudition();
  historyIndex--;
  restoreEditorHistoryState(history[historyIndex]);
  selectedIds.clear();
  syncAuditionTimeline();
  renderTrackOptions(); updateEditorMeta(); draw();
}

function redo() {
  if (historyIndex >= history.length - 1 || !editor) return;
  stopAudition();
  historyIndex++;
  restoreEditorHistoryState(history[historyIndex]);
  selectedIds.clear();
  syncAuditionTimeline();
  renderTrackOptions(); updateEditorMeta(); draw();
}

async function saveEditor() {
  if (!editor || !editor.notes.length) { setStatus("MIDI 至少需要保留一个音符才能保存。"); return; }
  const button = document.getElementById("midi-editor-save");
  const overwritingLibrarySong = !editor.isNew;
  if (overwritingLibrarySong) {
    const selectedSource = editor.sources.find((source) => source.key === editor.sourceKey);
    const sourceName = selectedSource?.name || (editor.sourceKey === "alternate" ? "备用旋律" : "主 MIDI");
    const confirmed = window.confirm(`确定覆盖曲库歌曲“${editor.name}”的 MIDI 来源“${sourceName}”吗？\n此操作会直接更新本地曲库。`);
    if (!confirmed) {
      setStatus("已取消覆盖，编辑内容仍保留。");
      return;
    }
  }
  button.disabled = true;
  setStatus(overwritingLibrarySong ? "正在覆盖曲库 MIDI 并验证…" : "正在保存并验证 MIDI…");
  try {
    const result = await invoke("library.editorSave", editorPayload());
    editor.id = result.id;
    editor.isNew = false;
    editor.original = cloneNotes(editor.notes);
    editor.originalTempoMicrosecondsPerQuarterNote = editor.tempoMicrosecondsPerQuarterNote;
    editor.sourcePath = "";
    editor.sourceName = editor.sourceName || `${editor.name}.mid`;
    const savedSource = editor.sources.find((source) => source.key === editor.sourceKey);
    if (savedSource) {
      savedSource.notes = plainNotes(editor.notes);
      savedSource.tempoUsPerQuarter = editor.tempoMicrosecondsPerQuarterNote;
    }
    history = [createEditorHistoryState()];
    historyIndex = 0;
    window.dispatchEvent(new CustomEvent("nte:reload-library"));
    window.dispatchEvent(new CustomEvent("nte:piano-song-imported", { detail: { id: result.id } }));
    setStatus(`${overwritingLibrarySong ? "已覆盖曲库 MIDI" : "已保存到 Piano 曲库"} · ${Number(result.noteCount || editor.notes.length).toLocaleString()} 个音符`);
    updateEditorMeta();
  } catch (error) {
    setStatus(`保存失败，修改仍保留：${error.message}`);
  } finally { button.disabled = false; }
}

async function exportEditor() {
  if (!editor) return;
  const button = document.getElementById("midi-editor-export");
  button.disabled = true;
  setStatus("正在写入并验证导出 MIDI…");
  try {
    const result = await invoke("library.editorExport", editorPayload());
    if (result?.filePath) setStatus(`已导出：${result.fileName || result.filePath}`);
    else setStatus("已取消导出。");
  } catch (error) { setStatus(`导出失败，修改仍保留：${error.message}`); }
  finally { button.disabled = false; }
}

function editorPayload() {
  return {
    id: editor.id,
    name: editor.name,
    sourceName: editor.sourceName,
    sourcePath: editor.sourcePath,
    sourceKey: editor.sourceKey,
    tempoUsPerQuarter: editor.tempoMicrosecondsPerQuarterNote,
    timeSignature: { numerator: editor.beatsPerBar, denominator: editor.beatUnitDenominator },
    notes: editor.notes.map(({ _editorId, ...note }) => note)
  };
}

function previewEditor() {
  if (!editor?.notes.length) return;
  if (auditionPlaying) { stopAudition(); setStatus("试听已停止，可拖动进度条从任意位置继续。"); return; }
  void startAuditionFrom(auditionOffsetUs);
}

function syncAuditionTimeline() {
  if (!editor?.notes.length) {
    auditionFirstUs = 0;
    auditionDurationUs = 0;
    auditionOffsetUs = 0;
    syncAuditionControls();
    return;
  }
  auditionFirstUs = editor.notes.reduce((first, note) => Math.min(first, note.startUs), Number.POSITIVE_INFINITY);
  const lastUs = editor.notes.reduce((last, note) => Math.max(last, note.startUs + note.durationUs), auditionFirstUs);
  auditionDurationUs = Math.max(0, lastUs - auditionFirstUs);
  auditionOffsetUs = clamp(auditionOffsetUs, 0, auditionDurationUs);
  syncAuditionControls();
}

function syncAuditionControls() {
  const progress = document.getElementById("midi-editor-audition-progress");
  const time = document.getElementById("midi-editor-audition-time");
  const currentNotes = document.getElementById("midi-editor-current-notes");
  if (progress) {
    progress.max = String(auditionDurationUs);
    progress.value = String(clamp(auditionOffsetUs, 0, auditionDurationUs));
    progress.setAttribute("aria-valuetext", `${formatAuditionClock(auditionOffsetUs)} / ${formatAuditionClock(auditionDurationUs)}`);
    progress.disabled = !editor?.notes.length || playbackLocked || auditionStarting;
  }
  if (time) time.textContent = `${formatAuditionClock(auditionOffsetUs)} / ${formatAuditionClock(auditionDurationUs)}`;
  const preview = document.getElementById("midi-editor-preview");
  if (preview) {
    preview.textContent = auditionPlaying ? "停止试听" : "试听";
    preview.disabled = !editor?.notes.length || playbackLocked || auditionStarting;
  }
  if (!auditionPlaying && currentNotes) currentNotes.textContent = "当前音符 —";
}

function beginAuditionScrub(event, point) {
  const resume = auditionPlaying;
  if (resume) stopAudition();
  auditionScrub = { pointerId: event.pointerId, resume };
  canvas.setPointerCapture(event.pointerId);
  canvas.focus();
  updateAuditionScrub(point);
}

function updateAuditionScrub(point) {
  if (!editor?.notes.length || playbackLocked) return;
  const absoluteUs = viewStartUs + Math.max(0, point.x - KEY_WIDTH) * 1_000_000 / pxPerSecond;
  auditionOffsetUs = clamp(absoluteUs - auditionFirstUs, 0, auditionDurationUs);
  syncAuditionControls();
  draw();
}

function formatAuditionClock(microseconds) {
  const milliseconds = Math.max(0, Math.floor((Number(microseconds) || 0) / 1000));
  const minutes = Math.floor(milliseconds / 60_000);
  const seconds = Math.floor(milliseconds / 1000) % 60;
  return `${String(minutes).padStart(2, "0")}:${String(seconds).padStart(2, "0")}.${String(milliseconds % 1000).padStart(3, "0")}`;
}

async function startAuditionFrom(offsetUs) {
  if (!editor?.notes.length || playbackLocked || auditionStarting) return;
  auditionStarting = true;
  const requestToken = ++auditionStartToken;
  syncAuditionControls();
  try {
    audioContext ||= new (window.AudioContext || window.webkitAudioContext)();
    await audioContext.resume();
    if (requestToken !== auditionStartToken) return;
    if (!editor?.notes.length || playbackLocked) { auditionStarting = false; syncAuditionControls(); return; }
    stopAudition();
    syncAuditionTimeline();
    auditionOffsetUs = clamp(offsetUs, 0, auditionDurationUs);
    if (auditionOffsetUs >= auditionDurationUs) auditionOffsetUs = 0;
    const startAt = audioContext.currentTime + 0.06;
    auditionStartedAt = startAt;
    auditionStartedOffsetUs = auditionOffsetUs;
    auditionPlaying = true;
    auditionResumeAfterSeek = false;
    for (const note of editor.notes.slice(0, 5000)) {
      const noteStartUs = note.startUs - auditionFirstUs;
      const noteEndUs = noteStartUs + note.durationUs;
      if (noteEndUs <= auditionOffsetUs) continue;
      const scheduledStartUs = Math.max(noteStartUs, auditionOffsetUs);
      const oscillator = audioContext.createOscillator();
      const gain = audioContext.createGain();
      const at = startAt + (scheduledStartUs - auditionOffsetUs) / 1_000_000;
      const length = Math.max(0.025, (noteEndUs - scheduledStartUs) / 1_000_000);
      oscillator.type = "triangle";
      oscillator.frequency.value = 440 * Math.pow(2, (note.noteNumber - 69) / 12);
      gain.gain.setValueAtTime(0.0001, at);
      gain.gain.exponentialRampToValueAtTime(Math.max(0.02, (note.velocity || 80) / 127 * 0.12), at + 0.015);
      gain.gain.setTargetAtTime(0.0001, at + length, 0.035);
      oscillator.connect(gain).connect(audioContext.destination);
      oscillator.start(at);
      oscillator.stop(at + length + 0.16);
      auditionNodes.push({ oscillator, gain });
    }
    auditionEndTimer = window.setTimeout(finishAudition, Math.max(200, (auditionDurationUs - auditionOffsetUs) / 1000 + 220));
    setStatus("正在试听当前编辑中的音符…再次点击可停止。");
    auditionStarting = false;
    syncAuditionControls();
    renderAuditionFrame();
  } catch (error) {
    if (requestToken === auditionStartToken) {
      auditionStarting = false;
      stopAudition();
      setStatus(`无法试听：${error.message}`);
    }
  }
}

function stopAudition() {
  auditionStartToken++;
  if (auditionPlaying) auditionOffsetUs = getAuditionPositionUs();
  auditionPlaying = false;
  auditionStarting = false;
  if (auditionFrame) cancelAnimationFrame(auditionFrame);
  auditionFrame = 0;
  if (auditionEndTimer) window.clearTimeout(auditionEndTimer);
  auditionEndTimer = 0;
  for (const node of auditionNodes) {
    try { node.oscillator.stop(); } catch { }
    try { node.oscillator.disconnect(); node.gain.disconnect(); } catch { }
  }
  auditionNodes = [];
  syncAuditionControls();
  draw();
}

function getAuditionPositionUs() {
  if (!auditionPlaying || !audioContext) return auditionOffsetUs;
  return clamp(auditionStartedOffsetUs + Math.max(0, audioContext.currentTime - auditionStartedAt) * 1_000_000, 0, auditionDurationUs);
}

function renderAuditionFrame() {
  if (!auditionPlaying) return;
  auditionOffsetUs = getAuditionPositionUs();
  const progress = document.getElementById("midi-editor-audition-progress");
  if (progress) progress.value = String(auditionOffsetUs);
  const time = document.getElementById("midi-editor-audition-time");
  if (time) time.textContent = `${formatAuditionClock(auditionOffsetUs)} / ${formatAuditionClock(auditionDurationUs)}`;
  const absoluteUs = auditionFirstUs + auditionOffsetUs;
  const sounding = editor?.notes.filter((note) => absoluteUs >= note.startUs && absoluteUs < note.startUs + note.durationUs) || [];
  const names = [...new Set(sounding.map((note) => noteName(note.noteNumber)))];
  const currentNotes = document.getElementById("midi-editor-current-notes");
  if (currentNotes) currentNotes.textContent = names.length ? `当前音符 ${names.slice(0, 4).join(" · ")}${names.length > 4 ? ` +${names.length - 4}` : ""}` : "当前音符 —";
  if (width > KEY_WIDTH) {
    const playheadX = KEY_WIDTH + ((absoluteUs - viewStartUs) / 1_000_000) * pxPerSecond;
    if (playheadX > width - 36) viewStartUs = Math.max(0, absoluteUs - ((width - KEY_WIDTH) * 0.72 / pxPerSecond) * 1_000_000);
    else if (playheadX < KEY_WIDTH) viewStartUs = Math.max(0, absoluteUs - 100_000);
  }
  draw();
  if (auditionOffsetUs >= auditionDurationUs) { finishAudition(); return; }
  auditionFrame = requestAnimationFrame(renderAuditionFrame);
}

function finishAudition() {
  auditionOffsetUs = auditionDurationUs;
  stopAudition();
  syncAuditionControls();
  setStatus("试听完成。");
}

function scrubAuditionBy(deltaUs) {
  if (!editor?.notes.length || playbackLocked) return;
  const wasPlaying = auditionPlaying;
  const position = wasPlaying ? getAuditionPositionUs() : auditionOffsetUs;
  if (wasPlaying) stopAudition();
  auditionOffsetUs = clamp(position + deltaUs, 0, auditionDurationUs);
  syncAuditionControls();
  draw();
  if (wasPlaying) void startAuditionFrom(auditionOffsetUs);
}

function closeEditor() {
  if (!confirmDiscardIfDirty()) return;
  clearEditorSnapshot();
}

function clearEditorSnapshot() {
  auditionResumeAfterSeek = false;
  stopAudition();
  if (playbackRaf) cancelAnimationFrame(playbackRaf);
  playbackRaf = 0;
  editor = null;
  syncAuditionTimeline();
  panel.hidden = true;
  const emptyState = document.getElementById("midi-editor-empty-state");
  if (emptyState) emptyState.hidden = false;
  const songSelect = document.getElementById("midi-editor-song");
  if (songSelect) songSelect.value = "";
  selectedIds.clear();
  setMidiRollEditorMode(false);
  updateEditorMeta();
}

function schedulePlaybackPositionDraw() {
  if (playbackRaf) return;
  playbackRaf = requestAnimationFrame(() => {
    playbackRaf = 0;
    if (editor && !panel.hidden) draw();
  });
}

function confirmDiscardIfDirty() {
  return !isDirty() || window.confirm("当前 MIDI 有未保存的修改。放弃这些修改吗？");
}

function isDirty() {
  return Boolean(editor && (
    JSON.stringify(plainNotes(editor.notes)) !== JSON.stringify(plainNotes(editor.original)) ||
    editor.tempoMicrosecondsPerQuarterNote !== editor.originalTempoMicrosecondsPerQuarterNote
  ));
}

function updateEditorMeta() {
  if (!editor) {
    setText("midi-editor-dirty", "已保存");
    document.getElementById("midi-editor-undo")?.setAttribute("disabled", "");
    document.getElementById("midi-editor-redo")?.setAttribute("disabled", "");
    syncBpmInput();
    return;
  }
  setText("midi-editor-name", editor.name);
  const dirty = isDirty();
  const status = document.getElementById("midi-editor-dirty");
  if (status) {
    status.textContent = dirty ? "未保存" : "已保存";
    status.classList.toggle("is-dirty", dirty);
  }
  const undo = document.getElementById("midi-editor-undo");
  const redoButton = document.getElementById("midi-editor-redo");
  if (undo) undo.disabled = historyIndex <= 0;
  if (redoButton) redoButton.disabled = historyIndex >= history.length - 1;
  const save = document.getElementById("midi-editor-save");
  if (save) save.textContent = editor.isNew ? "保存到曲库" : "覆盖曲库 MIDI";
  updatePlaybackLock();
}

function updatePlaybackLock() {
  for (const id of ["midi-editor-undo", "midi-editor-redo", "midi-editor-save", "midi-editor-export"]) {
    const button = document.getElementById(id);
    if (button) button.disabled = playbackLocked || (id === "midi-editor-undo" && historyIndex <= 0) || (id === "midi-editor-redo" && historyIndex >= history.length - 1);
  }
  const sourceSelect = document.getElementById("midi-editor-source");
  if (sourceSelect) sourceSelect.disabled = playbackLocked || !editor || editor.sources.length < 2;
  const snap = document.getElementById("midi-editor-snap");
  if (snap) snap.disabled = playbackLocked;
  const preview = document.getElementById("midi-editor-preview");
  if (preview) preview.disabled = playbackLocked || !editor;
  syncBpmInput();
  syncAuditionControls();
  if (playbackLocked) setStatus("播放期间已锁定音符编辑；停止播放后可继续。 ");
}

function setStatus(message) { setText("midi-editor-status", message); }
function errorMessage(data) {
  if (!data) return "";
  if (typeof data.error === "string") return data.error;
  if (data.error?.message) return data.error.message;
  if (data.message) return data.message;
  if (data.detail) return typeof data.detail === "string" ? data.detail : JSON.stringify(data.detail);
  return "";
}
function setText(id, value) { const element = document.getElementById(id); if (element) element.textContent = value ?? ""; }
function cloneNotes(notes) { return notes.map((note) => ({ ...note })); }
function plainNotes(notes) { return notes.map(({ _editorId, ...note }) => note); }
function assignIds(notes) { return notes.map((note) => ({ ...note, _editorId: nextNoteId++ })); }
function eventPoint(event) { const rect = canvas.getBoundingClientRect(); return { x: event.clientX - rect.left, y: event.clientY - rect.top }; }
function snapTime(us) { const fraction = Number(document.getElementById("midi-editor-snap")?.value) || 0; const step = fraction > 0 ? beatUs * fraction : 0; return step > 0 ? Math.max(0, Math.round(us / step) * step) : Math.max(0, Math.round(us)); }
function clamp(value, min, max) { return Math.min(max, Math.max(min, value)); }
function noteName(pitch) { const names = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"]; return `${names[((pitch % 12) + 12) % 12]}${Math.floor(pitch / 12) - 1}`; }
function formatClock(seconds) { const min = Math.floor(seconds / 60); return `${String(min).padStart(2, "0")}:${String(Math.floor(seconds % 60)).padStart(2, "0")}`; }
function formatBytes(bytes) { if (bytes < 1024) return `${bytes} B`; const mb = bytes / 1024 / 1024; return mb < 1024 ? `${mb.toFixed(1)} MB` : `${(mb / 1024).toFixed(2)} GB`; }
function roundedRect(context, x, y, w, h, radius) { context.beginPath(); context.roundRect(x, y, Math.max(1, w), Math.max(1, h), radius); }
function isEditableTarget(target) { return target instanceof HTMLElement && (target.isContentEditable || ["INPUT", "TEXTAREA", "SELECT"].includes(target.tagName)); }
function setZoom(value, anchorX) {
  const next = clamp(Number(value) || 70, 20, 180);
  const relativeUs = viewStartUs + Math.max(0, anchorX - KEY_WIDTH) * 1_000_000 / pxPerSecond;
  pxPerSecond = next;
  viewStartUs = Math.max(0, relativeUs - Math.max(0, anchorX - KEY_WIDTH) * 1_000_000 / pxPerSecond);
  const slider = document.getElementById("midi-editor-time-zoom");
  if (slider) slider.value = String(next);
  draw();
}
function zoomAt(factor, anchorX) { setZoom(pxPerSecond * factor, anchorX); }
function pan(fraction) { viewStartUs = Math.max(0, viewStartUs + fraction * (width - KEY_WIDTH) * 1_000_000 / pxPerSecond); draw(); }
function isPlaybackActive() { return ["playing", "countdown"].includes(window.ntePianoPlaybackState?.status); }

