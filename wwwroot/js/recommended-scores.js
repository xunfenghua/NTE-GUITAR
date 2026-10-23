import { invoke } from "./bridge.js";
import { showToast } from "./app.js";

let search = "";
let level = "";
let instrument = "";
let debounceTimer = 0;
let submissionFilePath = "";
let savedSubmissionUploaderName = "";

export function initRecommendedScores() {
  const levelFilter = document.getElementById("recommended-level-filter");
  if (levelFilter && levelFilter.options.length === 1) {
    for (let value = 1; value <= 16; value++) {
      const option = document.createElement("option");
      option.value = String(value);
      option.textContent = `${value} 级`;
      levelFilter.appendChild(option);
    }
  }

  document.getElementById("recommended-search")?.addEventListener("input", (event) => {
    search = event.target.value.trim();
    scheduleLoad();
  });
  levelFilter?.addEventListener("change", () => {
    level = levelFilter.value;
    loadRecommendedScores();
  });
  const instrumentFilter = document.getElementById("recommended-instrument-filter");
  instrumentFilter?.addEventListener("change", () => {
    instrument = instrumentFilter.value;
    loadRecommendedScores();
  });
  document.getElementById("btn-recommended-refresh")?.addEventListener("click", loadRecommendedScores);
  const submissionLevel = document.getElementById("recommended-submit-level");
  if (submissionLevel && submissionLevel.options.length === 0) {
    for (let value = 1; value <= 16; value++) {
      const option = document.createElement("option");
      option.value = String(value);
      option.textContent = `${value} 级`;
      submissionLevel.appendChild(option);
    }
  }
  document.getElementById("btn-recommended-submit")?.addEventListener("click", openSubmission);
  document.getElementById("btn-recommended-pick-midi")?.addEventListener("click", pickSubmissionMidi);
  document.getElementById("recommended-submit-form")?.addEventListener("submit", submitScore);
  const uploaderInput = document.getElementById("recommended-submit-uploader");
  uploaderInput?.addEventListener("change", () => {
    saveSubmissionUploaderName(uploaderInput.value).catch((error) => {
      showToast(`保存投稿者失败：${error.message}`, "danger");
      uploaderInput.value = savedSubmissionUploaderName;
    });
  });
  void loadSubmissionUploaderName();
  document.getElementById("recommended-tbody")?.addEventListener("click", onTableClick);
  window.addEventListener("nte:page-changed", (event) => {
    if (event.detail?.pageId === "recommended-scores") loadRecommendedScores();
  });
}

function scheduleLoad() {
  window.clearTimeout(debounceTimer);
  debounceTimer = window.setTimeout(loadRecommendedScores, 180);
}

async function loadRecommendedScores() {
  const body = document.getElementById("recommended-tbody");
  const status = document.getElementById("recommended-status");
  if (!body) return;
  body.innerHTML = '<tr><td colspan="6" class="recommended-empty">正在加载推荐谱…</td></tr>';
  try {
    const result = await invoke("recommended.list", {
      query: search,
      level: level ? Number(level) : null,
      instrument: instrument || null
    });
    if (result?.status === "unavailable") {
      body.innerHTML = '<tr><td colspan="6" class="recommended-empty">暂时无法连接推荐谱服务，联网后可重试。</td></tr>';
      if (status) status.textContent = "服务暂不可用";
      return;
    }
    const items = result?.items || [];
    if (status) status.textContent = result.offline
      ? `离线目录 · ${items.length} 首（仅显示已缓存目录）`
      : `共 ${items.length} 首 · 下载后可离线使用`;
    if (!items.length) {
      body.innerHTML = '<tr><td colspan="6" class="recommended-empty">没有符合条件的推荐谱</td></tr>';
      return;
    }
    body.innerHTML = items.map(renderScore).join("");
  } catch (error) {
    body.innerHTML = '<tr><td colspan="6" class="recommended-empty">加载失败，点击“刷新列表”重试。</td></tr>';
    if (status) status.textContent = "加载失败";
    showToast(`推荐谱加载失败：${error.message}`, "danger");
  }
}

function renderScore(score) {
  const cached = score.isCached === true;
  const instrumentLabel = score.instrument === "piano" ? "🎹 Piano" : score.instrument === "keyboard" ? "⌨️ 键盘谱" : score.instrument === "guitar" ? "🎸 吉他谱" : "❔ 未知类型";
  const size = formatBytes(score.sizeBytes);
  const subtitle = score.difficultyLabel
    ? `等级 ${score.difficultyLevel} · ${escapeHtml(score.difficultyLabel)}`
    : `等级 ${score.difficultyLevel}`;
  return `<tr>
    <td><span class="recommended-difficulty">${escapeHtml(score.displayDifficulty || String(score.difficultyLevel))}</span></td>
    <td><div class="recommended-title">${escapeHtml(score.title)}</div><div class="recommended-subtitle">${subtitle}</div></td>
    <td>${escapeHtml(score.artist || "—")}${score.uploaderName ? `<div class="recommended-subtitle">上传者：${escapeHtml(score.uploaderName)}</div>` : ""}</td>
    <td>${instrumentLabel}</td>
    <td>${size}${cached ? ' <span class="recommended-subtitle">· 已缓存</span>' : ""}</td>
    <td><div class="recommended-row-actions">
      <button class="btn btn-sm btn-primary" data-recommended-action="use" data-id="${escapeHtml(score.id)}">${cached ? "使用缓存" : "下载并使用"}</button>
      ${["piano", "keyboard", "guitar"].includes(score.instrument) ? `<button class="btn btn-sm" data-recommended-action="manual" data-id="${escapeHtml(score.id)}">加入本地曲库…</button>` : ""}
      ${score.instrument === "guitar" ? `<button class="btn btn-sm" data-recommended-action="game-chart" data-id="${escapeHtml(score.id)}">导出游戏谱 PDF</button>` : ""}
    </div></td>
  </tr>`;
}

async function openSubmission() {
  submissionFilePath = "";
  const form = document.getElementById("recommended-submit-form");
  form?.reset();
  const uploader = document.getElementById("recommended-submit-uploader");
  if (uploader) uploader.value = savedSubmissionUploaderName;
  const level = document.getElementById("recommended-submit-level");
  if (level) level.value = "1";
  const file = document.getElementById("recommended-submit-file");
  if (file) file.textContent = "尚未选择";
  document.getElementById("modal-recommended-submit")?.classList.add("open");
  await loadSubmissionUploaderName();
  if (uploader && !uploader.value.trim()) uploader.value = savedSubmissionUploaderName;
}

async function loadSubmissionUploaderName() {
  try {
    const settings = await invoke("settings.get");
    savedSubmissionUploaderName = String(settings?.recommendedSubmissionUploaderName || "").trim();
  } catch (error) {
    showToast(`读取投稿者设置失败：${error.message}`, "warning");
  }
  return savedSubmissionUploaderName;
}

async function saveSubmissionUploaderName(value) {
  const normalized = String(value || "").trim();
  const result = await invoke("recommended.uploaderName.update", { uploaderName: normalized });
  savedSubmissionUploaderName = String(result?.recommendedSubmissionUploaderName ?? normalized);
  return savedSubmissionUploaderName;
}

async function pickSubmissionMidi() {
  try {
    const selected = await invoke("recommended.pickSubmissionMidi");
    if (!selected?.filePath) return;
    submissionFilePath = selected.filePath;
    const file = document.getElementById("recommended-submit-file");
    if (file) file.textContent = selected.fileName || submissionFilePath.split(/[\\/]/).pop();
    const title = document.getElementById("recommended-submit-title");
    if (title && !title.value.trim()) title.value = (selected.fileName || "").replace(/\.midi?$/i, "");
  } catch (error) {
    showToast(`选择 MIDI 失败：${error.message}`, "danger");
  }
}

async function submitScore(event) {
  event.preventDefault();
  if (!submissionFilePath) {
    showToast("请先选择 MIDI 文件", "warning");
    return;
  }
  const button = document.getElementById("btn-recommended-submit-confirm");
  button.disabled = true;
  button.textContent = "正在提交…";
  try {
    await saveSubmissionUploaderName(document.getElementById("recommended-submit-uploader").value);
    await invoke("recommended.submit", {
      title: document.getElementById("recommended-submit-title").value.trim(),
      uploaderName: document.getElementById("recommended-submit-uploader").value.trim(),
      instrument: document.getElementById("recommended-submit-instrument").value,
      difficultyLevel: Number(document.getElementById("recommended-submit-level").value),
      difficultyLabel: document.getElementById("recommended-submit-label").value.trim(),
      filePath: submissionFilePath
    });
    document.getElementById("modal-recommended-submit")?.classList.remove("open");
    showToast("投稿已提交，审核通过后会加入推荐谱", "success");
  } catch (error) {
    showToast(`投稿失败：${error.message}`, "danger");
  } finally {
    button.disabled = false;
    button.textContent = "提交审核";
  }
}

async function onTableClick(event) {
  const button = event.target.closest("button[data-recommended-action]");
  if (!button) return;
  const id = button.dataset.id;
  const action = button.dataset.recommendedAction;
  if (!id || !action) return;
  const oldText = button.textContent;
  button.disabled = true;
  button.textContent = action === "use" ? "正在准备…" : action === "game-chart" ? "正在生成…" : "正在下载…";
  try {
    if (action === "use") {
      const result = await invoke("recommended.use", { id });
      showToast(result.instrument === "piano" ? `${result.name} 已加入 Piano 曲库，可在游戏钢琴中选择` : `${result.name} 已加入本地曲库并载入播放区`, "success");
      window.dispatchEvent(new CustomEvent("nte:reload-library"));
      loadRecommendedScores();
      return;
    }
    if (action === "game-chart") {
      const prepared = await invoke("recommended.prepareGameChart", { id });
      await invoke("library.generateGuitarChart", { id: prepared.id });
      const exported = await invoke("guitarchart.exportPdf", { id: prepared.id });
      if (exported?.success) showToast(`游戏专用吉他谱已导出：${exported.filePath}`, "success");
      else if (!exported?.canceled) showToast(exported?.error || "游戏谱已生成并保存到曲库", "danger");
      else showToast("已生成并保存到曲库；已取消 PDF 导出", "success");
      return;
    }
    const result = await invoke("recommended.prepareManual", { id });
    window.dispatchEvent(new CustomEvent("nte:recommended-manual-convert", { detail: result }));
    showToast(result.wasCached ? "已使用本地缓存，打开手动转换" : "下载完成，打开手动转换", "success");
  } catch (error) {
      showToast(`推荐谱操作失败：${error.message}`, "danger");
  } finally {
    button.disabled = false;
    button.textContent = oldText;
  }
}

function formatBytes(size) {
  if (!Number.isFinite(size) || size <= 0) return "—";
  return size < 1024 * 1024 ? `${Math.max(1, Math.round(size / 1024))} KB` : `${(size / 1024 / 1024).toFixed(1)} MB`;
}

function escapeHtml(value) {
  return String(value).replace(/[&<>"']/g, (character) => ({
    "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;"
  })[character]);
}

