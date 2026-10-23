// wwwroot/js/tutorial.js
import { invoke } from "./bridge.js";

let steps = [];
let currentStepIdx = 0;

export function initTutorial() {
  invoke("settings.getTutorial")
    .then((data) => {
      steps = data || [];
      renderStepPills();
      showStep(0);
    })
    .catch((err) => {
      console.warn("Failed to load tutorial:", err);
    });

  document.getElementById("btn-tut-prev")?.addEventListener("click", () => {
    if (currentStepIdx > 0) showStep(currentStepIdx - 1);
  });

  document.getElementById("btn-tut-next")?.addEventListener("click", () => {
    if (currentStepIdx < steps.length - 1) {
      showStep(currentStepIdx + 1);
    } else {
      // Finished
      window.app?.showPage("library");
    }
  });
}

function renderStepPills() {
  const container = document.getElementById("tutorial-pills-box");
  if (!container) return;

  container.innerHTML = "";
  steps.forEach((s, idx) => {
    const pill = document.createElement("div");
    pill.className = `step-pill ${idx === currentStepIdx ? "active" : ""}`;
    pill.textContent = s.stepNumber;
    pill.onclick = () => showStep(idx);
    container.appendChild(pill);
  });
}

function showStep(idx) {
  if (idx < 0 || idx >= steps.length) return;
  currentStepIdx = idx;
  const s = steps[idx];

  const pills = document.querySelectorAll(".step-pill");
  pills.forEach((p, i) => {
    p.className = `step-pill ${i === currentStepIdx ? "active" : (i < currentStepIdx ? "done" : "")}`;
  });

  const titleEl = document.getElementById("tut-title");
  const descEl = document.getElementById("tut-desc");
  const adviceEl = document.getElementById("tut-advice");
  const safetyEl = document.getElementById("tut-safety");
  const nextBtn = document.getElementById("btn-tut-next");

  if (titleEl) titleEl.textContent = `${s.icon} ${s.title}`;
  if (descEl) descEl.textContent = s.summary;
  if (adviceEl) adviceEl.textContent = s.advice;
  if (safetyEl) safetyEl.textContent = s.safetyTip;

  if (nextBtn) {
    nextBtn.textContent = (currentStepIdx === steps.length - 1) ? "完成向导并前往曲库 ✨" : "下一步 →";
  }
}
