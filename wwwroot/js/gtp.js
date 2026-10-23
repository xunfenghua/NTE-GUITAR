import { invoke } from "./bridge.js";
import { showToast } from "./app.js";
import { setMidiRollNotes } from "./midi-roll.js";

const SUPPORTED = /\.(?:gp3|gp4|gp5|gpx|gp)$/i;
let source = null;
let tracks = [];
let transpose = 0;
let busy = false;

const byId = (id) => document.getElementById(id);
const formatDuration = (microseconds) => {
  const seconds = Math.max(0, Math.floor((Number(microseconds) || 0) / 1_000_000));
  return `${String(Math.floor(seconds / 60)).padStart(2, "0")}:${String(seconds % 60).padStart(2, "0")}`;
};

function setStatus(message, kind = "info") {
  const status = byId("gtp-status");
  if (!status) return;
  status.textContent = message;
  status.dataset.kind = kind;
}

function errorText(error) {
  return error?.message || "操作失败，请重试。";
}

function renderTracks() {
  const host = byId("gtp-track-list");
  const options = byId("gtp-options");
  const summary = byId("gtp-track-summary");
  if (!host || !options) return;
  options.hidden = !source;
  host.replaceChildren();
  if (!source) return;
  if (summary) summary.textContent = `${tracks.length} 条音轨`;
  if (!tracks.length) {
    host.innerHTML = '<div class="gtp-track-empty">没有找到可转换的音轨。</div>';
    return;
  }
  for (const track of tracks) {
    const row = document.createElement("label");
    row.className = "gtp-track-row";
    const check = document.createElement("input");
    check.type = "checkbox";
    check.value = String(track.id);
    check.checked = true;
    check.addEventListener("change", updateConvertAvailability);
    const copy = document.createElement("span");
    copy.className = "gtp-track-copy";
    const name = document.createElement("strong");
    name.textContent = track.name || `音轨 ${track.id}`;
    const meta = document.createElement("small");
    meta.textContent = `${Number(track.noteCount) || 0} 音符 · ${formatDuration(track.durationUs)}`;
    copy.append(name, meta);
    const index = document.createElement("span");
    index.className = "gtp-track-index";
    index.textContent = String(track.id);
    row.append(check, copy, index);
    host.append(row);
  }
  updateConvertAvailability();
}

function selectedTrackIds() {
  return [...(byId("gtp-track-list")?.querySelectorAll('input[type="checkbox"]:checked') || [])]
    .map((input) => tracks.find((track) => String(track.id) === input.value)?.id ?? input.value);
}

function updateConvertAvailability() {
  const button = byId("gtp-convert");
  if (button) button.disabled = busy || !source || selectedTrackIds().length === 0;
}

async function parseFile(filePath, fileName = "") {
  if (!filePath) {
    setStatus("无法读取文件路径。请使用“浏览文件”选择 GTP 文件。", "error");
    return;
  }
  const pathName = fileName || filePath.split(/[\\/]/).pop() || "Guitar Pro 文件";
  if (!SUPPORTED.test(pathName)) {
    setStatus("文件格式不支持，请选择 .gp3、.gp4、.gp5、.gpx 或 .gp 文件。", "error");
    return;
  }
  busy = true;
  source = null;
  tracks = [];
  byId("gtp-file-name").textContent = pathName;
  byId("gtp-file-hint").textContent = "正在解析谱面…";
  renderTracks();
  updateConvertAvailability();
  setStatus("正在解析 Guitar Pro 文件…", "loading");
  try {
    const parsed = await invoke("gtp.parse", { filePath });
    source = { ...parsed, filePath: parsed?.filePath || filePath, fileName: parsed?.fileName || pathName };
    tracks = Array.isArray(parsed?.tracks) ? parsed.tracks : [];
    byId("gtp-file-name").textContent = source.fileName;
    byId("gtp-file-hint").textContent = `${source.title || source.fileName} · ${tracks.length} 条音轨`;
    setStatus(tracks.length ? "谱面解析完成，选择音轨和转换选项。" : "谱面已解析，但未找到可转换音轨。", tracks.length ? "success" : "error");
    renderTracks();
  } catch (error) {
    source = null;
    tracks = [];
    byId("gtp-file-hint").textContent = "Guitar Pro 文件 · .gp3 / .gp4 / .gp5 / .gpx / .gp";
    setStatus(`解析失败：${errorText(error)}`, "error");
    renderTracks();
  } finally {
    busy = false;
    updateConvertAvailability();
  }
}

function normalizePreviewNotes(notes) {
  return (Array.isArray(notes) ? notes : []).map((note) => ({
    ...note,
    pitch: note.noteNumber ?? note.pitch,
  }));
}

function renderListState(message, state = "empty") {
  const host = byId("gtp-list-state");
  const list = byId("gtp-list");
  if (!host || !list) return;
  host.hidden = false;
  host.className = `gtp-list-state ${state}`;
  host.textContent = message;
  list.hidden = true;
}

function renderItems(items) {
  const host = byId("gtp-list");
  const state = byId("gtp-list-state");
  if (!host || !state) return;
  host.replaceChildren();
  if (!items.length) {
    renderListState("还没有转换的 GTP 曲目。选择一个 Guitar Pro 文件开始转换。", "empty");
    return;
  }
  state.hidden = true;
  host.hidden = false;
  for (const item of items) {
    const card = document.createElement("article");
    card.className = "gtp-song-row";
    const info = document.createElement("div");
    info.className = "gtp-song-info";
    const title = document.createElement("strong");
    title.className = "gtp-song-title";
    title.textContent = item.name || "未命名 GTP 曲目";
    const meta = document.createElement("span");
    meta.className = "gtp-song-meta";
    meta.textContent = `${item.target || "GTP"} · ${Number(item.noteCount) || 0} 音符 · ${formatDuration(item.durationUs)}`;
    info.append(title, meta);
    const actions = document.createElement("div");
    actions.className = "gtp-song-actions";
    const preview = document.createElement("button");
    preview.type = "button";
    preview.className = "btn btn-sm";
    preview.textContent = "预览";
    preview.addEventListener("click", () => previewItem(item));
    const play = document.createElement("button");
    play.type = "button";
    play.className = "btn btn-sm btn-primary";
    play.textContent = "▶ 播放";
    play.addEventListener("click", () => playItem(item));
    const remove = document.createElement("button");
    remove.type = "button";
    remove.className = "btn btn-sm btn-danger";
    remove.textContent = "删除";
    remove.setAttribute("aria-label", `删除 ${item.name || "GTP 曲目"}`);
    remove.addEventListener("click", () => deleteItem(item));
    actions.append(preview, play, remove);
    card.append(info, actions);
    host.append(card);
  }
}

async function refreshList() {
  renderListState("正在读取 GTP 曲目…", "loading");
  try {
    const result = await invoke("gtp.list");
    renderItems(Array.isArray(result?.items) ? result.items : []);
  } catch (error) {
    renderListState(`读取 GTP 曲目失败：${errorText(error)}`, "error");
  }
}

async function previewItem(item) {
  try {
    const result = await invoke("gtp.preview", { id: item.id });
    setMidiRollNotes(normalizePreviewNotes(result?.previewNotes), "GTP 音符预览");
    showToast(`已载入 GTP 预览：${item.name || "未命名曲目"}`, "success");
  } catch (error) {
    showToast(`GTP 预览失败：${errorText(error)}`, "danger");
  }
}

async function playItem(item) {
  try {
    const isPiano = item.target === "piano" || item.target === "game-piano" || item.target === "gamePiano";
    if (isPiano) {
      window.dispatchEvent(new CustomEvent("nte:gtp-piano-song", {
        detail: { id: item.id, name: item.name || "GTP 曲目", noteCount: Number(item.noteCount) || 0 },
      }));
    } else {
      window.dispatchEvent(new CustomEvent("nte:gtp-piano-song", { detail: null }));
    }
    await invoke("gtp.play", { id: item.id });
  } catch (error) {
    showToast(`GTP 播放失败：${errorText(error)}`, "danger");
  }
}

async function deleteItem(item) {
  try {
    await invoke("gtp.delete", { id: item.id });
    showToast(`已删除 GTP 曲目：${item.name || "未命名曲目"}`, "success");
    await refreshList();
  } catch (error) {
    showToast(`删除 GTP 曲目失败：${errorText(error)}`, "danger");
  }
}

async function convert() {
  if (!source || busy) return;
  const button = byId("gtp-convert");
  const name = (source.title || source.fileName || "GTP 曲目").replace(/\.(?:gp3|gp4|gp5|gpx|gp)$/i, "");
  busy = true;
  updateConvertAvailability();
  button.textContent = "正在转换…";
  setStatus("正在转换谱面，请稍候…", "loading");
  try {
    const result = await invoke("gtp.convert", {
      filePath: source.filePath,
      name,
      trackIds: selectedTrackIds(),
      target: byId("gtp-target").value,
      selection: byId("gtp-selection").value,
      transpose,
    });
    if (Array.isArray(result?.previewNotes)) setMidiRollNotes(normalizePreviewNotes(result.previewNotes), "GTP 转换预览");
    setStatus(`转换完成：${result?.name || name} · ${Number(result?.noteCount) || 0} 音符`, "success");
    showToast("GTP 转换完成，曲目已添加到独立列表。", "success");
    await refreshList();
  } catch (error) {
    setStatus(`转换失败：${errorText(error)}`, "error");
    showToast(`GTP 转换失败：${errorText(error)}`, "danger");
  } finally {
    busy = false;
    button.textContent = "转换 GTP";
    updateConvertAvailability();
  }
}

export function initGtp() {
  window.addEventListener("nte:page-changed", (event) => {
    if (event.detail?.pageId !== "gtp") {
      window.dispatchEvent(new CustomEvent("nte:gtp-piano-song", { detail: null }));
    }
  });
  const dropzone = byId("gtp-dropzone");
  const pickFile = async () => {
    try {
      const file = await invoke("gtp.pickFile");
      const path = file?.filePath || file?.path;
      if (path) await parseFile(path, file?.fileName || file?.name || "");
      else if (!file?.cancelled) setStatus("未能取得文件路径，请重试。", "error");
    } catch (error) {
      setStatus(`选择文件失败：${errorText(error)}`, "error");
    }
  };
  byId("gtp-pick-file")?.addEventListener("click", (event) => { event.stopPropagation(); pickFile(); });
  dropzone?.addEventListener("click", (event) => { if (event.target !== byId("gtp-pick-file")) pickFile(); });
  dropzone?.addEventListener("keydown", (event) => {
    if (event.key === "Enter" || event.key === " ") { event.preventDefault(); pickFile(); }
  });
  dropzone?.addEventListener("dragover", (event) => { event.preventDefault(); dropzone.classList.add("dragging"); });
  dropzone?.addEventListener("dragleave", () => dropzone.classList.remove("dragging"));
  dropzone?.addEventListener("drop", async (event) => {
    event.preventDefault();
    dropzone.classList.remove("dragging");
    const file = event.dataTransfer?.files?.[0];
    if (!file) return;
    const path = file.path || file.fullPath;
    if (!path) {
      setStatus("此运行环境未提供拖入文件的本地路径，请点击“浏览文件”选择。", "error");
      return;
    }
    await parseFile(path, file.name);
  });

  byId("gtp-select-all")?.addEventListener("click", () => {
    byId("gtp-track-list")?.querySelectorAll('input[type="checkbox"]').forEach((input) => { input.checked = true; });
    updateConvertAvailability();
  });
  byId("gtp-select-none")?.addEventListener("click", () => {
    byId("gtp-track-list")?.querySelectorAll('input[type="checkbox"]').forEach((input) => { input.checked = false; });
    updateConvertAvailability();
  });
  byId("gtp-transpose-minus")?.addEventListener("click", () => { transpose = Math.max(-24, transpose - 1); byId("gtp-transpose-value").textContent = `${transpose > 0 ? "+" : ""}${transpose}`; });
  byId("gtp-transpose-plus")?.addEventListener("click", () => { transpose = Math.min(24, transpose + 1); byId("gtp-transpose-value").textContent = `${transpose > 0 ? "+" : ""}${transpose}`; });
  byId("gtp-convert")?.addEventListener("click", convert);
  byId("gtp-refresh")?.addEventListener("click", refreshList);
  refreshList();
}
