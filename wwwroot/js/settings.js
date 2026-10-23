// wwwroot/js/settings.js
import { invoke } from "./bridge.js";
import { closeModal, openModal, showToast } from "./app.js";

export function initSettings() {
  document.getElementById("btn-author-appreciation")?.addEventListener("click", () => {
    openModal("modal-author-appreciation");
  });
  window.addEventListener("keydown", (event) => {
    const modal = document.getElementById("modal-author-appreciation");
    if (event.key === "Escape" && modal?.classList.contains("open")) closeModal();
  });

  const avoidStrumInputs = ["chk-avoid-strum", "chk-avoid-strum-mode1", "chk-avoid-strum-mode3"]
    .map((id) => document.getElementById(id)).filter(Boolean);
  for (const checkbox of avoidStrumInputs) {
    checkbox.closest("label")?.addEventListener("click", (event) => event.stopPropagation());
    checkbox.addEventListener("change", () => {
      for (const other of avoidStrumInputs) other.checked = checkbox.checked;
      invoke("settings.update", { key: "avoidStrum", value: checkbox.checked })
        .catch((err) => { loadSettings(); showToast(`保存失败: ${err.message}`, "danger"); });
    });
  }
  for (const [id, key] of [["chk-arranged-accompaniment", "enableArrangedAccompaniment"]]) {
    const checkbox = document.getElementById(id);
    checkbox?.addEventListener("change", () => {
      invoke("settings.update", { key, value: checkbox.checked })
        .catch((err) => showToast(`保存失败: ${err.message}`, "danger"));
    });
  }
  const virtualGamepad = document.getElementById("chk-virtual-gamepad");
  const repairVirtualGamepad = document.getElementById("btn-repair-virtual-gamepad");
  repairVirtualGamepad?.addEventListener("click", async () => {
    repairVirtualGamepad.disabled = true;
    const originalLabel = repairVirtualGamepad.textContent;
    repairVirtualGamepad.textContent = "正在准备修复程序…";
    try {
      const result = await invoke("gamepad.repair");
      showToast(result.message, result.canceled ? "warning" : "success");
    } catch (err) {
      showToast(err.message || "模拟手柄修复失败。", "danger");
    } finally {
      repairVirtualGamepad.disabled = false;
      repairVirtualGamepad.textContent = originalLabel;
    }
  });
  virtualGamepad?.addEventListener("change", async () => {
    try {
      const settings = await invoke("settings.update", {
        key: "useVirtualGamepad",
        value: virtualGamepad.checked
      });
      applySettingsToUi(settings);
      showToast(virtualGamepad.checked ? "已启用虚拟手柄输入" : "已切回键鼠输入", "success");
    } catch (err) {
      showToast(err.message || "输入模式设置保存失败", "danger");
      loadSettings();
    }
  });
  loadSettings();
  initWallpaperSettings();
  let pendingKeyBinding = null;

  document.querySelectorAll("[data-key-capture]").forEach((button) => {
    button.dataset.idleText = button.textContent;
    button.addEventListener("click", () => {
      pendingKeyBinding = button.dataset.keyCapture;
      button.textContent = "请按键…";
      button.classList.add("btn-primary");
    });
  });

  window.addEventListener("keydown", async (event) => {
    if (!pendingKeyBinding) return;
    event.preventDefault();
    event.stopPropagation();
    if (event.ctrlKey || event.altKey || event.metaKey || event.shiftKey) {
      showToast("暂不支持组合键，请只按一个字母键或功能键。", "warning");
      return;
    }
    const settingKey = pendingKeyBinding;
    pendingKeyBinding = null;
    const button = document.querySelector(`[data-key-capture="${settingKey}"]`);
    if (button) {
      button.textContent = button.dataset.idleText || "设置按键";
      button.classList.remove("btn-primary");
    }
    const vk = event.keyCode || event.which;
    try {
      const settings = await invoke("settings.update", { key: settingKey, value: vk });
      applySettingsToUi(settings);
      showToast("快捷键已保存", "success");
    } catch (err) {
      showToast(err.message || "按键设置失败", "danger");
      loadSettings();
    }
  }, true);


  // Export Diagnostics
  document.getElementById("btn-export-diagnostics")?.addEventListener("click", () => {
    invoke("settings.exportDiagnostics")
      .then((path) => {
        showToast(`诊断包已导出至:\n${path}`, "success");
      })
      .catch((err) => {
        showToast(`导出失败: ${err.message}`, "danger");
      });
  });

  // Helper for Stepper wiring
  function wireStepper(valId, minusId, plusId, settingKey, step, min, max) {
    const valEl = document.getElementById(valId);
    document.getElementById(minusId)?.addEventListener("click", () => {
      let cur = parseInt(valEl?.textContent || "0", 10);
      cur = Math.max(min, cur - step);
      if (valEl) valEl.textContent = cur;
      if (settingKey === "minHoldMs") updateMinHoldHint(cur);
      invoke("settings.update", { key: settingKey, value: cur });
    });
    document.getElementById(plusId)?.addEventListener("click", () => {
      let cur = parseInt(valEl?.textContent || "0", 10);
      cur = Math.min(max, cur + step);
      if (valEl) valEl.textContent = cur;
      if (settingKey === "minHoldMs") updateMinHoldHint(cur);
      invoke("settings.update", { key: settingKey, value: cur });
    });
  }

  // 1. PreRoll Stepper (0 ~ 10000ms, step 500)
  wireStepper("val-preroll", "btn-preroll-minus", "btn-preroll-plus", "preRollMs", 500, 0, 10000);

  // 2. Page Gap Stepper (5 ~ 150ms, step 5)
  wireStepper("val-pagegap", "btn-pagegap-minus", "btn-pagegap-plus", "pageInterTapGapMs", 5, 5, 150);

  // 3. Min Hold Stepper (5 ~ 100ms, step 1; warn below the recommended 20ms)
  wireStepper("val-minhold", "btn-minhold-minus", "btn-minhold-plus", "minHoldMs", 1, 5, 100);

  // 4. Slot Settle Stepper (20 ~ 300ms, step 5)
  wireStepper("val-settle", "btn-settle-minus", "btn-settle-plus", "wheelMouseSettleMs", 5, 20, 300);

  // 5. Release Gap Stepper (1 ~ 30ms, step 1)
  wireStepper("val-relgap", "btn-relgap-minus", "btn-relgap-plus", "releaseGapMs", 1, 1, 30);

  // 6. Dense Pulse Stepper (5 ~ 100ms, step 5)
  wireStepper("val-pulse", "btn-pulse-minus", "btn-pulse-plus", "densePulseHoldMs", 5, 5, 100);

  // Reset Timing Defaults
  document.getElementById("btn-reset-timing")?.addEventListener("click", () => {
    invoke("settings.resetTimingDefaults")
      .then((s) => {
        applySettingsToUi(s);
        showToast("已成功恢复为官方推荐时序参数", "success");
      })
      .catch((err) => {
        showToast(`恢复默认失败: ${err.message}`, "danger");
      });
  });

  // Toggle AutoStop on focus loss
  const chkFocus = document.getElementById("chk-autostop-focus");
  chkFocus?.addEventListener("change", (e) => {
    invoke("settings.update", { key: "autoStopOnFocusLoss", value: e.target.checked });
  });

  // Toggle Sustain (bind both #chk-enable-sustain and #chk-perf-sustain)
  const chkSustain = document.getElementById("chk-enable-sustain") || document.getElementById("chk-perf-sustain");
  chkSustain?.addEventListener("change", (e) => {
    invoke("settings.update", { key: "enableSustain", value: e.target.checked });
  });

  const chkDisableGlass = document.getElementById("chk-disable-glass-effect");
  chkDisableGlass?.addEventListener("change", async (event) => {
    const disabled = event.target.checked;
    document.documentElement.classList.toggle("no-glass", disabled);
    try {
      const settings = await invoke("settings.update", { key: "disableGlassEffect", value: disabled });
      applySettingsToUi(settings);
      showToast(disabled ? "已关闭界面毛玻璃效果" : "已启用界面毛玻璃效果", "success");
    } catch (err) {
      showToast(err.message || "毛玻璃设置保存失败", "danger");
      loadSettings();
    }
  });
}

function initWallpaperSettings() {
  const current = document.getElementById("wallpaper-current");
  const warning = document.getElementById("wallpaper-warning");
  const dialog = document.getElementById("wallpaper-preview-dialog");
  const viewport = document.getElementById("wallpaper-preview-viewport");
  const zoom = document.getElementById("wallpaper-zoom");
  const zoomValue = document.getElementById("wallpaper-zoom-value");
  const previewImage = document.getElementById("wallpaper-preview-image");
  const previewVideo = document.getElementById("wallpaper-preview-video");
  const activeLayer = document.getElementById("wallpaper-layer");
  const activeImage = document.getElementById("wallpaper-active-image");
  const activeVideo = document.getElementById("wallpaper-active-video");
  const previewButton = document.getElementById("btn-wallpaper-preview");
  const clearButton = document.getElementById("btn-wallpaper-clear");
  const applyButton = document.getElementById("btn-wallpaper-apply");
  const selectButton = document.getElementById("btn-wallpaper-select");
  if (!dialog || !viewport || !activeLayer) return;

  let saved = null;
  let staged = null;
  let dragging = null;
  let applying = false;
  const cancelButtons = [document.getElementById("btn-wallpaper-cancel"), document.getElementById("btn-wallpaper-cancel-top")];

  function showWarning(message = "") {
    warning.textContent = message;
    warning.hidden = !message;
  }

  function fallbackWallpaper(message) {
    activeVideo.pause();
    activeLayer.hidden = true;
    document.documentElement.classList.remove("has-wallpaper");
    showWarning(message);
  }

  function setMedia(elementImage, elementVideo, media) {
    elementImage.hidden = true;
    elementVideo.hidden = true;
    elementImage.removeAttribute("src");
    elementVideo.pause();
    elementVideo.removeAttribute("src");
    if (!media?.wallpaperMediaUrl) return;
    if (media.wallpaperMediaType === "video") {
      elementVideo.src = media.wallpaperMediaUrl;
      elementVideo.hidden = false;
      elementVideo.muted = true;
      elementVideo.loop = true;
      elementVideo.playsInline = true;
      elementVideo.play().catch(() => {});
    } else {
      elementImage.src = media.wallpaperMediaUrl;
      elementImage.hidden = false;
    }
  }

  function paint(elementImage, elementVideo, media, viewportElement) {
    if (!media) return;
    const x = Math.max(-1, Math.min(1, Number(media.wallpaperOffsetX) || 0));
    const y = Math.max(-1, Math.min(1, Number(media.wallpaperOffsetY) || 0));
    const scale = Math.max(1, Math.min(3, Number(media.wallpaperScale) || 1));
    for (const element of [elementImage, elementVideo]) {
      element.style.objectPosition = `${50 + x * 50}% ${50 + y * 50}%`;
      element.style.transform = `scale(${scale})`;
    }
  }

  function updateCurrent() {
    const enabled = Boolean(saved?.wallpaperEnabled && saved.wallpaperMediaUrl);
    current.textContent = enabled
      ? `${saved.wallpaperMediaType === "video" ? "已应用视频壁纸" : "已应用图片壁纸"}`
      : "使用默认背景";
    previewButton.disabled = !enabled;
    clearButton.disabled = !enabled;
    if (enabled) {
      activeLayer.hidden = false;
      setMedia(activeImage, activeVideo, saved);
      paint(activeImage, activeVideo, saved, document.documentElement);
      document.documentElement.classList.add("has-wallpaper");
    } else {
      activeVideo.pause();
      activeImage.removeAttribute("src");
      activeVideo.removeAttribute("src");
      activeLayer.hidden = true;
      document.documentElement.classList.remove("has-wallpaper");
    }
  }

  function openPreview(media) {
    staged = { ...media };
    setMedia(previewImage, previewVideo, staged);
    zoom.value = String(Math.max(1, Math.min(3, Number(staged.wallpaperScale) || 1)));
    zoomValue.value = `${Math.round(Number(zoom.value) * 100)}%`;
    dialog.hidden = false;
    requestAnimationFrame(() => paint(previewImage, previewVideo, staged, viewport));
    document.getElementById("btn-wallpaper-cancel").focus();
  }

  function closePreview() {
    if (applying) return;
    dialog.hidden = true;
    staged = null;
    dragging = null;
    setMedia(previewImage, previewVideo, null);
    selectButton.focus();
  }

  function updateStagedPaint() {
    if (!staged) return;
    staged.wallpaperScale = Number(zoom.value);
    zoomValue.value = `${Math.round(staged.wallpaperScale * 100)}%`;
    paint(previewImage, previewVideo, staged, viewport);
  }

  invoke("settings.get").then((settings) => {
    saved = settings;
    updateCurrent();
    if (settings.wallpaperMissing) {
      showWarning("找不到已保存的壁纸素材，请重新选择文件。");
    }
  }).catch((err) => showWarning(err.message || "读取壁纸设置失败。"));

  selectButton.addEventListener("click", async () => {
    showWarning();
    try {
      const imported = await invoke("wallpaper.import");
      if (!imported) return;
      openPreview({
        wallpaperFileName: imported.fileName,
        wallpaperMediaType: imported.mediaType,
        wallpaperMediaUrl: imported.mediaUrl,
        wallpaperScale: 1,
        wallpaperOffsetX: 0,
        wallpaperOffsetY: 0
      });
    } catch (err) {
      showWarning(err.message || "导入壁纸失败，当前壁纸未更改。");
    }
  });

  previewButton.addEventListener("click", () => {
    if (saved?.wallpaperEnabled && saved.wallpaperMediaUrl) openPreview(saved);
  });
  document.getElementById("btn-wallpaper-cancel").addEventListener("click", closePreview);
  document.getElementById("btn-wallpaper-cancel-top").addEventListener("click", closePreview);
  zoom.addEventListener("input", updateStagedPaint);

  viewport.addEventListener("pointerdown", (event) => {
    if (!staged) return;
    dragging = { pointerId: event.pointerId, x: event.clientX, y: event.clientY,
      offsetX: Number(staged.wallpaperOffsetX) || 0, offsetY: Number(staged.wallpaperOffsetY) || 0 };
    viewport.setPointerCapture(event.pointerId);
    event.preventDefault();
  });
  viewport.addEventListener("pointermove", (event) => {
    if (!dragging || dragging.pointerId !== event.pointerId || !staged) return;
    staged.wallpaperOffsetX = Math.max(-1, Math.min(1, dragging.offsetX + (event.clientX - dragging.x) / Math.max(1, viewport.clientWidth * 0.35)));
    staged.wallpaperOffsetY = Math.max(-1, Math.min(1, dragging.offsetY + (event.clientY - dragging.y) / Math.max(1, viewport.clientHeight * 0.35)));
    updateStagedPaint();
  });
  const stopDrag = () => { dragging = null; };
  viewport.addEventListener("pointerup", stopDrag);
  viewport.addEventListener("pointercancel", stopDrag);
  activeVideo.addEventListener("error", () => fallbackWallpaper("壁纸视频无法播放，请尝试 MP4 或 WebM 格式。"));
  activeImage.addEventListener("error", () => fallbackWallpaper("壁纸图片无法读取，请重新选择图片。"));
  previewVideo.addEventListener("error", () => showToast("预览视频无法播放，请尝试 MP4 或 WebM 格式。", "warning"));
  previewImage.addEventListener("error", () => showToast("无法读取所选图片，请选择其他文件。", "warning"));

  applyButton.addEventListener("click", async () => {
    if (!staged || applying) return;
    applying = true;
    applyButton.disabled = true;
    cancelButtons.forEach((button) => { button.disabled = true; });
    try {
      saved = await invoke("wallpaper.apply", {
        fileName: staged.wallpaperFileName,
        mediaType: staged.wallpaperMediaType,
        scale: Number(staged.wallpaperScale) || 1,
        offsetX: Number(staged.wallpaperOffsetX) || 0,
        offsetY: Number(staged.wallpaperOffsetY) || 0
      });
      updateCurrent();
      showWarning();
      applying = false;
      closePreview();
      showToast("界面壁纸已应用并保存在本机", "success");
    } catch (err) {
      showWarning(err.message || "应用壁纸失败，当前壁纸未更改。");
    } finally {
      applying = false;
      applyButton.disabled = false;
      cancelButtons.forEach((button) => { button.disabled = false; });
    }
  });

  clearButton.addEventListener("click", async () => {
    clearButton.disabled = true;
    try {
      saved = await invoke("wallpaper.clear");
      updateCurrent();
      showWarning();
      showToast("已恢复默认界面背景", "success");
    } catch (err) {
      showWarning(err.message || "清除壁纸失败。");
    } finally {
      clearButton.disabled = !saved?.wallpaperEnabled;
    }
  });

  window.addEventListener("keydown", (event) => {
    if (!dialog.hidden && !applying && event.key === "Escape") closePreview();
  });
}

function applySettingsToUi(s) {
  if (!s) return;
  for (const id of ["chk-avoid-strum", "chk-avoid-strum-mode1", "chk-avoid-strum-mode3"]) {
    const avoidStrum = document.getElementById(id);
    if (avoidStrum) avoidStrum.checked = !!s.avoidStrum;
  }
  const arrangedAccompaniment = document.getElementById("chk-arranged-accompaniment");
  if (arrangedAccompaniment) arrangedAccompaniment.checked = s.enableArrangedAccompaniment !== false;
  const virtualGamepad = document.getElementById("chk-virtual-gamepad");
  if (virtualGamepad && s.useVirtualGamepad !== undefined) virtualGamepad.checked = Boolean(s.useVirtualGamepad);
  const setVal = (id, val) => {
    const el = document.getElementById(id);
    if (el) el.textContent = val;
  };

  setVal("val-preroll", s.preRollMs ?? 1500);
  setVal("val-pagegap", s.pageInterTapGapMs ?? 25);
  setVal("val-minhold", s.minHoldMs ?? 20);
  updateMinHoldHint(s.minHoldMs ?? 20);
  setVal("val-settle", s.wheelMouseSettleMs ?? 75);
  setVal("val-relgap", s.releaseGapMs ?? 2);
  setVal("val-pulse", s.densePulseHoldMs ?? 25);
  const hotkeyLabel = formatKey(s.globalHotkeyVk ?? 0x77);
  setVal("label-global-hotkey", hotkeyLabel);
  setVal("label-semi-page-prev", formatKey(s.semiAutoPagePrevVk ?? 0x52));
  setVal("label-semi-page-next", formatKey(s.semiAutoPageNextVk ?? 0x54));
  window.nteStartHotkeyLabel = hotkeyLabel;
  const playButton = document.getElementById("dock-btn-play");
  if (playButton) playButton.title = `播放 / 暂停 (${hotkeyLabel})`;
  const hotkeyBadge = document.getElementById("hint-global-hotkey");
  const hotkeyBadgeLabel = document.getElementById("hint-global-hotkey-label");
  if (hotkeyBadgeLabel) hotkeyBadgeLabel.textContent = hotkeyLabel;
  if (hotkeyBadge) hotkeyBadge.title = `切回游戏前台窗口后按 ${hotkeyLabel} 开始演奏`;
  const footerHotkey = document.getElementById("footer-global-hotkey");
  if (footerHotkey) footerHotkey.textContent = `⚡ 全局热键: ${hotkeyLabel} 播放 / 暂停 / 急停`;

  const chkFocus = document.getElementById("chk-autostop-focus");
  if (chkFocus && s.autoStopOnFocusLoss !== undefined) chkFocus.checked = s.autoStopOnFocusLoss;

  const chkTopMost = document.getElementById("chk-always-on-top");
  if (chkTopMost && s.alwaysOnTop !== undefined) chkTopMost.checked = s.alwaysOnTop;

  const chkDisableGlass = document.getElementById("chk-disable-glass-effect");
  if (chkDisableGlass && s.disableGlassEffect !== undefined) chkDisableGlass.checked = s.disableGlassEffect;
  document.documentElement.classList.toggle("no-glass", Boolean(s.disableGlassEffect));

  const chkSustain = document.getElementById("chk-enable-sustain") || document.getElementById("chk-perf-sustain");
  if (chkSustain && s.enableSustain !== undefined) chkSustain.checked = s.enableSustain;
}

function updateMinHoldHint(value) {
  const hint = document.getElementById("minhold-hint");
  if (!hint) return;
  if (value < 20) {
    hint.textContent = "警告：低于 20 ms 的短按可能无法被游戏稳定识别。";
    hint.style.color = "var(--warning)";
  } else {
    hint.textContent = "单键/琴弦最小按压时长（5–100 ms）";
    hint.style.color = "var(--muted)";
  }
}

function formatKey(vk) {
  if (vk >= 0x70 && vk <= 0x87) return `F${vk - 0x6f}`;
  if (vk >= 0x41 && vk <= 0x5a) return String.fromCharCode(vk);
  return `VK ${vk}`;
}

export function loadSettings() {
  invoke("settings.get")
    .then((s) => {
      applySettingsToUi(s);
    })
    .catch((err) => console.warn(err));
}
