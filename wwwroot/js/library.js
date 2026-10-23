// wwwroot/js/library.js
import { invoke } from "./bridge.js";
import { showToast, openModal, closeModal } from "./app.js";

let currentFilter = "all";
let currentSearch = "";
let selectedSongId = null;
let currentSongList = [];
let generatedGameChartSongId = null;
let libraryRequestVersion = 0;
let performanceModeSongRequestVersion = 0;
let submissionInProgress = false;
let savedSubmissionUploaderName = "";

export function initLibrary() {
  const submissionUploader = document.getElementById("lib-submission-uploader");
  const submitRecommendedButton = document.getElementById("btn-submit-selected-recommended");
  void loadSubmissionUploaderName();
  submissionUploader?.addEventListener("input", updateRecommendedSubmitButton);
  submissionUploader?.addEventListener("change", () => {
    saveSubmissionUploaderName(submissionUploader.value).catch((error) => {
      showSubmissionStatus(`保存投稿者失败：${error.message}`, true);
      submissionUploader.value = savedSubmissionUploaderName;
      updateRecommendedSubmitButton();
    });
  });
  submitRecommendedButton?.addEventListener("click", submitSelectedRecommendedScore);

  window.addEventListener("nte:reload-library", loadLibrary);
  window.addEventListener("nte:performance-mode-selected", async (event) => {
    const requestVersion = ++performanceModeSongRequestVersion;
    const mode = Number(event.detail?.mode);
    const instrument = mode === 0 ? "keyboard" : mode === 4 ? "all" : "guitar";
    const instrumentLabel = instrument === "keyboard" ? "键盘谱" : instrument === "guitar" ? "吉他谱" : "曲目";

    try {
      const playbackState = await invoke("playback.getState");
      if (requestVersion !== performanceModeSongRequestVersion) return;
      const currentId = playbackState?.song?.id;
      // 鼓点模式可从任意 MIDI 曲目生成节奏，无需更换已载入的歌曲。
      if (mode === 4 && currentId) return;

      const data = await invoke("library.list", { filter: instrument, query: "" });
      if (requestVersion !== performanceModeSongRequestVersion) return;

      const songs = Array.isArray(data?.items) ? data.items : [];
      if (songs.length === 0) {
        showToast(`当前演奏模式没有可用的${instrumentLabel}，请先导入对应曲目。`, "warning");
        return;
      }

      if (songs.some((song) => song.id === currentId)) return;
      const candidates = songs.filter((song) => song.id !== currentId);
      const pool = candidates.length > 0 ? candidates : songs;
      const nextSong = pool[Math.floor(Math.random() * pool.length)];

      await invoke("playback.load", { id: nextSong.id });
      if (requestVersion !== performanceModeSongRequestVersion) return;

      selectedSongId = nextSong.id;
      if (currentSongList.some((song) => song.id === nextSong.id)) {
        renderSongTable(currentSongList);
      }
      showToast(`已切换至${instrumentLabel}：${nextSong.name}`, "success");
    } catch (error) {
      if (requestVersion === performanceModeSongRequestVersion) {
        showToast(`自动准备对应模式曲目失败：${error.message}`, "warning");
      }
    }
  });
  window.addEventListener("nte:open-piano-import", () => {
    resetImportForm(2);
    openModal("modal-import");
  });
  window.addEventListener("nte:recommended-manual-convert", async (event) => {
    const source = event.detail;
    if (!source?.filePath) return;
    const requestVersion = ++importSessionVersion;
    try {
      const info = await invoke("library.parseMidi", { filePath: source.filePath });
      if (requestVersion !== importSessionVersion) return;
      info.fileName = source.fileName || `${source.title || "推荐谱"}.mid`;
      applyParsedMidiToImport(info);
      importInstrument = source.instrument === "keyboard" ? 0 : source.instrument === "piano" ? 2 : 1;
      setImportInstrument(importInstrument);
      openModal("modal-import");
    } catch (error) {
      showToast(`打开推荐谱手动转换失败：${error.message}`, "danger");
    }
  });

  const filterBtns = document.querySelectorAll(".lib-filter-btn");
  filterBtns.forEach((btn) => {
    btn.addEventListener("click", () => {
      filterBtns.forEach((b) => b.classList.remove("active"));
      btn.classList.add("active");
      currentFilter = btn.dataset.filter || "all";
      loadLibrary();
    });
  });

  const searchInput = document.getElementById("lib-search-input");
  searchInput?.addEventListener("input", (e) => {
    currentSearch = e.target.value.trim();
    loadLibrary();
  });

  // Action buttons
  document.getElementById("btn-play-selected")?.addEventListener("click", () => {
    if (!selectedSongId) {
      showToast("请先在列表中选中一首曲目", "warning");
      return;
    }
    if (currentSongList.find((song) => song.id === selectedSongId)?.instrument === "piano") {
      showToast("Piano 曲目请在游戏钢琴页面选择并演奏。", "info");
      document.querySelector('.nav-item[data-page="game-piano"]')?.click();
      window.dispatchEvent(new CustomEvent("nte:select-piano-song", { detail: { songId: selectedSongId } }));
      return;
    }
    invoke("playback.load", { id: selectedSongId }).then(() => {
      invoke("playback.play");
    });
  });

  document.getElementById("btn-edit-selected")?.addEventListener("click", () => {
    if (!selectedSongId) {
      showToast("请先在列表中选中一首曲目", "warning");
      return;
    }
    window.dispatchEvent(new CustomEvent("nte:editor-open-song", { detail: { songId: selectedSongId } }));
  });

  document.getElementById("btn-midi-roll-edit")?.addEventListener("click", async () => {
    try {
      const state = await invoke("playback.getState");
      const songId = state?.song?.id || selectedSongId;
      if (!songId) {
        showToast("请先选择曲目，或先载入一首 MIDI。", "warning");
        return;
      }
    window.dispatchEvent(new CustomEvent("nte:editor-open-song", { detail: { songId } }));
    } catch (error) {
      showToast(`读取当前曲目失败：${error.message}`, "danger");
    }
  });

  document.getElementById("btn-generate-game-chart")?.addEventListener("click", async (event) => {
    if (!selectedSongId) return showToast("请先选择一首吉他曲目", "warning");
    const button = event.currentTarget;
    button.disabled = true;
    try {
      const result = await invoke("library.generateGuitarChart", { id: selectedSongId });
      generatedGameChartSongId = selectedSongId;
      document.getElementById("game-chart-result").textContent = `谱面已保存到曲库：${result.playableNoteCount} 个可演奏，${result.atRiskNoteCount} 个风险，${result.unplayableNoteCount} 个不可演奏。`;
      showToast("游戏吉他谱已保存，请选择执行模式或导出 PDF", "success");
      openModal("modal-game-chart-actions");
    } catch (err) {
      showToast(`生成游戏吉他谱失败：${err.message}`, "danger");
    } finally {
      updateGameChartButton();
    }
  });

  document.getElementById("btn-chart-export-pdf")?.addEventListener("click", async () => {
    if (!generatedGameChartSongId) return;
    try {
      const exported = await invoke("guitarchart.exportPdf", { id: generatedGameChartSongId });
      if (exported?.success) {
        closeModal();
        showToast(`PDF 已导出：${exported.filePath}`, "success");
      } else if (!exported?.canceled) showToast(exported?.error || "PDF 导出失败，曲库中的谱面已保留", "danger");
    } catch (err) { showToast(`PDF 导出失败：${err.message}（曲库谱面仍已保存）`, "danger"); }
  });

  document.getElementById("btn-chart-open-manual")?.addEventListener("click", () => openGeneratedChart("full-manual"));
  document.getElementById("btn-chart-open-semi")?.addEventListener("click", () => openGeneratedChart("semi-auto"));

  document.getElementById("btn-delete-selected")?.addEventListener("click", () => {
    if (!selectedSongId) return;
    if (confirm("确定要从本地曲库删除该曲目吗？")) {
      invoke("library.delete", { id: selectedSongId }).then(() => {
        showToast("曲目已删除", "success");
        selectedSongId = null;
        updateRecommendedSubmitButton();
        loadLibrary();
      });
    }
  });

  document.getElementById("btn-rename-selected")?.addEventListener("click", () => {
    if (!selectedSongId) return;
    const song = currentSongList.find((s) => s.id === selectedSongId);
    const newName = prompt("请输入新的歌曲名称:", song ? song.name : "");
    if (newName && newName.trim()) {
      invoke("library.rename", { id: selectedSongId, name: newName.trim() }).then(() => {
        showToast("重命名成功", "success");
        loadLibrary();
      });
    }
  });

  document.getElementById("btn-seed-builtin")?.addEventListener("click", async () => {
    const btn = document.getElementById("btn-seed-builtin");
    if (btn) btn.disabled = true;
    try {
      const res = await invoke("library.seedBuiltIn", { force: true });
      const count = res?.seededCount ?? 0;
      showToast(count > 0 ? `已成功导入内置曲谱 (新增 ${count} 首)` : "内置曲谱已全量载入，无需重复导入", "success");
      loadLibrary();
    } catch (err) {
      showToast(`载入内置曲谱失败: ${err.message}`, "danger");
    } finally {
      if (btn) btn.disabled = false;
    }
  });

  window.addEventListener("nte:playback-state", (e) => {
    const s = e.detail;
    if (s && (s.state === "stopped" || s.state === "completed" || s.state === "playing")) {
      const btnAuditionTracks = document.getElementById("btn-import-audition-tracks");
      const btnStopAudition = document.getElementById("btn-import-stop-audition");
      if (btnAuditionTracks) btnAuditionTracks.style.display = "inline-flex";
      if (btnStopAudition) btnStopAudition.style.display = "none";

      const btnAuditionRes = document.getElementById("btn-import-audition-result");
      const btnStopResAudition = document.getElementById("btn-import-stop-result-audition");
      if (btnAuditionRes) btnAuditionRes.style.display = "inline-flex";
      if (btnStopResAudition) btnStopResAudition.style.display = "none";

      const btnReconvertAuditionTracks = document.getElementById("btn-reconvert-audition-tracks");
      const btnReconvertStopAudition = document.getElementById("btn-reconvert-stop-audition");
      if (btnReconvertAuditionTracks) btnReconvertAuditionTracks.style.display = "inline-flex";
      if (btnReconvertStopAudition) btnReconvertStopAudition.style.display = "none";

      const btnReconvertAuditionRes = document.getElementById("btn-reconvert-audition-result");
      const btnReconvertStopResAudition = document.getElementById("btn-reconvert-stop-result-audition");
      if (btnReconvertAuditionRes) btnReconvertAuditionRes.style.display = "inline-flex";
      if (btnReconvertStopResAudition) btnReconvertStopResAudition.style.display = "none";
    }
  });

  initImportModal();
  initReconvertModal();
  loadLibrary();
}

async function loadSubmissionUploaderName() {
  const input = document.getElementById("lib-submission-uploader");
  try {
    const settings = await invoke("settings.get");
    savedSubmissionUploaderName = String(settings?.recommendedSubmissionUploaderName || "").trim();
    if (input && !input.value.trim()) input.value = savedSubmissionUploaderName;
    updateRecommendedSubmitButton();
  } catch (error) {
    showSubmissionStatus(`读取投稿者设置失败：${error.message}`, true);
  }
}

async function saveSubmissionUploaderName(value) {
  const normalized = String(value || "").trim();
  const result = await invoke("recommended.uploaderName.update", { uploaderName: normalized });
  savedSubmissionUploaderName = String(result?.recommendedSubmissionUploaderName ?? normalized);
  showSubmissionStatus(savedSubmissionUploaderName ? "投稿者名称已保存" : "请填写投稿者名称", false);
  updateRecommendedSubmitButton();
  return savedSubmissionUploaderName;
}

async function submitSelectedRecommendedScore() {
  const button = document.getElementById("btn-submit-selected-recommended");
  const input = document.getElementById("lib-submission-uploader");
  const songId = selectedSongId;
  if (!button || !input || !songId) return;
  const uploaderName = input.value.trim();
  if (!uploaderName) {
    showSubmissionStatus("请先填写投稿者名称", true);
    input.focus();
    return;
  }

  submissionInProgress = true;
  updateRecommendedSubmitButton();
  button.textContent = "正在提交…";
  showSubmissionStatus("正在上传本地原始 MIDI…", false);
  try {
    await saveSubmissionUploaderName(uploaderName);
    const result = await invoke("library.submitRecommended", { id: songId });
    showSubmissionStatus(`已进入审核队列：${result?.title || "曲目"}`, false);
    showToast("投稿已提交，审核通过后会加入推荐谱", "success");
  } catch (error) {
    showSubmissionStatus(`投稿失败：${error.message}`, true);
    showToast(`投稿失败：${error.message}`, "danger");
  } finally {
    submissionInProgress = false;
    button.textContent = "⬆ 投稿选中曲目";
    updateRecommendedSubmitButton();
  }
}

function updateRecommendedSubmitButton() {
  const button = document.getElementById("btn-submit-selected-recommended");
  const input = document.getElementById("lib-submission-uploader");
  if (!button) return;
  button.disabled = submissionInProgress || !selectedSongId || !input?.value.trim();
}

function showSubmissionStatus(message, isError) {
  const status = document.getElementById("lib-submission-status");
  if (!status) return;
  status.textContent = message;
  status.dataset.state = isError ? "error" : "info";
}

async function openGeneratedChart(mode) {
  if (!generatedGameChartSongId) return;
  try {
    await invoke("semiauto.openWithMode", { songId: generatedGameChartSongId, mode });
    closeModal();
    showToast(mode === "full-manual" ? "已用全手动模式打开游戏吉他谱" : "已用半自动模式打开游戏吉他谱", "success");
  } catch (err) { showToast(`打开悬浮窗失败：${err.message}`, "danger"); }
}

export function loadLibrary() {
  const requestVersion = ++libraryRequestVersion;
  invoke("library.list", { filter: currentFilter, query: currentSearch })
    .then((data) => {
      if (requestVersion !== libraryRequestVersion) return;
      currentSongList = data.items || [];
      renderSongTable(currentSongList);
      updateCounts(data.counts);
    })
    .catch((err) => {
      if (requestVersion !== libraryRequestVersion) return;
      showToast(err.message, "danger");
    });
}

function showImportedSong(songId) {
  selectedSongId = songId;
  currentFilter = "all";
  currentSearch = "";
  const searchInput = document.getElementById("lib-search-input");
  if (searchInput) searchInput.value = "";
  document.querySelectorAll(".lib-filter-btn").forEach((button) => {
    button.classList.toggle("active", button.dataset.filter === "all");
  });
  loadLibrary();
}

function updateCounts(counts) {
  if (!counts) return;
  const countAll = document.getElementById("count-all");
  const countKeyboard = document.getElementById("count-keyboard");
  const countPiano = document.getElementById("count-piano");
  const countGuitar = document.getElementById("count-guitar");

  if (countAll) countAll.textContent = `(${counts.all})`;
  if (countKeyboard) countKeyboard.textContent = `(${counts.keyboard || 0})`;
  if (countPiano) countPiano.textContent = `(${counts.piano})`;
  if (countGuitar) countGuitar.textContent = `(${counts.guitar})`;
}

function renderSongTable(songs) {
  const tbody = document.getElementById("lib-tbody");
  if (!tbody) return;

  tbody.innerHTML = "";
  if (!songs.some((song) => song.id === selectedSongId)) selectedSongId = null;
  updateGameChartButton();
  updateRecommendedSubmitButton();

  if (songs.length === 0) {
    const tr = document.createElement("tr");
    tr.innerHTML = `<td colspan="6" style="text-align:center; padding: 36px; color: var(--muted);">暂无匹配曲目，点击右上角【导入 MIDI】添加</td>`;
    tbody.appendChild(tr);
    return;
  }

  songs.forEach((song) => {
    const tr = document.createElement("tr");
    if (song.id === selectedSongId) tr.classList.add("selected");

    tr.onclick = () => {
      selectedSongId = song.id;
      updateGameChartButton();
      updateRecommendedSubmitButton();
      document.querySelectorAll("#lib-tbody tr").forEach((r) => r.classList.remove("selected"));
      tr.classList.add("selected");
      // Auto-load metadata & advice to display guitar start advice banner on home page immediately
      if (song.instrument !== "piano") invoke("playback.load", { id: song.id }).catch(() => {});
    };

    tr.ondblclick = () => {
      selectedSongId = song.id;
      updateGameChartButton();
      if (song.instrument === "piano") {
        document.querySelector('.nav-item[data-page="game-piano"]')?.click();
        window.dispatchEvent(new CustomEvent("nte:select-piano-song", { detail: { songId: song.id } }));
        return;
      }
      invoke("playback.load", { id: song.id }).then(() => {
        document.querySelector('.nav-item[data-page="simulator"]')?.click();
        return invoke("playback.play");
      });
    };

    const instIcon = song.instrument === "guitar" ? "🎸" : song.instrument === "keyboard" ? "⌨️" : "🎹";
    const instClass = song.instrument === "guitar" ? "guitar" : song.instrument === "keyboard" ? "keyboard" : "piano";

    tr.innerHTML = `
      <td><span class="song-inst-tag ${instClass}">${instIcon} ${song.instrumentName}</span></td>
      <td><div class="song-title-cell">${escapeHtml(song.name)}</div></td>
      <td>${song.durationFormatted}</td>
      <td>${song.noteCount.toLocaleString()}</td>
      <td>${song.transpose >= 0 ? "+" : ""}${song.transpose}</td>
      <td>${song.createdAt}</td>
    `;
    tbody.appendChild(tr);
  });
}

function updateGameChartButton() {
  const button = document.getElementById("btn-generate-game-chart");
  if (!button) return;
  const selected = currentSongList.find((song) => song.id === selectedSongId);
  const editButton = document.getElementById("btn-edit-selected");
  if (editButton) editButton.disabled = !selected;
  button.disabled = !selected || selected.instrument !== "guitar";
  button.title = selected && selected.instrument !== "guitar" ? "仅支持吉他曲目" : "生成并保存游戏专用吉他谱，可切换全手动或半自动执行";
  const playButton = document.getElementById("btn-play-selected");
  if (playButton) {
    playButton.disabled = selected?.instrument === "piano";
    playButton.title = selected?.instrument === "piano" ? "Piano 曲目请在游戏钢琴页面演奏" : "播放选中曲目";
  }
}

// ==================== IMPORT MIDI WORKSHOP ====================
let importFilePath = "";
let importInstrument = 1; // 0: Keyboard script, 1: Guitar, 2: Game Piano source
let importTranspose = 0;
let importOutOfRange = "OctaveFold";
let importMelodyStrategy = "ContinuousMelody";
let importParsedTracks = [];
let isImportAuditioning = false;
let importSessionVersion = 0;

function resetImportForm(instrument) {
  importSessionVersion++;
  if (importComparisonTimer) {
    clearTimeout(importComparisonTimer);
    importComparisonTimer = null;
  }
  importFilePath = "";
  importParsedTracks = [];
  const path = document.getElementById("import-input-path");
  if (path) path.value = "";
  const name = document.getElementById("import-song-name");
  if (name) name.value = "";
  const fileMeta = document.getElementById("import-file-meta-row");
  if (fileMeta) fileMeta.style.display = "none";
  const pathHint = document.getElementById("import-info-box");
  if (pathHint) pathHint.textContent = "支持 .mid / .midi 格式文件";
  const tracks = document.getElementById("import-tracks-tbody");
  if (tracks) tracks.innerHTML = '<tr><td colspan="6" style="text-align:center">请先选择 MIDI 文件</td></tr>';
  const comparison = document.getElementById("import-comparison-box");
  if (comparison) comparison.style.display = "none";
  setImportInstrument(instrument);
}

function initImportModal() {
  const btnOpen = document.getElementById("btn-open-import");
  btnOpen?.addEventListener("click", () => {
    resetImportForm(1);
    openModal("modal-import");
  });

  // Native Windows File Dialog Picker
  const btnBrowse = document.getElementById("btn-browse-midi");
  btnBrowse?.addEventListener("click", () => {
    const requestVersion = importSessionVersion;
    invoke("library.pickFile")
      .then((info) => {
        if (!info || requestVersion !== importSessionVersion) return; // Cancelled or a newer import session opened.
        applyParsedMidiToImport(info);
      })
      .catch((err) => {
        showToast(`选择文件失败: ${err.message}`, "danger");
      });
  });

  // Instrument Selection Cards
  const cardGuitar = document.getElementById("import-card-guitar");
  const cardKeyboard = document.getElementById("import-card-keyboard");
  const cardGamePiano = document.getElementById("import-card-game-piano");

  cardGuitar?.addEventListener("click", () => {
    setImportInstrument(1);
  });

  cardKeyboard?.addEventListener("click", () => setImportInstrument(0));
  cardGamePiano?.addEventListener("click", () => setImportInstrument(2));

  // Transpose Stepper
  const valTranspose = document.getElementById("import-val-transpose");
  document.getElementById("import-btn-transpose-minus")?.addEventListener("click", () => {
    importTranspose--;
    if (valTranspose) valTranspose.textContent = `${importTranspose >= 0 ? "+" : ""}${importTranspose}`;
    scheduleImportComparison();
  });
  document.getElementById("import-btn-transpose-plus")?.addEventListener("click", () => {
    importTranspose++;
    if (valTranspose) valTranspose.textContent = `${importTranspose >= 0 ? "+" : ""}${importTranspose}`;
    scheduleImportComparison();
  });

  // Out of Range Chips
  document.querySelectorAll("#import-range-chips .range-chip").forEach((chip) => {
    chip.addEventListener("click", () => {
      document.querySelectorAll("#import-range-chips .range-chip").forEach((c) => c.classList.remove("active"));
      chip.classList.add("active");
      importOutOfRange = chip.dataset.range || "OctaveFold";
      scheduleImportComparison();
    });
  });

  // Melody Strategy Chips
  document.querySelectorAll("#import-melody-chips .melody-chip").forEach((chip) => {
    chip.addEventListener("click", () => {
      document.querySelectorAll("#import-melody-chips .melody-chip").forEach((c) => c.classList.remove("active"));
      chip.classList.add("active");
      importMelodyStrategy = chip.dataset.strategy || "ContinuousMelody";
      scheduleImportComparison();
    });
  });

  // Track Select All / None / Melody
  document.getElementById("btn-import-tracks-all")?.addEventListener("click", () => {
    document.querySelectorAll(".import-track-cb").forEach((cb) => (cb.checked = true));
    const chkAll = document.getElementById("chk-import-select-all");
    if (chkAll) chkAll.checked = true;
    scheduleImportComparison();
  });

  document.getElementById("btn-import-tracks-none")?.addEventListener("click", () => {
    document.querySelectorAll(".import-track-cb").forEach((cb) => (cb.checked = false));
    const chkAll = document.getElementById("chk-import-select-all");
    if (chkAll) chkAll.checked = false;
    scheduleImportComparison();
  });

  document.getElementById("chk-import-select-all")?.addEventListener("change", (e) => {
    document.querySelectorAll(".import-track-cb").forEach((cb) => (cb.checked = e.target.checked));
    scheduleImportComparison();
  });

  document.getElementById("btn-import-tracks-melody")?.addEventListener("click", () => {
    // 智能选择非打击乐且音符较多的主旋律音轨
    document.querySelectorAll(".import-track-cb").forEach((cb) => {
      const trackId = parseInt(cb.value, 10);
      const track = importParsedTracks.find((t) => t.id === trackId);
      const isPercussion = track && (track.program >= 112 && track.program <= 119);
      cb.checked = !isPercussion && (track ? track.noteCount > 20 : true);
    });
    scheduleImportComparison();
  });

  // Audition Selected Tracks
  const btnAuditionTracks = document.getElementById("btn-import-audition-tracks");
  const btnStopAudition = document.getElementById("btn-import-stop-audition");
  btnAuditionTracks?.addEventListener("click", () => {
    if (!importFilePath) return;
    const enabledTracks = getImportEnabledTracks();
    invoke("playback.auditionCustom", {
      filePath: importFilePath,
      enabledTracks,
      melodyStrategy: "FullSong",
      transpose: importTranspose
    }).then(() => {
      isImportAuditioning = true;
      if (btnAuditionTracks) btnAuditionTracks.style.display = "none";
      if (btnStopAudition) btnStopAudition.style.display = "inline-flex";
      showToast("正在声卡试听所选音轨...", "info");
    }).catch(err => showToast(err.message, "danger"));
  });

  btnStopAudition?.addEventListener("click", () => {
    invoke("playback.stopAudition").then(() => {
      isImportAuditioning = false;
      if (btnAuditionTracks) btnAuditionTracks.style.display = "inline-flex";
      if (btnStopAudition) btnStopAudition.style.display = "none";
    });
  });

  // Audition Extracted Result
  const btnAuditionRes = document.getElementById("btn-import-audition-result");
  const btnStopResAudition = document.getElementById("btn-import-stop-result-audition");
  btnAuditionRes?.addEventListener("click", () => {
    if (!importFilePath) return;
    const enabledTracks = getImportEnabledTracks();
    invoke("playback.auditionCustom", {
      filePath: importFilePath,
      enabledTracks,
      melodyStrategy: importMelodyStrategy,
      transpose: importTranspose
    }).then(() => {
      if (btnAuditionRes) btnAuditionRes.style.display = "none";
      if (btnStopResAudition) btnStopResAudition.style.display = "inline-flex";
      showToast("正在试听提取与算法融合结果...", "info");
    }).catch(err => showToast(err.message, "danger"));
  });

  btnStopResAudition?.addEventListener("click", () => {
    invoke("playback.stopAudition").then(() => {
      if (btnAuditionRes) btnAuditionRes.style.display = "inline-flex";
      if (btnStopResAudition) btnStopResAudition.style.display = "none";
    });
  });

  // Confirm Import
  document.getElementById("btn-confirm-import")?.addEventListener("click", () => {
    const name = document.getElementById("import-song-name")?.value || "";
    if (!importFilePath) {
      showToast("请先选择 MIDI 乐谱文件", "warning");
      return;
    }

    const enabledTracks = getImportEnabledTracks();
    if (importInstrument !== 2 && enabledTracks.length === 0) {
      showToast("请至少保留勾选一条音轨参与演奏", "warning");
      return;
    }

    invoke("playback.stopAudition");

    if (importInstrument === 2) {
      invoke("library.saveRawPianoSource", { filePath: importFilePath, name })
        .then((saved) => {
          showToast(`Piano 曲目已加入本地曲库：${saved.name}`, "success");
          closeModal();
          showImportedSong(saved.id);
          window.dispatchEvent(new CustomEvent("nte:piano-song-imported", { detail: { id: saved.id, name: saved.name } }));
        })
        .catch((err) => showToast(`Piano 导入失败：${err.message}`, "danger"));
      return;
    }

    invoke("library.convertAndSave", {
      filePath: importFilePath,
      name,
      instrument: importInstrument,
      transpose: importTranspose,
      outOfRange: importOutOfRange,
      filterDrum: true,
      enableMelody: importMelodyStrategy !== "FullSong",
      melodyStrategy: importMelodyStrategy,
      enabledTracks: enabledTracks
    })
      .then((saved) => {
        const msg = saved.isReplaced
          ? `曲目已成功覆盖替换原曲: ${saved.name}`
          : `曲目导入成功: ${saved.name}`;
        showToast(msg, "success");
        closeModal();
        showImportedSong(saved.id);
        invoke("playback.load", { id: saved.id });
      })
      .catch((err) => {
        showToast(err.message, "danger");
      });
  });
}

function getImportEnabledTracks() {
  return Array.from(document.querySelectorAll(".import-track-cb:checked")).map((cb) =>
    parseInt(cb.value, 10)
  );
}

let importComparisonTimer = null;
function scheduleImportComparison() {
  if (!importFilePath) return;
  if (importComparisonTimer) clearTimeout(importComparisonTimer);
  importComparisonTimer = setTimeout(() => {
    runImportComparison();
  }, 200);
}

function runImportComparison() {
  if (!importFilePath) return;
  if (importInstrument === 2) {
    const box = document.getElementById("import-comparison-box");
    if (box) box.style.display = "none";
    return;
  }
  const enabledTracks = getImportEnabledTracks();
  const box = document.getElementById("import-comparison-box");

  invoke("library.previewExtraction", {
    filePath: importFilePath,
    instrument: importInstrument,
    transpose: importTranspose,
    outOfRange: importOutOfRange,
    melodyStrategy: importMelodyStrategy,
    enabledTracks
  })
    .then((res) => {
      if (box) box.style.display = "block";
      const setTxt = (id, val) => {
        const el = document.getElementById(id);
        if (el) el.textContent = val;
      };

      setTxt("import-cmp-orig", `${res.originalNoteCount.toLocaleString()} 符 / ${res.originalPitchRange}`);
      setTxt("import-cmp-ext", `${res.extractedNoteCount.toLocaleString()} 符 (精简 ${res.reductionPercent}%)`);
      setTxt("import-cmp-range", `${res.extractedPitchRange} (★${res.melodyScore}分)`);
      setTxt("import-cmp-fit", `${res.rangeFitRate}% (${res.outOfRangeCount}音超限)`);
      setTxt("import-cmp-summary", `💡 [${res.targetInstrumentName}] ${res.strategySummary} · 标准覆盖: ${res.instrumentStandardRange}`);
    })
    .catch((err) => {
      console.warn("Comparison preview error:", err);
    });
}

function setImportInstrument(instrument) {
  importInstrument = instrument;
  document.getElementById("import-card-guitar")?.classList.toggle("active", instrument === 1);
  document.getElementById("import-card-keyboard")?.classList.toggle("active", instrument === 0);
  document.getElementById("import-card-game-piano")?.classList.toggle("active", instrument === 2);
  document.getElementById("modal-import")?.classList.toggle("raw-piano-import", instrument === 2);
  scheduleImportComparison();
}

function applyParsedMidiToImport(info) {
  importFilePath = info.filePath;
  const inputPath = document.getElementById("import-input-path");
  if (inputPath) inputPath.value = info.filePath;

  const songNameInput = document.getElementById("import-song-name");
  if (songNameInput) {
    songNameInput.value = info.fileName.replace(/\.[^/.]+$/, "");
  }

  // Update file info display in file-picker card
  const metaRow = document.getElementById("import-file-meta-row");
  if (metaRow) metaRow.style.display = "flex";

  const tagFilename = document.getElementById("import-info-filename");
  if (tagFilename) tagFilename.textContent = `🎵 ${info.fileName}`;

  const tagNotes = document.getElementById("import-info-notes");
  if (tagNotes) tagNotes.textContent = `⚡ ${info.noteCount.toLocaleString()} 音符`;

  const tagDur = document.getElementById("import-info-duration");
  if (tagDur) tagDur.textContent = `⏱️ ${info.durationFormatted}`;

  const tagTracks = document.getElementById("import-info-tracks");
  if (tagTracks) tagTracks.textContent = `🎹 ${info.trackCount} 轨`;

  const infoBox = document.getElementById("import-info-box");
  if (infoBox) infoBox.textContent = info.filePath;

  // Render tracks table
  importParsedTracks = info.tracks || [];
  const tbody = document.getElementById("import-tracks-tbody");
  if (tbody) {
    tbody.innerHTML = "";
    if (importParsedTracks.length === 0) {
      tbody.innerHTML = `<tr><td colspan="6" style="text-align: center; color: var(--muted); padding: 18px;">无有效音轨数据</td></tr>`;
    } else {
      importParsedTracks.forEach((t) => {
        const tr = document.createElement("tr");
        tr.innerHTML = `
          <td style="text-align: center;"><input type="checkbox" class="import-track-cb" value="${t.id}" checked></td>
          <td><span style="font-weight: 700; color: var(--muted);">#${t.id}</span></td>
          <td><b>${escapeHtml(t.name)}</b> <span style="font-size: 11px; color: var(--muted);">(${escapeHtml(t.instrumentName || "标准乐器")})</span></td>
          <td>${t.noteCount.toLocaleString()}</td>
          <td><span class="track-pitch-badge">${t.pitchRange || "-"}</span></td>
          <td style="text-align: right;"><button class="btn btn-sm btn-import-solo-audition" data-track="${t.id}" type="button" style="padding: 2px 7px; font-size: 11px;">▶ 试听</button></td>
        `;
        tbody.appendChild(tr);
      });

      // Bind track checkbox changes to update comparison
      document.querySelectorAll(".import-track-cb").forEach((cb) => {
        cb.addEventListener("change", () => scheduleImportComparison());
      });

      // Bind single track audition
      document.querySelectorAll(".btn-import-solo-audition").forEach((btn) => {
        btn.addEventListener("click", () => {
          const trackId = parseInt(btn.dataset.track, 10);
          invoke("playback.auditionCustom", {
            filePath: importFilePath,
            enabledTracks: [trackId],
            melodyStrategy: "FullSong",
            transpose: importTranspose
          }).then(() => {
            showToast(`正在试听 Track #${trackId}...`, "info");
          });
        });
      });
    }
  }

  // Trigger initial extraction comparison
  scheduleImportComparison();
}

// ==================== RECONVERT SONG WORKSHOP ====================
let reconvertSongId = null;
let reconvertInstrument = 1;
let reconvertTranspose = 0;
let reconvertOutOfRange = "OctaveFold";
let reconvertMelodyStrategy = "ContinuousMelody";
let reconvertParsedTracks = [];

function initReconvertModal() {
  const btnOpen = document.getElementById("btn-reconvert-selected");
  btnOpen?.addEventListener("click", () => {
    if (!selectedSongId) {
      showToast("请先在列表中选中一首曲目", "warning");
      return;
    }

    reconvertSongId = selectedSongId;
    invoke("library.getSongForReconvert", { id: selectedSongId })
      .then((data) => {
        populateReconvertModal(data);
        openModal("modal-reconvert");
      })
      .catch((err) => {
        showToast(`获取曲目配置失败: ${err.message}`, "danger");
      });
  });

  // Reconvert Instrument Cards
  const cardGuitar = document.getElementById("reconvert-card-guitar");
  const cardPiano = document.getElementById("reconvert-card-piano");

  cardGuitar?.addEventListener("click", () => {
    reconvertInstrument = 1;
    cardGuitar.classList.add("active");
    cardPiano?.classList.remove("active");
    scheduleReconvertComparison();
  });

  cardPiano?.addEventListener("click", () => {
    reconvertInstrument = 0;
    cardPiano.classList.add("active");
    cardGuitar?.classList.remove("active");
    scheduleReconvertComparison();
  });

  // Transpose Stepper
  const valTranspose = document.getElementById("reconvert-val-transpose");
  document.getElementById("reconvert-btn-transpose-minus")?.addEventListener("click", () => {
    reconvertTranspose--;
    if (valTranspose) valTranspose.textContent = `${reconvertTranspose >= 0 ? "+" : ""}${reconvertTranspose}`;
    scheduleReconvertComparison();
  });
  document.getElementById("reconvert-btn-transpose-plus")?.addEventListener("click", () => {
    reconvertTranspose++;
    if (valTranspose) valTranspose.textContent = `${reconvertTranspose >= 0 ? "+" : ""}${reconvertTranspose}`;
    scheduleReconvertComparison();
  });

  // Out of Range Chips
  document.querySelectorAll("#reconvert-range-chips .range-chip").forEach((chip) => {
    chip.addEventListener("click", () => {
      document.querySelectorAll("#reconvert-range-chips .range-chip").forEach((c) => c.classList.remove("active"));
      chip.classList.add("active");
      reconvertOutOfRange = chip.dataset.range || "OctaveFold";
      scheduleReconvertComparison();
    });
  });

  // Melody Strategy Chips
  document.querySelectorAll("#reconvert-melody-chips .melody-chip").forEach((chip) => {
    chip.addEventListener("click", () => {
      document.querySelectorAll("#reconvert-melody-chips .melody-chip").forEach((c) => c.classList.remove("active"));
      chip.classList.add("active");
      reconvertMelodyStrategy = chip.dataset.strategy || "ContinuousMelody";
      scheduleReconvertComparison();
    });
  });

  // Track checklist Select All / None / Melody
  document.getElementById("btn-reconvert-tracks-all")?.addEventListener("click", () => {
    document.querySelectorAll(".reconvert-track-cb").forEach((cb) => (cb.checked = true));
    const chkAll = document.getElementById("chk-reconvert-select-all");
    if (chkAll) chkAll.checked = true;
    scheduleReconvertComparison();
  });

  document.getElementById("btn-reconvert-tracks-none")?.addEventListener("click", () => {
    document.querySelectorAll(".reconvert-track-cb").forEach((cb) => (cb.checked = false));
    const chkAll = document.getElementById("chk-reconvert-select-all");
    if (chkAll) chkAll.checked = false;
    scheduleReconvertComparison();
  });

  document.getElementById("chk-reconvert-select-all")?.addEventListener("change", (e) => {
    document.querySelectorAll(".reconvert-track-cb").forEach((cb) => (cb.checked = e.target.checked));
    scheduleReconvertComparison();
  });

  document.getElementById("btn-reconvert-tracks-melody")?.addEventListener("click", () => {
    document.querySelectorAll(".reconvert-track-cb").forEach((cb) => {
      const trackId = parseInt(cb.value, 10);
      const track = reconvertParsedTracks.find((t) => t.id === trackId);
      const isPercussion = track && (track.program >= 112 && track.program <= 119);
      cb.checked = !isPercussion && (track ? track.noteCount > 20 : true);
    });
    scheduleReconvertComparison();
  });

  // Audition Selected Tracks
  const btnAuditionTracks = document.getElementById("btn-reconvert-audition-tracks");
  const btnStopAudition = document.getElementById("btn-reconvert-stop-audition");
  btnAuditionTracks?.addEventListener("click", () => {
    if (!reconvertSongId) return;
    const enabledTracks = getReconvertEnabledTracks();
    invoke("playback.auditionCustom", {
      songId: reconvertSongId,
      enabledTracks,
      melodyStrategy: "FullSong",
      transpose: reconvertTranspose
    }).then(() => {
      if (btnAuditionTracks) btnAuditionTracks.style.display = "none";
      if (btnStopAudition) btnStopAudition.style.display = "inline-flex";
      showToast("正在声卡试听所选音轨...", "info");
    }).catch(err => showToast(err.message, "danger"));
  });

  btnStopAudition?.addEventListener("click", () => {
    invoke("playback.stopAudition").then(() => {
      if (btnAuditionTracks) btnAuditionTracks.style.display = "inline-flex";
      if (btnStopAudition) btnStopAudition.style.display = "none";
    });
  });

  // Audition Extracted Result
  const btnAuditionRes = document.getElementById("btn-reconvert-audition-result");
  const btnStopResAudition = document.getElementById("btn-reconvert-stop-result-audition");
  btnAuditionRes?.addEventListener("click", () => {
    if (!reconvertSongId) return;
    const enabledTracks = getReconvertEnabledTracks();
    invoke("playback.auditionCustom", {
      songId: reconvertSongId,
      enabledTracks,
      melodyStrategy: reconvertMelodyStrategy,
      transpose: reconvertTranspose
    }).then(() => {
      if (btnAuditionRes) btnAuditionRes.style.display = "none";
      if (btnStopResAudition) btnStopResAudition.style.display = "inline-flex";
      showToast("正在试听提取与算法融合结果...", "info");
    }).catch(err => showToast(err.message, "danger"));
  });

  btnStopResAudition?.addEventListener("click", () => {
    invoke("playback.stopAudition").then(() => {
      if (btnAuditionRes) btnAuditionRes.style.display = "inline-flex";
      if (btnStopResAudition) btnStopResAudition.style.display = "none";
    });
  });

  // Confirm Reconvert
  document.getElementById("btn-confirm-reconvert")?.addEventListener("click", () => {
    const targetSongId = reconvertSongId || selectedSongId;
    if (!targetSongId) {
      showToast("请先在列表中选中待转换的曲目", "warning");
      return;
    }

    const enabledTracks = getReconvertEnabledTracks();
    if (enabledTracks.length === 0) {
      showToast("请至少保留勾选一条音轨参与演奏", "warning");
      return;
    }

    invoke("playback.stopAudition");

    invoke("library.reconvert", {
      id: targetSongId,
      instrument: reconvertInstrument,
      transpose: reconvertTranspose,
      outOfRange: reconvertOutOfRange,
      melodyStrategy: reconvertMelodyStrategy,
      enabledTracks: enabledTracks
    })
      .then((saved) => {
        showToast(`曲目已成功重新转换并覆盖原曲: ${saved.name}`, "success");
        closeModal();
        selectedSongId = saved.id;
        loadLibrary();
        invoke("playback.load", { id: saved.id });
      })
      .catch((err) => {
        showToast(`重新转换失败: ${err.message}`, "danger");
      });
  });
}

function getReconvertEnabledTracks() {
  return Array.from(document.querySelectorAll(".reconvert-track-cb:checked")).map((cb) =>
    parseInt(cb.value, 10)
  );
}

let reconvertComparisonTimer = null;
function scheduleReconvertComparison() {
  if (!reconvertSongId) return;
  if (reconvertComparisonTimer) clearTimeout(reconvertComparisonTimer);
  reconvertComparisonTimer = setTimeout(() => {
    runReconvertComparison();
  }, 200);
}

function runReconvertComparison() {
  if (!reconvertSongId) return;
  const enabledTracks = getReconvertEnabledTracks();
  const box = document.getElementById("reconvert-comparison-box");

  invoke("library.previewExtraction", {
    songId: reconvertSongId,
    instrument: reconvertInstrument,
    transpose: reconvertTranspose,
    outOfRange: reconvertOutOfRange,
    melodyStrategy: reconvertMelodyStrategy,
    enabledTracks
  })
    .then((res) => {
      if (box) box.style.display = "block";
      const setTxt = (id, val) => {
        const el = document.getElementById(id);
        if (el) el.textContent = val;
      };

      setTxt("reconvert-cmp-orig", `${res.originalNoteCount.toLocaleString()} 符 / ${res.originalPitchRange}`);
      setTxt("reconvert-cmp-ext", `${res.extractedNoteCount.toLocaleString()} 符 (精简 ${res.reductionPercent}%)`);
      setTxt("reconvert-cmp-range", `${res.extractedPitchRange} (★${res.melodyScore}分)`);
      setTxt("reconvert-cmp-fit", `${res.rangeFitRate}% (${res.outOfRangeCount}音超限)`);
      setTxt("reconvert-cmp-summary", `💡 [${res.targetInstrumentName}] ${res.strategySummary} · 标准覆盖: ${res.instrumentStandardRange}`);
    })
    .catch((err) => {
      console.warn("Reconvert comparison error:", err);
    });
}

function populateReconvertModal(data) {
  reconvertSongId = data.id;
  const titleEl = document.getElementById("reconvert-song-title");
  const infoEl = document.getElementById("reconvert-song-info");
  if (titleEl) titleEl.textContent = data.name;
  if (infoEl) infoEl.textContent = `源文件: ${data.sourceName} · 哈希: ${data.sourceHash.substring(0, 10)}...`;

  // Target Instrument
  reconvertInstrument = data.targetInstrument ?? 1;
  const cardGuitar = document.getElementById("reconvert-card-guitar");
  const cardPiano = document.getElementById("reconvert-card-piano");
  if (cardGuitar) cardGuitar.classList.toggle("active", reconvertInstrument === 1);
  if (cardPiano) cardPiano.classList.toggle("active", reconvertInstrument === 0);

  // Transpose
  reconvertTranspose = data.transpose || 0;
  const valTranspose = document.getElementById("reconvert-val-transpose");
  if (valTranspose) valTranspose.textContent = `${reconvertTranspose >= 0 ? "+" : ""}${reconvertTranspose}`;

  // Out of Range
  reconvertOutOfRange = data.outOfRange || "OctaveFold";
  document.querySelectorAll("#reconvert-range-chips .range-chip").forEach((chip) => {
    chip.classList.toggle("active", chip.dataset.range === reconvertOutOfRange);
  });

  // Melody Strategy
  reconvertMelodyStrategy = data.melodyStrategy || "ContinuousMelody";
  document.querySelectorAll("#reconvert-melody-chips .melody-chip").forEach((chip) => {
    chip.classList.toggle("active", chip.dataset.strategy === reconvertMelodyStrategy);
  });

  // Render Tracks Table
  reconvertParsedTracks = data.tracks || [];
  const tbody = document.getElementById("reconvert-tracks-tbody");
  if (tbody) {
    tbody.innerHTML = "";
    if (reconvertParsedTracks.length === 0) {
      tbody.innerHTML = `<tr><td colspan="6" style="text-align: center; color: var(--muted); padding: 18px;">该曲目暂无独立音轨元数据</td></tr>`;
    } else {
      const enabledSet = data.enabledTracks ? new Set(data.enabledTracks) : null;
      reconvertParsedTracks.forEach((t) => {
        const isChecked = enabledSet == null || enabledSet.has(t.id);
        const tr = document.createElement("tr");
        tr.innerHTML = `
          <td style="text-align: center;"><input type="checkbox" class="reconvert-track-cb" value="${t.id}" ${isChecked ? "checked" : ""}></td>
          <td><span style="font-weight: 700; color: var(--muted);">#${t.id}</span></td>
          <td><b>${escapeHtml(t.name)}</b> <span style="font-size: 11px; color: var(--muted);">(${escapeHtml(t.instrumentName || "标准乐器")})</span></td>
          <td>${t.noteCount.toLocaleString()}</td>
          <td><span class="track-pitch-badge">${t.pitchRange || "-"}</span></td>
          <td style="text-align: right;"><button class="btn btn-sm btn-reconvert-solo-audition" data-track="${t.id}" type="button" style="padding: 2px 7px; font-size: 11px;">▶ 试听</button></td>
        `;
        tbody.appendChild(tr);
      });

      document.querySelectorAll(".reconvert-track-cb").forEach((cb) => {
        cb.addEventListener("change", () => scheduleReconvertComparison());
      });

      document.querySelectorAll(".btn-reconvert-solo-audition").forEach((btn) => {
        btn.addEventListener("click", () => {
          const trackId = parseInt(btn.dataset.track, 10);
          invoke("playback.auditionCustom", {
            songId: reconvertSongId,
            enabledTracks: [trackId],
            melodyStrategy: "FullSong",
            transpose: reconvertTranspose
          }).then(() => {
            showToast(`正在试听 Track #${trackId}...`, "info");
          });
        });
      });
    }
  }

  // Trigger initial comparison
  scheduleReconvertComparison();
}

function escapeHtml(str) {
  return str.replace(/[&<>'"]/g, 
    tag => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', "'": '&#39;', '"': '&quot;' }[tag] || tag)
  );
}
