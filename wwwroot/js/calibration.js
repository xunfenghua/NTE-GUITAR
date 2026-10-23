// wwwroot/js/calibration.js
import { invoke } from "./bridge.js";
import { showToast } from "./app.js";

let autoCalibrationPreviewState = null;

export function initCalibration() {
  loadCalibrationStatus();

  document.getElementById("btn-calib-rebind")?.addEventListener("click", async (event) => {
    const button = event.currentTarget;
    if (button.disabled) return;
    const originalText = button.textContent;
    button.disabled = true;
    button.textContent = "请切到游戏窗口（最多 8 秒）...";
    showToast("请切换到游戏窗口并保持前台，最多等待 8 秒", "info");
    try {
      const result = await invoke("calibration.rebindGameWindow");
      if (result?.status) renderCalibration(result.status);
      showToast(
        result?.success ? `已重新绑定游戏窗口：${result.status?.window?.title || "游戏窗口"}` : (result?.reason || "未捕获到外部游戏窗口，请重试"),
        result?.success ? "success" : "warning",
      );
    } catch (err) {
      console.error("Failed to rebind game window:", err);
      showToast("重新绑定失败，请重试", "error");
    } finally {
      button.disabled = false;
      button.textContent = originalText;
    }
  });

  document.getElementById("btn-calib-auto-guitar")?.addEventListener("click", async (event) => {
    const button = event.currentTarget;
    if (button.disabled) return;
    button.disabled = true;
    const originalText = button.textContent;
    button.textContent = "正在等待异环吉他界面...";
    setAutoCalibrationStatus("请切换到异环并打开六块吉他界面，保持窗口在前台，最多等待 8 秒。", "info");
    clearAutoCalibrationPreview();
    try {
      const result = await invoke("calibration.autoBindAndDetect");
      if (!result?.success) {
        setAutoCalibrationStatus(result?.reason || "未能识别吉他六块，请使用手动校准。", "error");
        showToast(result?.reason || "未能识别吉他六块", "warning");
        return;
      }
      renderAutoCalibrationPreview(result);
      setAutoCalibrationStatus(result.manualFallback
        ? `已捕获 ${result.title || "异环"} 画面，但自动识别不可靠；请拖动六个编号点校正位置后再确认。`
        : `已识别 ${result.title || "异环"} 的六个槽位，请核对预览后确认。`, result.manualFallback ? "info" : "success");
    } catch (err) {
      console.error("Failed to detect Huanhuan guitar slots:", err);
      setAutoCalibrationStatus(err?.message || "自动校准失败，请重试。", "error");
      showToast("自动校准失败，请重试", "error");
    } finally {
      button.disabled = false;
      button.textContent = originalText;
    }
  });

  document.getElementById("btn-calib-auto-confirm")?.addEventListener("click", async (event) => {
    const button = event.currentTarget;
    if (button.disabled) return;
    button.disabled = true;
    try {
      const candidates = [...(autoCalibrationPreviewState?.candidates || [])].sort((a, b) => a.slot - b.slot);
      const result = await invoke("calibration.confirmAutoCalibration", {
        slotXs: candidates.map((candidate) => candidate.x),
        slotYs: candidates.map((candidate) => candidate.y),
      });
      if (!result?.success) {
        setAutoCalibrationStatus(result?.reason || "保存失败，原坐标已保留。", "error");
        showToast(result?.reason || "保存自动校准失败", "error");
        return;
      }
      renderCalibration(result.status);
      clearAutoCalibrationPreview();
      setAutoCalibrationStatus("异环窗口与吉他槽位坐标已保存。", "success");
      showToast("异环自动绑定与校准完成", "success");
    } catch (err) {
      setAutoCalibrationStatus(err?.message || "保存失败，原坐标已保留。", "error");
      showToast("保存自动校准失败", "error");
    } finally {
      button.disabled = false;
    }
  });

  document.getElementById("btn-calib-auto-cancel")?.addEventListener("click", async () => {
    try { await invoke("calibration.cancelAutoCalibration"); }
    catch (err) { console.warn("Failed to cancel auto calibration:", err); }
    clearAutoCalibrationPreview();
    setAutoCalibrationStatus("已取消；原窗口绑定与坐标未更改。", "info");
  });

  document.getElementById("btn-calib-auto-reset")?.addEventListener("click", () => {
    if (!autoCalibrationPreviewState) return;
    autoCalibrationPreviewState.candidates = autoCalibrationPreviewState.originalCandidates.map((candidate) => ({ ...candidate }));
    renderAutoCalibrationMarkers();
    setAutoCalibrationStatus("已恢复预览初始位置。拖动编号圆点可微调槽位中心。", "info");
  });

  document.getElementById("btn-calib-reset")?.addEventListener("click", () => {
    if (confirm("确定要恢复默认六槽位和扫弦点坐标吗？")) {
      invoke("calibration.resetDefaults").then((data) => {
        showToast("已恢复默认坐标", "success");
        renderCalibration(data);
      });
    }
  });

  document.getElementById("btn-calib-test-strum")?.addEventListener("click", () => {
    invoke("calibration.testSlot", { slot: 0 }).then((ok) => {
      showToast(ok ? "扫弦点测试点击已发送" : "未绑定游戏窗口或发送失败", ok ? "success" : "warning");
    });
  });
}

function setAutoCalibrationStatus(message, kind = "info") {
  const status = document.getElementById("calib-auto-status");
  if (!status) return;
  status.textContent = message || "";
  status.dataset.state = kind;
}

function renderAutoCalibrationPreview(data) {
  const panel = document.getElementById("calib-auto-preview");
  const image = document.getElementById("calib-auto-image");
  if (!panel || !image) return;

  autoCalibrationPreviewState = {
    candidates: (data.candidates || []).map((candidate) => ({ ...candidate })),
    originalCandidates: (data.candidates || []).map((candidate) => ({ ...candidate })),
    clientWidth: data.clientWidth,
    clientHeight: data.clientHeight,
  };
  image.src = data.previewImage;
  const imageMeta = document.getElementById("calib-auto-image-meta");
  if (imageMeta) imageMeta.textContent = `截图范围：游戏客户区 · ${data.clientWidth} × ${data.clientHeight} px`;
  renderAutoCalibrationMarkers();
  const notice = document.getElementById("calib-auto-notice");
  if (notice) notice.textContent = `${data.notice || "请核对绿色标记是否落在对应六块中心。"} 可拖动编号圆点微调，下面显示归一化坐标和客户区像素位置。`;
  panel.hidden = false;
}

function renderAutoCalibrationMarkers() {
  const markers = document.getElementById("calib-auto-markers");
  const coordinates = document.getElementById("calib-auto-coordinates");
  if (!markers || !coordinates || !autoCalibrationPreviewState) return;

  markers.replaceChildren();
  coordinates.replaceChildren();
  [...autoCalibrationPreviewState.candidates]
    .sort((a, b) => a.slot - b.slot)
    .forEach((candidate) => {
      const marker = document.createElement("button");
      marker.type = "button";
      marker.className = "calib-auto-marker";
      marker.textContent = String(candidate.slot);
      marker.style.left = `${candidate.x * 100}%`;
      marker.style.top = `${candidate.y * 100}%`;
      marker.title = candidate.matchScore > 0
        ? `Slot ${candidate.slot} · 内部匹配分 ${Number(candidate.matchScore).toFixed(3)} · 拖动以微调`
        : `Slot ${candidate.slot} · 当前配置位置 · 拖动以手动定位`;
      marker.setAttribute("aria-label", `Slot ${candidate.slot}，X ${candidate.x.toFixed(4)}，Y ${candidate.y.toFixed(4)}。拖动以微调位置`);
      marker.addEventListener("pointerdown", (event) => beginAutoCalibrationMarkerDrag(event, candidate));
      markers.appendChild(marker);

      const row = document.createElement("div");
      row.className = "calib-auto-coordinate-row";
      const pixelX = Math.round(candidate.x * Math.max(0, autoCalibrationPreviewState.clientWidth - 1));
      const pixelY = Math.round(candidate.y * Math.max(0, autoCalibrationPreviewState.clientHeight - 1));
      row.textContent = `Slot ${candidate.slot} · X ${candidate.x.toFixed(4)} · Y ${candidate.y.toFixed(4)} · ${pixelX}, ${pixelY}px`;
      coordinates.appendChild(row);
    });
}

function beginAutoCalibrationMarkerDrag(event, candidate) {
  if (!autoCalibrationPreviewState || event.button !== 0) return;
  const markers = document.getElementById("calib-auto-markers");
  if (!markers) return;
  event.preventDefault();
  markers.setPointerCapture(event.pointerId);

  const move = (moveEvent) => {
    if (moveEvent.pointerId !== event.pointerId) return;
    const bounds = markers.getBoundingClientRect();
    if (!bounds.width || !bounds.height) return;
    candidate.x = clamp01((moveEvent.clientX - bounds.left) / bounds.width);
    candidate.y = clamp01((moveEvent.clientY - bounds.top) / bounds.height);
    renderAutoCalibrationMarkers();
  };
  const finish = (finishEvent) => {
    if (finishEvent.pointerId !== event.pointerId) return;
    markers.removeEventListener("pointermove", move);
    markers.removeEventListener("pointerup", finish);
    markers.removeEventListener("pointercancel", finish);
    if (markers.hasPointerCapture(event.pointerId)) markers.releasePointerCapture(event.pointerId);
  };

  markers.addEventListener("pointermove", move);
  markers.addEventListener("pointerup", finish);
  markers.addEventListener("pointercancel", finish);
}

function clamp01(value) {
  return Math.min(1, Math.max(0, value));
}

function clearAutoCalibrationPreview() {
  const panel = document.getElementById("calib-auto-preview");
  const image = document.getElementById("calib-auto-image");
  const markers = document.getElementById("calib-auto-markers");
  const coordinates = document.getElementById("calib-auto-coordinates");
  const imageMeta = document.getElementById("calib-auto-image-meta");
  autoCalibrationPreviewState = null;
  if (panel) panel.hidden = true;
  if (image) image.removeAttribute("src");
  if (markers) markers.replaceChildren();
  if (coordinates) coordinates.replaceChildren();
  if (imageMeta) imageMeta.textContent = "";
}

export function loadCalibrationStatus() {
  invoke("calibration.getStatus")
    .then(renderCalibration)
    .catch((err) => {
      console.warn("Failed to load calibration:", err);
    });
}

function renderCalibration(data) {
  if (!data) return;

  // Render Window Status
  const winTitle = document.getElementById("calib-win-title");
  const winBadge = document.getElementById("calib-win-badge");
  const winDetail = document.getElementById("calib-win-detail");

  if (winTitle) winTitle.textContent = data.window.title;
  if (winDetail) winDetail.textContent = data.window.detail;

  if (winBadge) {
    winBadge.className = "badge";
    const text = winBadge.querySelector(".badge-text");
    if (data.window.isForeground) {
      winBadge.classList.add("success");
      if (text) text.textContent = "已捕获 · 处于前台";
    } else if (data.window.isBound) {
      winBadge.classList.add("warning");
      if (text) text.textContent = "已绑定 · 后台运行中";
    } else {
      winBadge.classList.add("danger");
      if (text) text.textContent = "未绑定游戏窗口";
    }
  }

  // Render Slots Table
  const calibrationProfile = document.getElementById("calib-coordinate-profile");
  if (calibrationProfile) {
    const info = data.calibration;
    calibrationProfile.textContent = info?.clientWidth && info?.clientHeight
      ? `校准基准：${info.clientWidth} × ${info.clientHeight}${info.source ? ` · ${info.source}` : ""}`
      : "当前配置未记录校准窗口尺寸";
  }

  const listEl = document.getElementById("calib-slots-list");
  if (!listEl) return;

  listEl.innerHTML = "";
  data.slots.forEach((s) => {
    const row = document.createElement("div");
    row.className = "calib-slot-row";
    row.innerHTML = `
      <div style="font-weight: 600;">${s.name}</div>
      <div class="calib-coords">
        <span>X: ${s.x.toFixed(4)}</span>
        <span>Y: ${s.y.toFixed(4)}</span>
      </div>
      <button class="btn btn-sm btn-calib-test" data-slot="${s.index}">测试点击</button>
    `;
    listEl.appendChild(row);
  });

  listEl.querySelectorAll(".btn-calib-test").forEach((btn) => {
    btn.addEventListener("click", () => {
      const slot = parseInt(btn.dataset.slot, 10);
      invoke("calibration.testSlot", { slot }).then((ok) => {
        showToast(ok ? `已向 Slot ${slot} 发送测试点击` : "未绑定窗口或测试失败", ok ? "success" : "warning");
      });
    });
  });

  // Strum Point
  const strumCoords = document.getElementById("calib-strum-coords");
  if (strumCoords && data.strumPoint) {
    strumCoords.textContent = `X: ${data.strumPoint.x.toFixed(4)}, Y: ${data.strumPoint.y.toFixed(4)}`;
  }
}
