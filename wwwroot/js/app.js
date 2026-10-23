// wwwroot/js/app.js
import { initPlayback } from "./playback.js";
import { initLibrary } from "./library.js";
import { initGuitarSimulator, setSimMode } from "./guitar-simulator.js";
import { initCalibration } from "./calibration.js";
import { initTutorial } from "./tutorial.js";
import { initSettings } from "./settings.js";
import { initAnnouncements } from "./announcements.js";
import { initGamePiano } from "./game-piano.js";
import { initMidiEditor } from "./midi-editor.js";
import { initRecommendedScores } from "./recommended-scores.js";
import { initGtp } from "./gtp.js";
import { invoke } from "./bridge.js";

// Global App State & Router (Unchanged route IDs)
const VALID_PAGES = new Set([
  "library", "audio-transcription", "midi-editor", "recommended-scores", "gtp", "performance", "game-piano", "simulator", "calibration", "tutorial", "settings"
]);
let currentPageId = "";
let enterCleanupTimer = 0;

window.app = {
  showPage(pageId, options = {}) {
    if (!VALID_PAGES.has(pageId)) return false;
    if (pageId === currentPageId) return true;
    if (!options.discardConfirmed && window.nteMidiEditorHasUnsavedChanges?.() && !window.confirm("MIDI 卷帘中有未保存的修改。离开此页面并放弃修改吗？")) return false;

    const targetPage = document.getElementById(`page-${pageId}`);
    const targetNav = document.querySelector(`.nav-item[data-page="${pageId}"]`);
    if (!targetPage) return false;

    // 1. 立即更新导航栏激活态与无障碍属性
    document.querySelectorAll(".nav-item").forEach((nav) => {
      const isMatch = nav.dataset.page === pageId;
      nav.classList.toggle("active", isMatch);
      if (isMatch) nav.setAttribute("aria-current", "page");
      else nav.removeAttribute("aria-current");
    });

    // 2. 切换舞台容器，清理上一次残留的进入动效类（无锁防卡死）
    if (enterCleanupTimer) {
      clearTimeout(enterCleanupTimer);
      enterCleanupTimer = 0;
    }
    document.querySelectorAll(".page-container").forEach((page) => {
      if (page !== targetPage) {
        page.classList.remove("active", "is-entering");
        page.setAttribute("aria-hidden", "true");
      }
    });

    targetPage.classList.remove("is-entering");
    targetPage.classList.add("active");
    targetPage.setAttribute("aria-hidden", "false");

    // 强制触发一次轻量化重排以重启动画
    void targetPage.offsetWidth;
    targetPage.classList.add("is-entering");
    currentPageId = pageId;
    void invoke("ui.setActivePage", { pageId }).catch((error) => console.error("Failed to sync active page:", error));
    window.dispatchEvent(new CustomEvent("nte:page-changed", { detail: { pageId } }));
    return true;

    enterCleanupTimer = window.setTimeout(() => {
      targetPage.classList.remove("is-entering");
      enterCleanupTimer = 0;
    }, 180);

    // 3. 同步顶部标题
    const titleEl = document.getElementById("header-title");
    if (titleEl && targetNav) {
      titleEl.textContent = targetNav.querySelector(".nav-text")?.textContent || "工作台";
    }
  },

  setTheme(theme) {
    const validTheme = theme === "light" ? "light" : "dark";
    document.documentElement.setAttribute("data-theme", validTheme);
    try {
      localStorage.setItem("nte-theme", validTheme);
    } catch (_) {}
    invoke("semiauto.setTheme", { theme: validTheme }).catch(() => {});

    // Update top header toggle button
    const iconEl = document.getElementById("theme-toggle-icon");
    const labelEl = document.getElementById("theme-toggle-label");
    if (iconEl) iconEl.textContent = validTheme === "dark" ? "🌙" : "☀️";
    if (labelEl) labelEl.textContent = validTheme === "dark" ? "黑曜液态" : "极光亮白";

    // Update Settings page theme cards
    const darkCard = document.getElementById("theme-card-dark");
    const lightCard = document.getElementById("theme-card-light");
    if (darkCard && lightCard) {
      darkCard.classList.toggle("active", validTheme === "dark");
      lightCard.classList.toggle("active", validTheme === "light");
    }
  },

  toggleTheme() {
    const current = document.documentElement.getAttribute("data-theme") || "dark";
    this.setTheme(current === "dark" ? "light" : "dark");
  }
};

export function showToast(message, type = "info", durationMs = 3500) {
  const container = document.getElementById("toast-container");
  if (!container) return;

  const toast = document.createElement("div");
  toast.className = `toast ${type}`;
  toast.textContent = message;
  container.appendChild(toast);

  setTimeout(() => {
    toast.style.opacity = "0";
    setTimeout(() => toast.remove(), 200);
  }, durationMs);
}

export function openModal(modalId) {
  const modal = document.getElementById(modalId);
  if (modal) modal.classList.add("open");
}

export function closeModal() {
  document.querySelectorAll(".modal-backdrop").forEach((m) => m.classList.remove("open"));
}

function initTheme() {
  let savedTheme = "dark";
  try {
    savedTheme = localStorage.getItem("nte-theme") || "dark";
  } catch (_) {}
  window.app.setTheme(savedTheme);

  // Header quick toggle button
  document.getElementById("btn-theme-toggle")?.addEventListener("click", () => {
    window.app.toggleTheme();
  });

  // Settings page theme cards
  document.querySelectorAll("[data-theme-select]").forEach((card) => {
    card.addEventListener("click", () => {
      const targetTheme = card.getAttribute("data-theme-select");
      if (targetTheme) {
        window.app.setTheme(targetTheme);
        showToast(
          targetTheme === "dark"
            ? "已切换至：黑曜石液态主题 🌙"
            : "已切换至：极光水滴亮白主题 ☀️",
          "info"
        );
      }
    });
  });
}

function initPerformanceControls() {
  const cardPiano = document.getElementById("mode-card-piano");
  const cardFret = document.getElementById("mode-card-fret");
  const cardChord = document.getElementById("mode-card-chord");
  const cardHybrid = document.getElementById("mode-card-hybrid");
  const cardDrums = document.getElementById("mode-card-drums");
  const drumKeyLayout = document.getElementById("drum-key-layout");
  const drumModeDescription = document.getElementById("drum-mode-description");
  function updateDrumModeDescription() {
    if (!drumModeDescription) return;
    const keys = drumKeyLayout?.value === "ASQuote" ? ["A", "S", ";", "'"] : ["D", "F", "J", "K"];
    drumModeDescription.textContent = `${keys[0]} 底鼓 · ${keys[1]} 军鼓 · ${keys[2]} 轻镲 · ${keys[3]} 重镲；自动识别 MIDI 鼓轨，也能从旋律生成节奏。`;
  }
  let modeSelectionVersion = 0;
  let modeUpdateQueue = Promise.resolve();

  function selectMode(mode) {
    if (cardPiano) cardPiano.classList.toggle("active", mode === 0);
    if (cardFret) cardFret.classList.toggle("active", mode === 1);
    if (cardChord) cardChord.classList.toggle("active", mode === 2);
    if (cardHybrid) cardHybrid.classList.toggle("active", mode === 3);
    if (cardDrums) cardDrums.classList.toggle("active", mode === 4);

    if (mode === 0) {
      setSimMode("Piano");
      showToast("已切换至：键盘脚本模式（36 键）", "info");
    } else if (mode === 1) {
      setSimMode("Fret");
      showToast("已切换至：吉他品格独奏模式 (Mode 1)", "info");
    } else if (mode === 2) {
      setSimMode("Chord");
      showToast("已切换至：吉他 24 和弦伴奏模式 (Mode 2)", "info");
    } else if (mode === 3) {
      setSimMode("Fret");
      showToast("已切换至：吉他完整混合演奏 (Mode 3)", "info");
    } else if (mode === 4) {
      setSimMode("Piano");
      showToast("已切换至：四键鼓点模式", "info");
    }
    const requestVersion = ++modeSelectionVersion;
    const update = modeUpdateQueue.then(() => invoke("settings.update", { key: "playMode", value: mode }));
    modeUpdateQueue = update.catch(() => {});
    update.then(() => {
      if (requestVersion === modeSelectionVersion) {
        window.dispatchEvent(new CustomEvent("nte:performance-mode-selected", { detail: { mode } }));
      }
    }).catch(() => {});
  }

  cardPiano?.addEventListener("click", (e) => {
    if (e.target.id === "btn-perf-preset-editor") return;
    selectMode(0);
  });

  cardFret?.addEventListener("click", () => {
    selectMode(1);
  });

  cardChord?.addEventListener("click", () => {
    selectMode(2);
  });
  cardHybrid?.addEventListener("click", () => {
    selectMode(3);
  });
  cardDrums?.addEventListener("click", () => selectMode(4));
  drumKeyLayout?.addEventListener("click", (event) => event.stopPropagation());
  drumKeyLayout?.addEventListener("change", () => {
    updateDrumModeDescription();
    invoke("settings.update", { key: "drumKeyLayout", value: drumKeyLayout.value })
      .then(() => showToast(`鼓点键位：${drumKeyLayout.value === "ASQuote" ? "A / S / ; / '" : "D / F / J / K"}`, "info"))
      .catch((error) => showToast(`保存鼓点键位失败：${error.message}`, "danger"));
  });

  // Open 36-key Preset Editor Dialog
  document.getElementById("btn-perf-preset-editor")?.addEventListener("click", (e) => {
    e.stopPropagation();
    invoke("tools.openPresetEditor")
      .then(() => showToast("已启动 36 键按键映射编辑器", "info"))
      .catch((err) => showToast(`打开失败: ${err.message}`, "danger"));
  });

  // Open Compatibility Test Dialog
  document.getElementById("btn-perf-compat-test")?.addEventListener("click", () => {
    invoke("tools.openCompatibilityTest")
      .then(() => showToast("已启动游戏按键兼容性测试向导", "info"))
      .catch((err) => showToast(`打开失败: ${err.message}`, "danger"));
  });

  // Open / Toggle V3.0 SemiAuto Guitar Overlay Window
  document.getElementById("btn-semiauto-overlay")?.addEventListener("click", () => {
    invoke("semiauto.toggle")
      .then((res) => showToast(res?.visible ? "已显示半自动琴谱悬浮窗 (F9)" : "已隐藏悬浮窗 (F9)", "info"))
      .catch((err) => showToast(`操作失败: ${err.message}`, "danger"));
  });

  // Open Advanced Wheel Calibration Dialog
  document.getElementById("btn-calib-open-dialog")?.addEventListener("click", () => {
    invoke("tools.openWheelCalibration")
      .then(() => showToast("已启动高级轮盘校准向导", "info"))
      .catch((err) => showToast(`打开失败: ${err.message}`, "danger"));
  });

  // Sync initial mode from settings
  invoke("settings.get").then((s) => {
    if (s && s.playMode !== undefined) {
      if (cardPiano) cardPiano.classList.toggle("active", s.playMode === 0);
      if (cardFret) cardFret.classList.toggle("active", s.playMode === 1);
      if (cardChord) cardChord.classList.toggle("active", s.playMode === 2);
      if (cardHybrid) cardHybrid.classList.toggle("active", s.playMode === 3);
      if (cardDrums) cardDrums.classList.toggle("active", s.playMode === 4);
      if (drumKeyLayout && ["DFJK", "ASQuote"].includes(s.drumKeyLayout)) {
        drumKeyLayout.value = s.drumKeyLayout;
        updateDrumModeDescription();
      }
    }
  }).catch(() => {});

  let perfTrans = 0;
  const valTrans = document.getElementById("perf-val-trans");
  document.getElementById("btn-perf-trans-minus")?.addEventListener("click", () => {
    perfTrans = Math.max(-24, perfTrans - 1);
    if (valTrans) valTrans.textContent = `${perfTrans >= 0 ? "+" : ""}${perfTrans}`;
    invoke("settings.update", { key: "transpose", value: perfTrans }).catch(() => {});
  });

  document.getElementById("btn-perf-trans-plus")?.addEventListener("click", () => {
    perfTrans = Math.min(24, perfTrans + 1);
    if (valTrans) valTrans.textContent = `${perfTrans >= 0 ? "+" : ""}${perfTrans}`;
    invoke("settings.update", { key: "transpose", value: perfTrans }).catch(() => {});
  });
}

function initWindowSizeLock() {
  let isWindowLocked = false;
  const btnSizeLock = document.getElementById("btn-win-sizelock");

  function renderSizeLockButton(locked) {
    isWindowLocked = Boolean(locked);
    if (!btnSizeLock) return;
    btnSizeLock.setAttribute("aria-pressed", String(isWindowLocked));
    const iconEl = document.getElementById("win-sizelock-icon");
    const labelEl = document.getElementById("win-sizelock-label");
    if (iconEl) iconEl.textContent = isWindowLocked ? "🔒" : "⤢";
    if (labelEl) labelEl.textContent = isWindowLocked ? "已固定窗口" : "自由尺寸";
  }

  btnSizeLock?.addEventListener("click", async () => {
    const targetLocked = !isWindowLocked;
    btnSizeLock.disabled = true;
    try {
      const res = await invoke("window.setSizeLock", { locked: targetLocked });
      renderSizeLockButton(res?.locked ?? targetLocked);
      showToast(
        targetLocked ? "🔒 窗口尺寸已固定，防止误触拉伸" : "⤢ 已解锁，可自由拖拽窗口边缘调整大小",
        "info"
      );
    } catch (err) {
      showToast(`切换窗口尺寸模式失败: ${err.message}`, "danger");
    } finally {
      btnSizeLock.disabled = false;
    }
  });

  window.addEventListener("nte:window-size-mode", (e) => {
    if (e.detail && e.detail.locked !== undefined) {
      renderSizeLockButton(e.detail.locked);
    }
  });
}

function initWindowTopMost() {
  let isTopMost = false;
  const btnTopMost = document.getElementById("btn-win-topmost");
  const chkTopMost = document.getElementById("chk-always-on-top");

  function renderTopMost(enabled) {
    isTopMost = Boolean(enabled);
    if (btnTopMost) {
      btnTopMost.setAttribute("aria-pressed", String(isTopMost));
      const labelEl = document.getElementById("win-topmost-label");
      if (labelEl) labelEl.textContent = isTopMost ? "程序置顶: 开" : "程序置顶: 关";
      btnTopMost.title = isTopMost
        ? "程序置顶已开启：软件始终浮于顶层，且已启用防夺焦机制（点击界面不抢夺游戏焦点）"
        : "程序置顶已关闭：点击可开启始终置顶且不抢占游戏鼠标焦点";
    }
    if (chkTopMost && chkTopMost.checked !== isTopMost) {
      chkTopMost.checked = isTopMost;
    }
  }

  async function toggleTopMost(targetState) {
    if (btnTopMost) btnTopMost.disabled = true;
    try {
      const res = await invoke("window.setTopMost", { enabled: targetState });
      const newState = res?.enabled ?? targetState;
      renderTopMost(newState);
      showToast(
        newState
          ? "程序置顶已开启：软件始终浮于顶层，点击界面不夺取游戏鼠标焦点"
          : "程序置顶已关闭：已恢复为常规窗口层级",
        "info"
      );
    } catch (err) {
      showToast(`切换程序置顶失败: ${err.message}`, "danger");
    } finally {
      if (btnTopMost) btnTopMost.disabled = false;
    }
  }

  btnTopMost?.addEventListener("click", () => {
    toggleTopMost(!isTopMost);
  });

  chkTopMost?.addEventListener("change", (e) => {
    toggleTopMost(e.target.checked);
  });

  window.addEventListener("nte:window-topmost-changed", (e) => {
    if (e.detail && e.detail.enabled !== undefined) {
      renderTopMost(e.detail.enabled);
    }
  });

  // 读取初始状态
  invoke("window.getTopMostState")
    .then((res) => {
      if (res && res.enabled !== undefined) {
        renderTopMost(res.enabled);
      }
    })
    .catch(() => {});
}

function initWindowResetSize() {
  const btnHeaderReset = document.getElementById("btn-win-reset-size");
  const btnSettingsReset = document.getElementById("btn-settings-reset-size");

  async function handleResetSize(triggerBtn) {
    if (triggerBtn) triggerBtn.disabled = true;
    try {
      const res = await invoke("window.resetToRecommendedSize");
      const w = res?.width ?? 1080;
      const h = res?.height ?? 700;
      showToast(`已恢复至推荐分辨率 (${w} × ${h}) 并居中显示`, "info");
    } catch (err) {
      showToast(`恢复推荐分辨率失败: ${err.message}`, "danger");
    } finally {
      if (triggerBtn) triggerBtn.disabled = false;
    }
  }

  btnHeaderReset?.addEventListener("click", () => handleResetSize(btnHeaderReset));
  btnSettingsReset?.addEventListener("click", () => handleResetSize(btnSettingsReset));
}

document.addEventListener("DOMContentLoaded", () => {
  // Initialize Dual Liquid Glass Theme
  initTheme();

  // Window size lock, TopMost & Reset size
  initWindowSizeLock();
  initWindowTopMost();
  initWindowResetSize();

  // Navigation
  document.querySelectorAll(".nav-item").forEach((item) => {
    item.addEventListener("click", () => {
      const page = item.dataset.page;
      if (page) window.app.showPage(page);
    });
  });

  // Modal Close buttons
  document.querySelectorAll(".btn-close-modal").forEach((btn) => {
    btn.addEventListener("click", closeModal);
  });

  document.querySelectorAll(".modal-backdrop").forEach((bg) => {
    bg.addEventListener("click", (e) => {
      if (e.target === bg) closeModal();
    });
  });

  // Window control buttons
  document.getElementById("btn-win-minimize")?.addEventListener("click", () => {
    invoke("window.minimize");
  });
  document.getElementById("btn-win-close")?.addEventListener("click", () => {
    if (window.nteMidiEditorHasUnsavedChanges?.() && !window.confirm("MIDI 卷帘中有未保存的修改。仍要关闭程序吗？")) return;
    invoke("window.close");
  });

  // Submodules init
  initPerformanceControls();
  initPlayback();
  initLibrary();
  initRecommendedScores();
  initGtp();
  initGamePiano();
  initMidiEditor();
  initGuitarSimulator();
  initCalibration();
  initTutorial();
  initSettings();
  initAnnouncements();
  // Default Page
  window.app.showPage("library");
});
