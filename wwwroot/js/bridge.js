// wwwroot/js/bridge.js

const pending = new Map();
let nextId = 1;

/**
 * Send an action request to C# backend host
 * @param {string} action - Whitelisted action name
 * @param {object} payload - Optional payload data
 * @returns {Promise<any>}
 */
export function invoke(action, payload = {}) {
  return new Promise((resolve, reject) => {
    const requestId = `req-${Date.now()}-${nextId++}`;
    const isLongAction = action === "library.generateGuitarChart" ||
      action === "gtp.parse" || action === "gtp.convert" || action === "gtp.preview" ||
      action === "guitarchart.exportPdf" ||
      action === "recommended.use" ||
      action === "recommended.prepareManual" ||
      action === "recommended.prepareGameChart" ||
      action === "recommended.submit" ||
      action === "library.submitRecommended" ||
      action === "calibration.autoBindAndDetect" ||
      action === "audioTranscription.prepare" ||
      action === "audioTranscription.installCuda" ||
      action === "audioTranscription.transcribe";
    const timer = setTimeout(() => {
      pending.delete(requestId);
      reject(new Error("请求超时，请检查程序后台状态后重试。"));
    }, isLongAction ? 120000 : 15000);

    pending.set(requestId, { resolve, reject, timer });

    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage({ requestId, action, payload });
    } else {
      clearTimeout(timer);
      pending.delete(requestId);
      console.warn("[Bridge Mock] No WebView2 detected. Mocking action:", action);
      reject(new Error("WebView2 运行宿主未连接"));
    }
  });
}

// Global listener for responses & backend events
if (window.chrome && window.chrome.webview) {
  window.chrome.webview.addEventListener("message", (event) => {
    const message = event.data;
    if (!message) return;

    // 1. Handle Response to invoke()
    if (message.requestId && pending.has(message.requestId)) {
      const item = pending.get(message.requestId);
      clearTimeout(item.timer);
      pending.delete(message.requestId);

      if (message.success) {
        item.resolve(message.data);
      } else {
        item.reject(new Error(message.error || "操作失败"));
      }
      return;
    }

    // 2. Handle Backend Events
    if (message.event === "playback.stateChanged") {
      window.dispatchEvent(
        new CustomEvent("nte:playback-state", { detail: message.data })
      );
    } else if (message.event === "playback.startConfirmationRequired") {
      window.dispatchEvent(
        new CustomEvent("nte:playback-start-confirmation-required", { detail: message.data })
      );
    } else if (message.event === "guitar.simulationUpdated") {
      window.dispatchEvent(
        new CustomEvent("nte:guitar-state", { detail: message.data })
      );
    } else if (message.event === "midi.roll.loaded") {
      // 新增：当曲目加载或变速重编译完成时，推送音符卷帘快照
      window.dispatchEvent(
        new CustomEvent("nte:midi-roll-loaded", { detail: message.data })
      );
    } else if (message.event === "piano.playbackState") {
      window.dispatchEvent(new CustomEvent("nte:piano-playback-state", { detail: message.data }));
    } else if (message.event === "audioTranscription.progress") {
      window.dispatchEvent(new CustomEvent("nte:audio-transcription-progress", { detail: message.data }));
    } else if (message.event === "audioTranscription.log") {
      window.dispatchEvent(new CustomEvent("nte:audio-transcription-log", { detail: message.data }));
    } else if (message.event === "audioTranscription.cpuConfirmationRequired") {
      window.dispatchEvent(new CustomEvent("nte:audio-transcription-cpu-confirmation", { detail: message.data }));
    } else if (message.event === "audioTranscription.completed") {
      window.dispatchEvent(new CustomEvent("nte:audio-transcription-completed", { detail: message.data }));
    } else if (message.event === "audioTranscription.failed") {
      window.dispatchEvent(new CustomEvent("nte:audio-transcription-failed", { detail: message.data }));
    } else if (message.event === "audioTranscription.shutdownFailed") {
      window.dispatchEvent(new CustomEvent("nte:audio-transcription-shutdown-failed", { detail: message.data }));
    } else if (message.event === "piano.hotkey") {
      window.dispatchEvent(new CustomEvent("nte:piano-dock-command", { detail: { action: "toggle" } }));
    } else if (message.event === "window.sizeModeChanged") {
      // 新增：同步窗口尺寸锁定状态
      window.dispatchEvent(
        new CustomEvent("nte:window-size-mode", { detail: message.data })
      );
    } else if (message.event === "window.topMostChanged") {
      // 新增：同步窗口置顶与无焦点保护状态
      window.dispatchEvent(
        new CustomEvent("nte:window-topmost-changed", { detail: message.data })
      );
    }
  });
}
