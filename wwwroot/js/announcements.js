import { invoke } from "./bridge.js";
import { closeModal, openModal, showToast } from "./app.js";

let currentAnnouncement = null;
let currentFingerprint = "";

export function initAnnouncements() {
  document.getElementById("btn-announcement-details")?.addEventListener("click", showDetails);
  document.getElementById("btn-copy-announcement-content")?.addEventListener("click", () => {
    copyText(currentAnnouncement?.content || "", "公告内容已复制");
  });
  document.getElementById("btn-copy-announcement-url")?.addEventListener("click", () => {
    copyText(currentAnnouncement?.downloadUrl || "", "下载地址已复制");
  });
  document.getElementById("btn-open-announcement-url")?.addEventListener("click", async () => {
    if (!currentAnnouncement?.downloadUrl) return;
    try {
      await invoke("announcement.openDownload", { url: currentAnnouncement.downloadUrl });
    } catch (error) {
      showToast(error.message || "无法打开下载地址", "danger");
    }
  });
  void checkAnnouncement();
}

async function checkAnnouncement() {
  try {
    const result = await invoke("announcement.check");
    if (result?.status === "latest") {
      showToast("已是最新版", "success", 1400);
      return;
    }
    if (result?.status !== "new" || !result.announcement || !result.fingerprint) {
      showToast("暂时无法检查更新", "warning", 2200);
      return;
    }

    currentAnnouncement = result.announcement;
    currentFingerprint = result.fingerprint;
    const marked = await invoke("announcement.markSeen", { fingerprint: currentFingerprint });
    if (!marked) {
      showToast("公告提示状态保存失败", "warning", 2200);
      return;
    }

    document.getElementById("announcement-prompt-title").textContent =
      currentAnnouncement.title || "发现新公告";
    const summary = (currentAnnouncement.content || "").trim();
    document.getElementById("announcement-prompt-summary").textContent =
      `${currentAnnouncement.version ? `版本 ${currentAnnouncement.version} · ` : ""}${summary.slice(0, 180)}${summary.length > 180 ? "…" : ""}`;
    openModal("modal-announcement-prompt");
  } catch (_) {
    showToast("暂时无法检查更新", "warning", 2200);
  }
}

function showDetails() {
  if (!currentAnnouncement) return;
  closeModal();
  document.getElementById("announcement-detail-title").textContent = currentAnnouncement.title || "公告";
  document.getElementById("announcement-detail-version").textContent =
    currentAnnouncement.version ? `版本 ${currentAnnouncement.version}` : "公告详情";
  document.getElementById("announcement-detail-content").textContent = currentAnnouncement.content || "";

  const downloadUrl = (currentAnnouncement.downloadUrl || "").trim();
  const hasUrl = downloadUrl.length > 0;
  const downloadRow = document.getElementById("announcement-download-row");
  const urlInput = document.getElementById("announcement-download-url");
  const copyUrl = document.getElementById("btn-copy-announcement-url");
  const openUrl = document.getElementById("btn-open-announcement-url");
  downloadRow.style.display = hasUrl ? "block" : "none";
  urlInput.value = downloadUrl;
  copyUrl.style.display = hasUrl ? "inline-flex" : "none";
  openUrl.style.display = hasUrl ? "inline-flex" : "none";
  openModal("modal-announcement-detail");
}

async function copyText(value, successMessage) {
  if (!value) {
    showToast("没有可复制的内容", "warning");
    return;
  }
  try {
    await navigator.clipboard.writeText(value);
    showToast(successMessage, "success");
  } catch (_) {
    const field = document.createElement("textarea");
    field.value = value;
    field.style.position = "fixed";
    field.style.opacity = "0";
    document.body.appendChild(field);
    field.select();
    const copied = document.execCommand("copy");
    field.remove();
    showToast(copied ? successMessage : "复制失败，请选中文本手动复制", copied ? "success" : "warning");
  }
}
