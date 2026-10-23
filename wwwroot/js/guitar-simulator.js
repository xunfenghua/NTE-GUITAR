// wwwroot/js/guitar-simulator.js

const FRET_PAGES = {
  P1: { label: "P1 (I~VI 品)", slots: ["I", "II", "III", "IV", "V", "VI"] },
  P2: { label: "P2 (VII~XII 品)", slots: ["VII", "VIII", "IX", "X", "XI", "XII"] },
  P3: { label: "P3 (XIII~XVIII 品)", slots: ["XIII", "XIV", "XV", "XVI", "XVII", "XVIII"] },
  P4: { label: "P4 (XIX~XXIV 品)", slots: ["XIX", "XX", "XXI", "XXII", "XXIII", "XXIV"] },
  P5: { label: "P5 (Zero / 0品空弦)", slots: ["Zero", "—", "—", "—", "—", "—"] }
};

const CHORD_PAGES = {
  P1: { label: "P1 (基础三和弦)", slots: ["G", "Am", "Bm", "C", "D", "Em"] },
  P2: { label: "P2 (七和弦/九和弦)", slots: ["Cmaj7", "C7", "Cm7", "G7", "C9", "Cm9"] },
  P3: { label: "P3 (五和弦/挂留)", slots: ["C5", "D5", "G5", "A5", "Cmaj9", "Cadd9"] },
  P4: { label: "P4 (斜杠低音/扩展)", slots: ["G/B", "Am7/G", "D/F#", "F/A", "A", "Dm"] }
};

// 默认展示截图同款的 Chord P2 (Cmaj7 激活)
let currentSimMode = "Chord"; // "Fret" | "Chord"
let currentSimPage = "P2";
let activeSlotIndex = 1; // 1..6 (默认高亮顶部 Slot 1)

// 极坐标转笛卡尔坐标
function polarToCartesian(cx, cy, r, angleDeg) {
  const rad = (angleDeg * Math.PI) / 180;
  return {
    x: cx + r * Math.cos(rad),
    y: cy + r * Math.sin(rad)
  };
}

// 生成环形扇叶 SVG Path (Annular Sector Wedge)
function createAnnularSectorPath(cx, cy, rInner, rOuter, startAngle, endAngle) {
  const p1 = polarToCartesian(cx, cy, rOuter, startAngle);
  const p2 = polarToCartesian(cx, cy, rOuter, endAngle);
  const p3 = polarToCartesian(cx, cy, rInner, endAngle);
  const p4 = polarToCartesian(cx, cy, rInner, startAngle);
  const largeArc = endAngle - startAngle <= 180 ? "0" : "1";

  return [
    `M ${p1.x.toFixed(2)} ${p1.y.toFixed(2)}`,
    `A ${rOuter} ${rOuter} 0 ${largeArc} 1 ${p2.x.toFixed(2)} ${p2.y.toFixed(2)}`,
    `L ${p3.x.toFixed(2)} ${p3.y.toFixed(2)}`,
    `A ${rInner} ${rInner} 0 ${largeArc} 0 ${p4.x.toFixed(2)} ${p4.y.toFixed(2)}`,
    "Z"
  ].join(" ");
}

// 生成单条圆弧 Path (用于内圈霓虹亮边与 GUITAR 弧形字路径)
function createArcPath(cx, cy, r, startAngle, endAngle) {
  const start = polarToCartesian(cx, cy, r, startAngle);
  const end = polarToCartesian(cx, cy, r, endAngle);
  const largeArc = Math.abs(endAngle - startAngle) <= 180 ? "0" : "1";
  return `M ${start.x.toFixed(2)} ${start.y.toFixed(2)} A ${r} ${r} 0 ${largeArc} 1 ${end.x.toFixed(2)} ${end.y.toFixed(2)}`;
}

export function initGuitarSimulator() {
  renderSimPageSelectors();
  renderWheelSectors();
  renderStrings();
  bindIngameControls();

  window.addEventListener("nte:guitar-state", (e) => {
    handleSimulationUpdate(e.detail);
  });

  window.addEventListener("guitar.simulationUpdated", (e) => {
    handleSimulationUpdate(e.detail);
  });

  let lastTrackedSongId = null;
  window.addEventListener("nte:playback-state", (e) => {
    const s = e.detail;
    if (!s) return;
    // 关键优化：绝不让 50ms 一次的播放进度心跳把当前轮盘强制刷回 initialPage！
    // 仅在初次载入新曲目且处于待命状态时，同步推荐起始页
    if (s.song && s.song.id !== lastTrackedSongId && s.state === "stopped") {
      lastTrackedSongId = s.song.id;
      if (s.advice && s.advice.initialPage !== undefined) {
        const pageKey = `P${s.advice.initialPage}`;
        const pages = getActivePagesMap();
        if (pages[pageKey]) {
          currentSimPage = pageKey;
          updatePageSelectorActive();
          updateWheelLabels();
        }
      }
    } else if (!s.song && s.state === "stopped") {
      lastTrackedSongId = null;
    }
  });
}

export function setSimPage(page) {
  const pages = getActivePagesMap();
  if (pages[page]) {
    currentSimPage = page;
    updatePageSelectorActive();
    updateWheelLabels();
  }
}

export function setSimMode(mode) {
  const norm = (mode || "").toLowerCase();
  const nextMode = norm.includes("chord") ? "Chord" : (norm.includes("piano") ? "Piano" : "Fret");
  if (nextMode === currentSimMode) return;
  currentSimMode = nextMode;

  const pages = getActivePagesMap();
  if (!pages[currentSimPage]) {
    currentSimPage = "P1";
  }
  const hudMode = document.getElementById("sim-hud-mode");
  if (hudMode) hudMode.textContent = currentSimMode === "Chord" ? "和弦" : (currentSimMode === "Piano" ? "钢琴" : "独奏");

  renderSimPageSelectors();
  updateWheelLabels();
}

function getActivePagesMap() {
  return currentSimMode === "Fret" ? FRET_PAGES : CHORD_PAGES;
}

function stepPage(delta) {
  const keys = Object.keys(getActivePagesMap());
  const idx = keys.indexOf(currentSimPage);
  const nextIdx = (idx + delta + keys.length) % keys.length;
  currentSimPage = keys[nextIdx];
  updatePageSelectorActive();
  updateWheelLabels();
}

// 琴弦长按与拨弦状态追踪
const activeStrings = new Set();
const manuallyHeldStrings = new Set();
const stringReleaseTimers = new Map();
const stringLineElements = new Array(7);

function getGameStringNumber(note) {
  const gameKey = Number(note?.gameKey);
  if (Number.isInteger(gameKey) && gameKey >= 1 && gameKey <= 6) return gameKey;
  const physicalString = Number(note?.physicalString ?? note?.stringNo);
  return Number.isInteger(physicalString) && physicalString >= 1 && physicalString <= 6
    ? 7 - physicalString
    : 0;
}

function animateStringPluck(line) {
  // CSS opacity pulse stays composited and avoids creating a Web Animation for every note.
  if (!line.classList.contains("pluck-pulse")) line.classList.add("pluck-pulse");
}

function bindIngameControls() {
  document.getElementById("sim-btn-prev-page")?.addEventListener("click", () => stepPage(-1));
  document.getElementById("sim-btn-next-page")?.addEventListener("click", () => stepPage(1));
  document.getElementById("sim-hud-toggle-mode")?.addEventListener("click", () => {
    setSimMode(currentSimMode === "Chord" ? "Fret" : "Chord");
  });

  // 支持在吉他模拟页直接按 R/T 翻页、Z 切换模式、1~6 试拨/长按琴弦
  window.addEventListener("keydown", (e) => {
    const simPage = document.getElementById("page-simulator");
    if (!simPage || !simPage.classList.contains("active")) return;
    if (["INPUT", "TEXTAREA"].includes(document.activeElement?.tagName)) return;

    const key = e.key.toUpperCase();
    if (key === "R") {
      stepPage(-1);
    } else if (key === "T") {
      stepPage(1);
    } else if (key === "Z") {
      setSimMode(currentSimMode === "Chord" ? "Fret" : "Chord");
    } else if (key >= "1" && key <= "6") {
      // 阻止操作系统长按时的按键自动连点 (Auto-Repeat)
      if (e.repeat) {
        e.preventDefault();
        return;
      }
      e.preventDefault();
      const gameKey = parseInt(key, 10);
      startManualHold(gameKey, gameKey);
    }
  });

  window.addEventListener("keyup", (e) => {
    const simPage = document.getElementById("page-simulator");
    if (!simPage || !simPage.classList.contains("active")) return;
    if (["INPUT", "TEXTAREA"].includes(document.activeElement?.tagName)) return;

    const key = e.key.toUpperCase();
    if (key >= "1" && key <= "6") {
      e.preventDefault();
      const gameKey = parseInt(key, 10);
      stopManualHold(gameKey, gameKey);
    }
  });

  // 窗口失去焦点时，确保释放所有手动按住的琴弦光效
  window.addEventListener("blur", () => {
    manuallyHeldStrings.forEach((strNo) => {
      stopManualHold(strNo, strNo);
    });
    manuallyHeldStrings.clear();
  });
}

function startManualHold(stringNo, gameKey) {
  manuallyHeldStrings.add(stringNo);
  activeStrings.add(stringNo);

  if (stringReleaseTimers.has(stringNo)) {
    clearTimeout(stringReleaseTimers.get(stringNo));
    stringReleaseTimers.delete(stringNo);
  }

  const line = stringLineElements[stringNo];
  if (line) {
    line.classList.add("active", "holding");
    animateStringPluck(line);
  }

}

function stopManualHold(stringNo, gameKey) {
  manuallyHeldStrings.delete(stringNo);
  activeStrings.delete(stringNo);

  const line = stringLineElements[stringNo];
  if (line) {
    line.classList.remove("holding");
    // 若当前无后端播放音符占用，延迟平滑消退 active
    setTimeout(() => {
      if (!manuallyHeldStrings.has(stringNo) && !activeStrings.has(stringNo)) {
        line.classList.remove("active");
      }
    }, 80);
  }

}

function triggerStringPreview(stringNo, gameKey) {
  startManualHold(stringNo, gameKey);
  setTimeout(() => stopManualHold(stringNo, gameKey), 260);
}

function renderSimPageSelectors() {
  const container = document.getElementById("sim-page-selector");
  const dotsContainer = document.getElementById("sim-ingame-dots");
  const pages = getActivePagesMap();
  const keys = Object.keys(pages);

  if (container) {
    container.innerHTML = "";
    keys.forEach((key) => {
      const btn = document.createElement("button");
      btn.className = `sim-page-btn ${key === currentSimPage ? "active" : ""}`;
      btn.setAttribute("data-page", key);
      btn.textContent = key;
      btn.title = pages[key].label;
      btn.onclick = () => {
        setSimPage(key);
      };
      container.appendChild(btn);
    });
  }

  // 渲染底部游戏同款同心圆点 ○ ◎ ○ ○
  if (dotsContainer) {
    dotsContainer.innerHTML = "";
    keys.forEach((key) => {
      const dot = document.createElement("button");
      dot.className = `ingame-page-dot ${key === currentSimPage ? "active" : ""}`;
      dot.setAttribute("data-page", key);
      dot.title = pages[key].label;
      dot.onclick = () => {
        setSimPage(key);
      };
      dotsContainer.appendChild(dot);
    });
  }
}

function updatePageSelectorActive() {
  const container = document.getElementById("sim-page-selector");
  if (container) {
    container.querySelectorAll(".sim-page-btn").forEach((btn) => {
      const pageKey = btn.getAttribute("data-page") || btn.textContent.trim();
      if (pageKey === currentSimPage) {
        btn.classList.add("active");
      } else {
        btn.classList.remove("active");
      }
    });
  }

  const dotsContainer = document.getElementById("sim-ingame-dots");
  if (dotsContainer) {
    const keys = Object.keys(getActivePagesMap());
    const curIdx = keys.indexOf(currentSimPage);
    dotsContainer.querySelectorAll(".ingame-page-dot").forEach((dot, idx) => {
      if (idx === curIdx) {
        dot.classList.add("active");
      } else {
        dot.classList.remove("active");
      }
    });
  }
}

function updateWheelLabels() {
  const container = document.getElementById("wheel-sectors-box");
  if (!container) return;

  const pages = getActivePagesMap();
  const slotLabels = pages[currentSimPage]?.slots || ["-", "-", "-", "-", "-", "-"];

  for (let i = 1; i <= 6; i++) {
    const sec = document.getElementById(`wheel-sector-${i}`);
    if (!sec) continue;

    const textEl = sec.querySelector(".sector-text");
    if (textEl) {
      const labelText = slotLabels[i - 1] || "-";
      textEl.textContent = labelText;
      if (labelText.length >= 4) {
        textEl.classList.add("long-text");
      } else {
        textEl.classList.remove("long-text");
      }
    }

    if (i === activeSlotIndex) {
      sec.classList.add("active");
    } else {
      sec.classList.remove("active");
    }
  }
}

function renderWheelSectors() {
  const container = document.getElementById("wheel-sectors-box");
  if (!container) return;

  // 关键防闪：若轮盘 SVG 骨架已存在，直接更新文字与高亮，杜绝重新 innerHTML 导致白闪与重新渲染
  if (container.querySelector(".ingame-wheel-svg")) {
    updateWheelLabels();
    return;
  }

  const pages = getActivePagesMap();
  const slotLabels = pages[currentSimPage]?.slots || ["-", "-", "-", "-", "-", "-"];

  const cx = 230;
  const cy = 230;
  const rOuter = 218;
  const rInner = 118;
  const rArc = 122.5; // 内圈枚红高亮灯条半径
  const rLabel = 169; // 扇区文字中心半径
  const gapHalf = 1.15; // 扇叶之间的窄缝角度

  // 6个扇区中心角度：12点(-90°)、2点(-30°)、4点(30°)、6点(90°)、8点(150°)、10点(210°)
  const centerAngles = [-90, -30, 30, 90, 150, 210];

  let sectorsSvg = "";
  for (let i = 1; i <= 6; i++) {
    const cAngle = centerAngles[i - 1];
    const startAngle = cAngle - 30 + gapHalf;
    const endAngle = cAngle + 30 - gapHalf;

    const wedgePath = createAnnularSectorPath(cx, cy, rInner, rOuter, startAngle, endAngle);
    const innerGlowArc = createArcPath(cx, cy, rArc, startAngle + 0.6, endAngle - 0.6);
    const labelPos = polarToCartesian(cx, cy, rLabel, cAngle);
    const labelText = slotLabels[i - 1] || "-";
    const isLongLabel = labelText.length >= 4;
    const isActive = i === activeSlotIndex;

    sectorsSvg += `
      <g class="wheel-sector sector-${i} ${isActive ? "active" : ""}" id="wheel-sector-${i}" data-slot="${i}">
        <path class="sector-wedge" d="${wedgePath}" />
        <path class="sector-inner-arc" d="${innerGlowArc}" />
        <text
          class="sector-text ${isLongLabel ? "long-text" : ""}"
          x="${labelPos.x.toFixed(2)}"
          y="${labelPos.y.toFixed(2)}"
          text-anchor="middle"
          dominant-baseline="central"
        >${labelText}</text>
      </g>
    `;
  }

  // 中圈 · GUITAR · 金属浮雕环的3段弧形路径
  const guitarTextR = 99;
  const arcTop = createArcPath(cx, cy, guitarTextR, -135, -45);
  const arcRight = createArcPath(cx, cy, guitarTextR, -15, 75);
  const arcLeft = createArcPath(cx, cy, guitarTextR, 105, 195);

  container.innerHTML = `
    <svg class="ingame-wheel-svg" viewBox="0 0 460 460" xmlns="http://www.w3.org/2000/svg">
      <defs>
        <!-- 激活态扇叶：复刻截图顶部 Cmaj7 枚红径向渐变 -->
        <radialGradient id="activeSectorGrad" cx="50%" cy="50%" r="50%">
          <stop offset="52%" stop-color="#9e1f56" stop-opacity="0.92" />
          <stop offset="100%" stop-color="#dc2973" stop-opacity="0.96" />
        </radialGradient>

        <!-- 未激活扇叶：复刻截图深邃半透明黑底 -->
        <radialGradient id="inactiveSectorGrad" cx="50%" cy="50%" r="50%">
          <stop offset="55%" stop-color="#0d0d11" stop-opacity="0.82" />
          <stop offset="100%" stop-color="#17181f" stop-opacity="0.72" />
        </radialGradient>

        <!-- 内圈霓虹亮粉发光滤镜 -->
        <filter id="pinkGlow" x="-20%" y="-20%" width="140%" height="140%">
          <feGaussianBlur stdDeviation="2.5" result="blur" />
          <feMerge>
            <feMergeNode in="blur" />
            <feMergeNode in="SourceGraphic" />
          </feMerge>
        </filter>

        <path id="guitarArcTop" d="${arcTop}" />
        <path id="guitarArcRight" d="${arcRight}" />
        <path id="guitarArcLeft" d="${arcLeft}" />
      </defs>

      <!-- 外层6大扇叶 -->
      <g class="wheel-sectors-group">
        ${sectorsSvg}
      </g>

      <!-- 中圈 GUITAR 暗黑金属浮雕环 -->
      <g class="guitar-bezel-group" pointer-events="none">
        <!-- 外金属暗环 -->
        <circle cx="${cx}" cy="${cy}" r="113" fill="#17181c" stroke="#2c2e36" stroke-width="2.5" />
        <!-- 刻印文字底环 -->
        <circle cx="${cx}" cy="${cy}" r="99" fill="none" stroke="#22242b" stroke-width="18" />
        <!-- 内金属高光切边环 -->
        <circle cx="${cx}" cy="${cy}" r="88" fill="#050507" stroke="#383b45" stroke-width="2" />

        <!-- 三组 · GUITAR · 弧形浮雕文字 -->
        <text class="bezel-curved-text"><textPath href="#guitarArcTop" startOffset="50%" text-anchor="middle">· GUITAR ·</textPath></text>
        <text class="bezel-curved-text"><textPath href="#guitarArcRight" startOffset="50%" text-anchor="middle">· GUITAR ·</textPath></text>
        <text class="bezel-curved-text"><textPath href="#guitarArcLeft" startOffset="50%" text-anchor="middle">· GUITAR ·</textPath></text>
      </g>
    </svg>
  `;

  // 绑定点击扇区切换高亮
  container.querySelectorAll(".wheel-sector").forEach((sec) => {
    sec.addEventListener("click", () => {
      const slot = parseInt(sec.getAttribute("data-slot"), 10);
      activeSlotIndex = slot;
      container.querySelectorAll(".wheel-sector").forEach((s) => s.classList.remove("active"));
      sec.classList.add("active");

    });
  });
}

function renderStrings() {
  const container = document.getElementById("guitar-strings-box");
  if (!container) return;

  container.innerHTML = "";
  const stringsMeta = [
    { num: 1, name: "游戏1弦 (低E2)", key: 1 },
    { num: 2, name: "游戏2弦 (A2)",   key: 2 },
    { num: 3, name: "游戏3弦 (D3)",   key: 3 },
    { num: 4, name: "游戏4弦 (G3)",   key: 4 },
    { num: 5, name: "游戏5弦 (B3)",   key: 5 },
    { num: 6, name: "游戏6弦 (高E4)", key: 6 }
  ];

  stringsMeta.forEach((s) => {
    const line = document.createElement("div");
    line.className = "guitar-string-line";
    line.id = `guitar-string-${s.num}`;
    line.title = `${s.name} -> 游戏按键 [${s.key}] (按住试拨/长按)`;
    stringLineElements[s.num] = line;
    line.addEventListener("animationend", (e) => {
      if (e.animationName === "stringPluckFlash") line.classList.remove("pluck-pulse");
    });

    const label = document.createElement("span");
    label.className = "guitar-string-tag";
    label.textContent = s.num;

    line.appendChild(label);

    line.addEventListener("pointerdown", (e) => {
      e.preventDefault();
      startManualHold(s.num, s.key);
    });
    line.addEventListener("pointerup", (e) => {
      e.preventDefault();
      stopManualHold(s.num, s.key);
    });
    line.addEventListener("pointerleave", () => {
      if (manuallyHeldStrings.has(s.num)) {
        stopManualHold(s.num, s.key);
      }
    });
    line.addEventListener("pointercancel", () => {
      if (manuallyHeldStrings.has(s.num)) {
        stopManualHold(s.num, s.key);
      }
    });

    container.appendChild(line);
  });
}

function handleSimulationUpdate(snapshot) {
  if (!snapshot) return;

  if (snapshot.mode && snapshot.mode !== currentSimMode) {
    const norm = snapshot.mode.toLowerCase();
    const nextMode = norm.includes("chord") ? "Chord" : (norm.includes("piano") ? "Piano" : "Fret");
    if (nextMode !== currentSimMode) {
      currentSimMode = nextMode;
      const hudMode = document.getElementById("sim-hud-mode");
      if (hudMode) hudMode.textContent = currentSimMode === "Chord" ? "和弦" : (currentSimMode === "Piano" ? "钢琴" : "独奏");
      renderSimPageSelectors();
      updateWheelLabels();
    }
  }

  if (snapshot.currentPage && snapshot.currentPage !== currentSimPage) {
    currentSimPage = snapshot.currentPage;
    updatePageSelectorActive();
    updateWheelLabels();
  }

  // 1. 槽位高亮计算
  let activeSlot = 0;
  if (snapshot.currentSlot && snapshot.currentSlot >= 1 && snapshot.currentSlot <= 6) {
    activeSlot = snapshot.currentSlot;
  }

  // 2. 收集当前发音的琴弦编号，保留弦线动画
  const newActiveStrings = new Set();
  if (snapshot.activeNotes && snapshot.activeNotes.length > 0) {
    snapshot.activeNotes.forEach((n) => {
      const gameStringNo = getGameStringNumber(n);
      if (gameStringNo) newActiveStrings.add(gameStringNo);
      if (n.fret !== undefined && n.fret !== null) {
        activeSlot = n.fret === 0 ? 1 : (((n.fret - 1) % 6) + 1);
      }
    });
  } else if ((snapshot.actionType === "pluck" || snapshot.actionType === "sustain") && snapshot.stringNo >= 1 && snapshot.stringNo <= 6) {
    const gameStringNo = getGameStringNumber(snapshot);
    if (gameStringNo) newActiveStrings.add(gameStringNo);
  } else if (snapshot.actionType === "release" && snapshot.stringNo >= 1 && snapshot.stringNo <= 6) {
    const gameStringNo = getGameStringNumber(snapshot);
    if (gameStringNo) newActiveStrings.delete(gameStringNo);
  }

  if (activeSlot > 0 && activeSlot !== activeSlotIndex) {
    activeSlotIndex = activeSlot;
    for (let s = 1; s <= 6; s++) {
      const sec = document.getElementById(`wheel-sector-${s}`);
      if (sec) {
        if (s === activeSlotIndex) sec.classList.add("active");
        else sec.classList.remove("active");
      }
    }
  }

  // 3. 弦状态平滑处理
  for (let s = 1; s <= 6; s++) {
    const line = stringLineElements[s];
    if (!line) continue;
    const isNowActive = newActiveStrings.has(s) || manuallyHeldStrings.has(s);
    const wasActive = activeStrings.has(s);

    if (isNowActive) {
      if (!wasActive) {
        activeStrings.add(s);
        line.classList.add("active");
        animateStringPluck(line);
      } else if (!manuallyHeldStrings.has(s) && !line.classList.contains("holding")) {
        line.classList.add("holding");
      }

      if ((!snapshot.activeNotes || snapshot.activeNotes.length === 0) && !manuallyHeldStrings.has(s)) {
        if (stringReleaseTimers.has(s)) clearTimeout(stringReleaseTimers.get(s));
        const t = setTimeout(() => {
          stringReleaseTimers.delete(s);
          if (!manuallyHeldStrings.has(s) && activeStrings.has(s)) {
            activeStrings.delete(s);
            line.classList.remove("active", "holding");
          }
        }, 250);
        stringReleaseTimers.set(s, t);
      }
    } else {
      if (wasActive) {
        activeStrings.delete(s);
        line.classList.remove("active", "holding");
      }
      if (stringReleaseTimers.has(s)) {
        clearTimeout(stringReleaseTimers.get(s));
        stringReleaseTimers.delete(s);
      }
    }
  }

  // Handle Strum animation
  if (snapshot.actionType === "strumDown" || snapshot.actionType === "strumUp") {
    const hudId = snapshot.actionType === "strumDown" ? "sim-hud-strum-down" : "sim-hud-strum-up";
    const hudEl = document.getElementById(hudId);
    if (hudEl) {
      hudEl.classList.add("active");
      setTimeout(() => hudEl.classList.remove("active"), 260);
    }
  }

}
