(() => {
  const $ = id => document.getElementById(id);
  const canvas = $("falling-lanes"), ctx = canvas.getContext("2d", { alpha: true });
  const laneBackground = document.createElement("canvas"), laneBackgroundContext = laneBackground.getContext("2d", { alpha: true });
  let laneBackgroundDirty = true;
  let laneBackgroundDpr = 0, laneBackgroundLogicalWidth = 0, laneBackgroundLogicalHeight = 0;
  const state = { chart: null, notes: [], noteColumns: new Map(), positions: [], notePositionChanges: [], mode: "rhythm-normal", executionMode: "semi-auto", playing: false, speed: 100, nowUs: 0, durationUs: 0,
    theme: "dark", currentPage: 0, currentSlot: 0, wheelSides: ["上方", "右上", "右下", "下方", "左下", "左上"], manualPracticePosition: false, libraryLoading: false, practiceState: null, noteFeedback: new Map(), dpr: 1, immersive: false, autoImmersion: false };
  let labels = ["1", "2", "3", "4", "5", "6", "J"];
  let statusTimer = 0, dragTimer = 0, dragStarted = false, renderFrameId = 0;
  let logicalWidth = 0, logicalHeight = 0;
  let noteWindowCursor = 0, noteWindowTimeUs = 0, noteWindowInitialized = false;
  const visibleNotes = [];
  let libraryTimer = 0, libraryQuery = "", librarySource = "local";
  const send = (type, payload = {}) => window.chrome?.webview?.postMessage({ type, payload });
  const fmt = us => { const s = Math.floor(Math.max(0, us) / 1e6); return `${String(Math.floor(s / 60)).padStart(2, "0")}:${String(s % 60).padStart(2, "0")}`; };
  const lowerBound = (items, time, getTime) => { let low = 0, high = items.length; while (low < high) { const mid = (low + high) >>> 1; if (getTime(items[mid]) < time) low = mid + 1; else high = mid; } return low; };
  const showStatus = (text, kind = "") => { $("status-line").textContent = text; $("status-line").dataset.feedback = kind; state.statusUntil = performance.now() + 1000; };
  const applyMode = mode => { if (mode === "practice") mode = "rhythm-normal"; state.mode = mode; $("play-mode").value = mode; $("switch-mode").disabled = true; $("execution-mode").disabled = mode === "auto-showcase" || mode === "simulated"; $("switch-mode").value = mode === "rhythm-hell" ? "manual" : "auto"; if (mode === "auto-showcase") showStatus("锁屏可打：开始后自动换位并向前台游戏发送按键"); else if (mode === "simulated") showStatus("MIDI 跟谱：完整曲目持续播放，按键命中后高亮对应音符"); requestRender(); };
  let lastStartPage = 1;
  const startPagePicker = $("start-page-picker"), startPageSelect = $("start-page-select");
  const askStartPage = () => state.mode === "auto-showcase" || (state.mode !== "simulated" && state.executionMode === "semi-auto");
  const startPlayback = initialPage => send("play.start", initialPage ? { initialPage } : {});
  const openStartPagePicker = () => {
    startPageSelect.value = String(lastStartPage);
    startPagePicker.hidden = false;
    startPageSelect.focus();
  };
  $("start-page-confirm").addEventListener("click", () => {
    const page = Number(startPageSelect.value);
    if (!Number.isInteger(page) || page < 1 || page > 5) return;
    lastStartPage = page;
    startPagePicker.hidden = true;
    startPlayback(page);
  });
  $("start-page-cancel").addEventListener("click", () => { startPagePicker.hidden = true; });
  startPagePicker.addEventListener("click", event => { if (event.target === startPagePicker) startPagePicker.hidden = true; });
  startPagePicker.addEventListener("keydown", event => {
    if (event.key === "Escape") startPagePicker.hidden = true;
    if (event.key === "Enter" && event.target === startPageSelect) $("start-page-confirm").click();
  });
  const setMode = mode => { applyMode(mode); send("mode.change", { mode }); };
  const applyLayout = layout => {
    const wide = layout === "wide";
    document.body.classList.toggle("wide-layout", wide);
    const button = $("layout-mode");
    button.setAttribute("aria-pressed", String(wide));
    button.textContent = wide ? "竖版" : "宽版";
    button.title = wide ? "切换为竖版悬浮窗" : "切换为宽版悬浮窗";
    resize();
    requestRender();
  };
  const setExecutionMode = mode => { state.executionMode = mode === "full-manual" ? "full-manual" : "semi-auto"; $("execution-mode").value = state.executionMode; send("execution.mode.change", { mode: state.executionMode }); showStatus(state.executionMode === "semi-auto" ? "半自动：请绑定游戏窗口并保持其在前台" : "全手动：按位置提示自行翻页和选槽"); };
  const readColors = () => { const s = getComputedStyle(document.body); return {
    text: s.getPropertyValue("--text").trim(), muted: s.getPropertyValue("--muted").trim(), line: s.getPropertyValue("--line").trim(), lane: s.getPropertyValue("--lane").trim(),
    note: s.getPropertyValue("--note").trim(), noteBorder: s.getPropertyValue("--note-border").trim(), sw: s.getPropertyValue("--switch").trim(), swBorder: s.getPropertyValue("--switch-border").trim(), hit: s.getPropertyValue("--hit").trim(), error: s.getPropertyValue("--error").trim(), laneDivider: s.getPropertyValue("--lane-divider").trim() }; };
  let palette = readColors();
  const refreshPalette = () => { palette = readColors(); laneBackgroundDirty = true; requestRender(); };
  const setText = (id, value) => { const el = $(id), text = String(value); if (el && el.textContent !== text) el.textContent = text; };
  function applyLaneKeys(keys) {
    if (!Array.isArray(keys) || keys.length !== 7) return;
    labels = keys.map(key => String(key || "?"));
    laneBackgroundDirty = true;
    for (let i = 0; i < 7; i++) {
      setText(`lane-key-${i + 1}`, labels[i]);
      const cell = $("keybar").querySelector(`[data-lane="${i + 1}"]`);
      if (cell) cell.textContent = labels[i];
    }
    $("falling-lanes").setAttribute("aria-label", `七轨按键：${labels.join("、")}`);
    requestRender();
  }
  function resize(width, height) {
    const rect = width == null || height == null ? canvas.getBoundingClientRect() : null;
    const nextWidth = Math.max(0, width ?? rect.width), nextHeight = Math.max(0, height ?? rect.height);
    if (nextWidth !== logicalWidth || nextHeight !== logicalHeight) laneBackgroundDirty = true;
    logicalWidth = nextWidth;
    logicalHeight = nextHeight;
    const deviceDpr = Math.min(window.devicePixelRatio || 1, 1.25);
    const pixelBudgetDpr = logicalWidth * logicalHeight > 0 ? Math.sqrt(2_000_000 / (logicalWidth * logicalHeight)) : deviceDpr;
    state.dpr = Math.min(deviceDpr, pixelBudgetDpr);
    const pixelWidth = Math.max(1, Math.round(logicalWidth * state.dpr));
    const pixelHeight = Math.max(1, Math.round(logicalHeight * state.dpr));
    if (canvas.width !== pixelWidth || canvas.height !== pixelHeight) {
      canvas.width = pixelWidth; canvas.height = pixelHeight;
      laneBackgroundDirty = true;
    }
    ctx.setTransform(state.dpr, 0, 0, state.dpr, 0, 0);
    requestRender();
  }
  new ResizeObserver(entries => {
    const rect = entries[entries.length - 1]?.contentRect;
    if (rect) resize(rect.width, rect.height);
  }).observe(canvas);
  function requestRender() {
    if (!renderFrameId && !document.body.classList.contains("dragging"))
      renderFrameId = requestAnimationFrame(render);
  }
  function renderLaneBackground(w, h, laneW) {
    const pixelWidth = canvas.width, pixelHeight = canvas.height;
    if (laneBackground.width !== pixelWidth || laneBackground.height !== pixelHeight) {
      laneBackground.width = pixelWidth;
      laneBackground.height = pixelHeight;
      laneBackgroundDirty = true;
    }
    if (laneBackgroundDpr !== state.dpr || laneBackgroundLogicalWidth !== w || laneBackgroundLogicalHeight !== h)
      laneBackgroundDirty = true;
    if (!laneBackgroundDirty) return;
    laneBackgroundDirty = false;
    laneBackgroundDpr = state.dpr;
    laneBackgroundLogicalWidth = w;
    laneBackgroundLogicalHeight = h;
    laneBackgroundContext.setTransform(state.dpr, 0, 0, state.dpr, 0, 0);
    laneBackgroundContext.clearRect(0, 0, w, h);
    laneBackgroundContext.textAlign = "center";
    laneBackgroundContext.textBaseline = "middle";
    for (let i = 0; i < 7; i++) {
      const x = i * laneW;
      laneBackgroundContext.fillStyle = i === 6 ? "rgba(96,58,154,.16)" : palette.lane;
      laneBackgroundContext.fillRect(x, 0, laneW, h);
      if (i > 0) {
        laneBackgroundContext.strokeStyle = palette.laneDivider;
        laneBackgroundContext.lineWidth = 1.5;
        laneBackgroundContext.beginPath();
        laneBackgroundContext.moveTo(x, 0);
        laneBackgroundContext.lineTo(x, h);
        laneBackgroundContext.stroke();
      }
      laneBackgroundContext.fillStyle = i === 6 ? palette.swBorder : palette.text;
      laneBackgroundContext.font = "700 11px Segoe UI, sans-serif";
      laneBackgroundContext.fillText(labels[i], x + laneW / 2, 17);
    }
  }
  function render() {
    renderFrameId = 0;
    const w = logicalWidth, h = logicalHeight, c = palette;
    if (!w || !h) return;
    const nowUs = state.playing ? state.nowUs + Math.max(0, performance.now() - (state.tickAt || performance.now())) * 1000 * state.speed / 100 : state.nowUs;
    const laneW = w / 7;
    const immersiveKeybarHeight = state.immersive ? Math.max(28, Math.min(h * .054, 43)) : 0;
    const hitY = state.immersive ? h - immersiveKeybarHeight - 5 : h - 2;
    const horizon = 2_800_000 * state.speed / 100, fallPx = hitY - 18;
    renderLaneBackground(w, h, laneW);
    ctx.clearRect(0, 0, w, h);
    ctx.drawImage(laneBackground, 0, 0, w, h);
    const yAt = t => hitY - ((t - nowUs) / horizon) * fallPx;
    if (!noteWindowInitialized || nowUs < noteWindowTimeUs - 50_000) {
      noteWindowCursor = 0;
      visibleNotes.length = 0;
      noteWindowInitialized = true;
    }
    const visibleUntilUs = nowUs + horizon;
    while (noteWindowCursor < state.notes.length && Number(state.notes[noteWindowCursor].startUs || 0) <= visibleUntilUs)
      visibleNotes.push(state.notes[noteWindowCursor++]);
    const visibleAfterUs = nowUs - 230_000;
    let visibleWriteIndex = 0;
    for (let i = 0; i < visibleNotes.length; i++) {
      const note = visibleNotes[i], end = Number(note.startUs || 0) + Math.max(0, Number(note.durationUs || 0));
      if (end >= visibleAfterUs) visibleNotes[visibleWriteIndex++] = note;
    }
    visibleNotes.length = visibleWriteIndex;
    noteWindowTimeUs = nowUs;
    for (const n of visibleNotes) {
      const lane = Number(n.gameKey); if (lane < 1 || lane > 6) continue;
      const start = Number(n.startUs || 0), duration = Math.max(0, Number(n.durationUs || 0)), end = start + duration;
      if (end < nowUs - 230_000 || start > nowUs + horizon) continue;
      let top = duration < 35_000 ? yAt(start) : yAt(end), bottom = duration < 35_000 ? top + 13 : yAt(start);
      const layout = state.noteColumns.get(String(n.id)) || { column: 0, columns: 1 };
      const slotW = laneW / layout.columns, bw = Math.max(5, slotW * .84);
      const x = (lane - 1) * laneW + layout.column * slotW + (slotW - bw) / 2;
      const feedback = state.noteFeedback.get(String(n.id));
      if (feedback && feedback.expiresAt <= performance.now()) state.noteFeedback.delete(String(n.id));
      const result = feedback && feedback.expiresAt > performance.now() ? feedback.grade : "";
      const topClip = Math.max(0, Math.min(h, top)), bottomClip = Math.max(0, Math.min(h, bottom));
      if (bottomClip <= topClip) continue;
      ctx.shadowBlur = 0;
      ctx.fillStyle = result === "Miss" ? c.error : (result ? c.hit : c.note);
      ctx.strokeStyle = result === "Miss" ? c.error : (result ? c.hit : c.noteBorder); ctx.lineWidth = result ? 2 : 1;
      ctx.beginPath(); ctx.roundRect(x, topClip, bw, Math.max(8, bottomClip - topClip), Math.min(8, bw / 2)); ctx.fill(); ctx.stroke();
      if (!state.immersive && bw >= 17 && bottomClip - topClip >= 15) {
        ctx.fillStyle = result === "Miss" ? "#fff" : "rgba(255,255,255,.92)";
        ctx.font = "700 8px Segoe UI, sans-serif";
        ctx.fillText(String(n.noteNumber ?? ""), x + bw / 2, Math.max(topClip + 8, Math.min(bottomClip - 7, topClip + 12)));
      }
      if (duration >= 35_000 && bottomClip - topClip > 18) {
        ctx.fillStyle = "rgba(240,251,255,.9)"; ctx.beginPath(); ctx.roundRect(x + bw * .15, topClip + 2, bw * .7, 3, 2); ctx.fill();
        ctx.beginPath(); ctx.roundRect(x + bw * .15, Math.max(topClip + 2, bottomClip - 5), bw * .7, 3, 2); ctx.fill();
      }
    }
    if (!state.immersive) {
      const positionStart = Math.max(0, lowerBound(state.positions, nowUs - horizon - 300_000, ev => Number(ev.targetTimeUs ?? ev.targetTimeUS ?? 0)) - 1);
      const positionIndex = lowerBound(state.positions, nowUs, ev => Number(ev.targetTimeUs ?? ev.targetTimeUS ?? 0));
      let currentPosition = positionIndex > 0 ? state.positions[positionIndex - 1] : null;
      let nextPosition = state.positions[positionIndex] || null;
      if (!currentPosition) {
        const noteIndex = lowerBound(state.notePositionChanges, nowUs, n => Number(n.startUs || 0));
        const firstNote = noteIndex > 0 ? state.notePositionChanges[noteIndex - 1] : state.notePositionChanges[0];
        if (firstNote) currentPosition = { to: firstNote.position };
      }
      if (!nextPosition && currentPosition) {
        const from = currentPosition.to || currentPosition.To;
        const nextIndex = lowerBound(state.notePositionChanges, nowUs + 1, n => Number(n.startUs || 0));
        let nextNote = state.notePositionChanges[nextIndex];
        if (nextNote && nextNote.position.page === (from?.page ?? from?.Page) && nextNote.position.slot === (from?.slot ?? from?.Slot)) nextNote = state.notePositionChanges[nextIndex + 1];
        if (nextNote) nextPosition = { to: nextNote.position };
      }
      const wheelSide = slot => state.wheelSides[slot - 1] || "未知方向";
      const posText = ev => {
        if (!ev) return "P— · S—";
        const position = ev.to || ev.To || ev.position || ev.Position || {};
        const page = position.page ?? position.Page ?? "—", slot = Number(position.slot ?? position.Slot);
        const side = wheelSide(slot), slotText = Number.isFinite(slot) ? `S${slot}` : "S—";
        const details = `P${page} · 轮盘${side} · ${slotText}${position.fret != null || position.Fret != null ? ` · ${position.fret ?? position.Fret}品` : ""}`;
        const currentTitle = $("status-current"), nextTitle = $("status-next");
        if (ev === currentPosition && currentTitle.title !== `起始 / 当前轮盘：${details}`) currentTitle.title = `起始 / 当前轮盘：${details}`;
        if (ev === nextPosition && nextTitle.title !== `下一个轮盘位置：${details}`) nextTitle.title = `下一个轮盘位置：${details}`;
        return `P${page} · ${side} ${slotText}`;
      };
      setText("status-current", posText(currentPosition));
      setText("status-next", posText(nextPosition));
      for (let pi = positionStart; pi < state.positions.length && Number(state.positions[pi].targetTimeUs ?? state.positions[pi].targetTimeUS ?? 0) <= nowUs + horizon; pi++) {
        const ev = state.positions[pi], t = Number(ev.targetTimeUs ?? ev.targetTimeUS ?? 0);
        const nextTime = Number(state.positions[pi + 1]?.targetTimeUs ?? state.positions[pi + 1]?.targetTimeUS ?? state.durationUs);
        const y = yAt(t), nextY = yAt(Math.max(t, nextTime));
        if (Math.max(y, nextY) < -24 || Math.min(y, nextY) > h + 22) continue;
        const x = 6 * laneW + 3, bw = Math.max(10, laneW - 6);
        const barTop = Math.max(0, Math.min(h, Math.min(y, nextY))), barBottom = Math.max(barTop + 13, Math.max(0, Math.min(h, Math.max(y, nextY))));
        ctx.shadowBlur = 0; ctx.fillStyle = c.sw;
        ctx.strokeStyle = Math.abs(t - nowUs) < 220_000 ? c.hit : c.swBorder; ctx.lineWidth = 1.5;
        ctx.beginPath(); ctx.roundRect(x, barTop, bw, Math.max(13, barBottom - barTop), 7); ctx.fill(); ctx.stroke();
        if (barBottom - barTop > 17) { ctx.fillStyle = "rgba(255,255,255,.78)"; ctx.beginPath(); ctx.roundRect(x + bw * .18, barTop + 2, bw * .64, 2, 1); ctx.fill(); ctx.beginPath(); ctx.roundRect(x + bw * .18, barBottom - 4, bw * .64, 2, 1); ctx.fill(); }
        const labelY = Math.max(12, Math.min(h - 12, y));
        ctx.fillStyle = "#fff"; ctx.font = "700 8px Segoe UI, sans-serif"; ctx.fillText(`P${ev.to?.page ?? ev.to?.Page ?? "?"}·S${ev.to?.slot ?? ev.to?.Slot ?? "?"}`, x + bw / 2, labelY);
      }
    }
    ctx.save(); ctx.shadowBlur = 0; ctx.strokeStyle = c.hit; ctx.lineWidth = 2;
    ctx.beginPath(); ctx.moveTo(0, hitY); ctx.lineTo(w, hitY); ctx.stroke();
    ctx.restore();
    if (!state.immersive) {
      const remain = Math.max(0, state.durationUs - nowUs), elapsedText = fmt(nowUs), totalText = fmt(state.durationUs);
      setText("elapsed-label", elapsedText); setText("duration-label", totalText);
      setText("timeline-current", elapsedText); setText("timeline-total", totalText); setText("status-remain", fmt(remain));
      const progress = state.durationUs ? Math.min(1, nowUs / state.durationUs) : 0;
      $("song-progress-fill").style.transform = `scaleX(${progress})`;
      if (!state.seeking) {
        const seekValue = String(state.durationUs ? Math.round(progress * 1000) : 0);
        if ($("timeline-seek").value !== seekValue) $("timeline-seek").value = seekValue;
      }
      if (!state.playing && performance.now() > (state.statusUntil || 0)) {
        setText("status-line", state.mode === "practice" && state.practiceState?.playing ? "练习等待按键" : (state.chart ? "琴谱已就绪" : "就绪"));
        if ($("status-line").dataset.feedback) $("status-line").dataset.feedback = "";
      }
    }
    if (state.playing) requestRender();
  }
  function setChart(chart) {
    state.chart = chart || null; state.practiceState = null; state.nowUs = 0; state.durationUs = Number(chart?.durationUs || 0);
    state.noteFeedback.clear();
    state.notes = (chart?.notes || []).filter(n => Number(n.gameKey) >= 1 && Number(n.gameKey) <= 6).slice().sort((a, b) => Number(a.startUs) - Number(b.startUs));
    noteWindowCursor = 0;
    noteWindowTimeUs = 0;
    noteWindowInitialized = false;
    visibleNotes.length = 0;
    state.noteColumns.clear();
    for (let lane = 1; lane <= 6; lane++) {
      const laneNotes = state.notes.filter(n => Number(n.gameKey) === lane);
      let cluster = [], clusterEnd = -1;
      const placeCluster = () => {
        if (!cluster.length) return;
        const slotEnds = [];
        const placed = [];
        for (const note of cluster) {
          const start = Number(note.startUs || 0), duration = Math.max(0, Number(note.durationUs || 0));
          const end = Math.max(start + 60_000, start + duration);
          let column = slotEnds.findIndex(slotEnd => slotEnd <= start);
          if (column < 0) column = slotEnds.length;
          slotEnds[column] = end;
          placed.push([note, column]);
        }
        const columns = Math.max(1, slotEnds.length);
        for (const [note, column] of placed) state.noteColumns.set(String(note.id), { column, columns });
        cluster = [];
      };
      for (const note of laneNotes) {
        const start = Number(note.startUs || 0), end = Math.max(start + 60_000, start + Math.max(0, Number(note.durationUs || 0)));
        if (cluster.length && start >= clusterEnd) placeCluster();
        cluster.push(note);
        clusterEnd = Math.max(clusterEnd, end);
      }
      placeCluster();
    }
    const positionedNotes = state.notes.filter(n => n.position);
    state.notePositionChanges = positionedNotes.filter((n, index) => index === 0 ||
      n.position.page !== positionedNotes[index - 1].position.page || n.position.slot !== positionedNotes[index - 1].position.slot);
    state.positions = (chart?.positionEvents || []).slice().sort((a, b) => Number(a.targetTimeUs) - Number(b.targetTimeUs));
    $("empty-state").hidden = Boolean(chart?.notes?.length || chart?.positionEvents?.length);
    if (chart) {
      $("track-title").textContent = chart.sourceName || "已载入曲目";
      send("chart.loaded", { chartId: chart.chartId, noteCount: chart.notes?.length || 0 });
    }
    requestRender();
  }
  $("play-mode").addEventListener("change", e => setMode(e.target.value));
  $("switch-mode").addEventListener("change", e => send("position.manual-practice", { enabled: e.target.value === "manual" }));
  let speedTimer = 0;
  $("speed-range").addEventListener("input", e => { state.speed = Number(e.target.value); $("speed-value").textContent = `${state.speed}%`; clearTimeout(speedTimer); const value = state.speed; speedTimer = setTimeout(() => send("speed.change", { speedPercent: value }), 80); });
  $("execution-mode").addEventListener("change", e => setExecutionMode(e.target.value));
  $("theme-button").addEventListener("click", () => {
    state.theme = state.theme === "dark" ? "light" : "dark";
    document.documentElement.dataset.theme = state.theme;
    $("theme-button").textContent = state.theme === "dark" ? "☾" : "☀";
    refreshPalette();
    send("theme.change", { theme: state.theme });
  });
  $("start-button").addEventListener("click", () => {
    if (state.playing || state.practiceState?.playing || !startPagePicker.hidden) return;
    if (askStartPage()) openStartPagePicker();
    else startPlayback(null);
  });
  $("pause-button").addEventListener("click", () => send("play.pause", {}));
  $("reset-button").addEventListener("click", () => {
    send("play.reset", {});
  });
  $("timeline-seek").addEventListener("pointerdown", () => { state.seeking = true; });
  $("timeline-seek").addEventListener("pointerup", () => { state.seeking = false; });
  $("timeline-seek").addEventListener("change", e => { const ratio = Number(e.currentTarget.value) / 1000; send("play.seek", { ratio }); state.seeking = false; });
  $("hide-button").addEventListener("click", () => send("window.hide", {}));
  const lanePicker = $("lane-key-picker"), laneKeyStatus = $("lane-key-status");
  let laneCaptureActive = false;
  function closeLaneKeyPicker() {
    if (laneCaptureActive) send("lane-key.cancel", {});
    laneCaptureActive = false;
    lanePicker.hidden = true;
  }
  $("lane-key-settings").addEventListener("click", () => {
    lanePicker.hidden = false;
    laneKeyStatus.textContent = `当前键位：${labels.join(" ")}`;
    laneKeyStatus.dataset.error = "false";
    $("lane-key-close").focus();
  });
  $("lane-key-close").addEventListener("click", closeLaneKeyPicker);
  lanePicker.addEventListener("click", e => { if (e.target === lanePicker) closeLaneKeyPicker(); });
  lanePicker.addEventListener("click", e => {
    const button = e.target.closest("button[data-capture-lane]");
    if (!button) return;
    const lane = Number(button.dataset.captureLane);
    laneKeyStatus.textContent = `请按下轨道 ${lane} 的新键，Esc 取消…`;
    laneKeyStatus.dataset.error = "false";
    send("lane-key.capture", { lane });
  });
  $("lane-key-reset").addEventListener("click", () => {
    laneKeyStatus.textContent = "正在恢复默认键位…";
    laneKeyStatus.dataset.error = "false";
    send("lane-key.reset", {});
  });
  const picker = $("library-picker"), librarySearch = $("library-search"), libraryResults = $("library-results");
  function requestLibrary(query = "") {
    libraryQuery = query.trim();
    $("library-count").textContent = "正在读取曲目…";
    $("library-status").textContent = librarySource === "recommended" ? "读取服务器推荐谱…" : "只显示吉他曲目";
    libraryResults.replaceChildren();
    $("library-empty").hidden = true;
    send(librarySource === "recommended" ? "library.recommended.list" : "library.guitar.list", { query: libraryQuery });
  }
  function setLibrarySource(source) {
    librarySource = source;
    const recommended = source === "recommended";
    $("library-tab-local").classList.toggle("active", !recommended);
    $("library-tab-local").setAttribute("aria-selected", String(!recommended));
    $("library-tab-recommended").classList.toggle("active", recommended);
    $("library-tab-recommended").setAttribute("aria-selected", String(recommended));
    $("library-search-label").textContent = recommended ? "搜索推荐吉他谱" : "搜索本地吉他曲目";
    librarySearch.placeholder = recommended ? "输入曲名或投稿者搜索" : "输入曲名搜索";
    requestLibrary(librarySearch.value);
  }
  function closeLibrary() { picker.hidden = true; state.libraryLoading = false; librarySearch.disabled = false; }
  $("library-button").addEventListener("click", () => { picker.hidden = false; librarySearch.value = ""; setLibrarySource("local"); librarySearch.focus(); });
  $("library-tab-local").addEventListener("click", () => setLibrarySource("local"));
  $("library-tab-recommended").addEventListener("click", () => setLibrarySource("recommended"));
  $("library-close").addEventListener("click", closeLibrary);
  picker.addEventListener("click", e => { if (e.target === picker) closeLibrary(); });
  document.addEventListener("keydown", e => {
    if (e.key !== "Escape") return;
    if (!lanePicker.hidden) {
      e.preventDefault();
      if (laneCaptureActive) {
        send("lane-key.cancel", {});
        laneCaptureActive = false;
        laneKeyStatus.textContent = "已取消按键录入";
      } else closeLaneKeyPicker();
    } else if (!picker.hidden) closeLibrary();
    else if (!immersionPicker.hidden) { cancelImmersionCapture(); immersionPicker.hidden = true; }
  });
  librarySearch.addEventListener("input", e => { clearTimeout(libraryTimer); const query = e.target.value; libraryTimer = setTimeout(() => requestLibrary(query), 180); });
  libraryResults.addEventListener("click", e => {
    const button = e.target.closest("button[data-song-id]");
    if (!button) return;
    state.libraryLoading = true; librarySearch.disabled = true;
    libraryResults.querySelectorAll("button").forEach(item => { item.disabled = true; });
    $("library-status").textContent = `正在载入「${button.dataset.songName}」并生成吉他谱…`;
    if (button.dataset.songSource === "recommended")
      send("library.recommended.select", { scoreId: button.dataset.songId });
    else send("library.guitar.select", { songId: button.dataset.songId });
  });
  $("layout-mode").addEventListener("click", () => send("window.layout", { layout: document.body.classList.contains("wide-layout") ? "portrait" : "wide" }));
  const immersionPicker = $("immersion-picker"), immersionStatus = $("immersion-hotkey-status");
  let immersionCaptureActive = false;
  function cancelImmersionCapture() {
    if (!immersionCaptureActive) return;
    immersionCaptureActive = false;
    send("immersion.hotkey.cancel", {});
  }
  $("immersion-settings-button").addEventListener("click", () => { immersionPicker.hidden = false; $("immersion-dialog").focus(); });
  $("immersion-close").addEventListener("click", () => { cancelImmersionCapture(); immersionPicker.hidden = true; });
  immersionPicker.addEventListener("click", event => { if (event.target === immersionPicker) { cancelImmersionCapture(); immersionPicker.hidden = true; } });
  $("immersion-auto").addEventListener("change", event => {
    send("immersion.auto", { enabled: event.target.checked });
    immersionStatus.textContent = event.target.checked ? "播放推进后自动进入，暂停或结束时退出" : "自动进入已关闭";
  });
  $("immersion-hotkey-capture").addEventListener("click", () => {
    immersionCaptureActive = true;
    immersionStatus.textContent = "正在启动全局按键捕获…";
    immersionStatus.dataset.error = "false";
    send("immersion.hotkey.capture", {});
  });
  let opacityTimer = 0;
  $("opacity-range").addEventListener("input", e => { $("opacity-value").textContent = `${e.target.value}%`; clearTimeout(opacityTimer); const value = Number(e.target.value); opacityTimer = setTimeout(() => send("window.opacity", { percent: value }), 120); });
  const drag = $("drag-handle");
  const setDraggingVisual = dragging => {
    document.body.classList.toggle("dragging", dragging);
    if (dragging && renderFrameId) { cancelAnimationFrame(renderFrameId); renderFrameId = 0; }
    else if (!dragging) requestRender();
  };
  drag.addEventListener("pointerdown", e => { dragStarted = false; drag.setPointerCapture(e.pointerId); dragTimer = setTimeout(() => { dragStarted = true; setDraggingVisual(true); send("window.drag", {}); }, 450); });
  for (const type of ["pointerup", "pointercancel", "lostpointercapture"]) drag.addEventListener(type, () => { clearTimeout(dragTimer); if (!dragStarted) setDraggingVisual(false); });
  window.chrome?.webview?.addEventListener("message", e => {
    const { type, payload = {} } = e.data || {};
    switch (type) {
      case "chart.render": setChart(payload.chart); closeLibrary(); break;
      case "chart.clear": setChart(null); $("track-title").textContent = payload.message || "尚未载入曲目"; closeLibrary(); break;
      case "library.guitar.list": {
        if (payload.query !== libraryQuery || librarySource !== "local") break;
        const songs = Array.isArray(payload.data?.items) ? payload.data.items : [];
        $("library-count").textContent = `${songs.length} 首吉他曲目`;
        $("library-empty").hidden = songs.length > 0;
        for (const song of songs) {
          const button = document.createElement("button"); button.type = "button"; button.className = "library-song"; button.dataset.songId = String(song.id || ""); button.dataset.songName = String(song.name || "曲目"); button.setAttribute("role", "option");
          const main = document.createElement("span"); main.className = "library-song-main";
          const name = document.createElement("span"); name.className = "library-song-name"; name.textContent = song.name || "未命名吉他曲目";
          const meta = document.createElement("span"); meta.className = "library-song-meta"; meta.textContent = `${song.noteCount ?? 0} 个音符 · ${song.instrumentName || "吉他"}`;
          const duration = document.createElement("span"); duration.className = "library-song-duration"; duration.textContent = song.durationFormatted || fmt(Number(song.durationUs || 0));
          main.append(name, meta); button.append(main, duration); button.disabled = state.libraryLoading; libraryResults.append(button);
        }
        break;
      }
      case "library.recommended.list": {
        if (payload.query !== libraryQuery || librarySource !== "recommended") break;
        const songs = Array.isArray(payload.data?.items) ? payload.data.items : [];
        const unavailable = payload.data?.status === "unavailable";
        $("library-count").textContent = unavailable ? "服务暂不可用" : `${songs.length} 首推荐吉他谱`;
        $("library-empty").hidden = songs.length > 0;
        $("library-empty").textContent = unavailable ? "暂时无法连接推荐谱服务" : "没有匹配的推荐吉他谱";
        $("library-status").textContent = payload.data?.offline ? "离线目录 · 选取后使用本地缓存" : "选取后下载（已下载的曲目会使用本地缓存）";
        for (const song of songs) {
          const button = document.createElement("button"); button.type = "button"; button.className = "library-song";
          button.dataset.songId = String(song.id || ""); button.dataset.songSource = "recommended"; button.dataset.songName = String(song.title || "推荐谱"); button.setAttribute("role", "option");
          const main = document.createElement("span"); main.className = "library-song-main";
          const name = document.createElement("span"); name.className = "library-song-name"; name.textContent = song.title || "未命名推荐谱";
          const meta = document.createElement("span"); meta.className = "library-song-meta recommended-song-meta";
          const level = document.createElement("span"); level.className = "library-difficulty-badge"; level.textContent = String(song.difficultyLevel || "—"); level.title = `等级 ${song.difficultyLevel || "未设置"}`;
          const detail = document.createElement("span"); detail.className = "library-recommended-detail";
          const label = song.difficultyLabel ? ` · ${song.difficultyLabel}` : "";
          const uploader = song.uploaderName ? ` · 投稿者 ${song.uploaderName}` : " · 推荐谱";
          detail.textContent = `${label}${uploader}`;
          meta.append(level, detail);
          const status = document.createElement("span"); status.className = `library-cache-badge${song.isCached ? " is-cached" : ""}`; status.textContent = song.isCached ? "已缓存" : "下载";
          main.append(name, meta); button.append(main, status); button.disabled = state.libraryLoading; libraryResults.append(button);
        }
        break;
      }
      case "library.guitar.loading": state.libraryLoading = true; librarySearch.disabled = true; $("library-status").textContent = "正在载入曲目并生成七轨谱面…"; break;
      case "theme.set": state.theme = payload.theme === "light" ? "light" : "dark"; document.documentElement.dataset.theme = state.theme; $("theme-button").textContent = state.theme === "dark" ? "☾" : "☀"; refreshPalette(); break;
      case "mode.state": {
        const mode = payload.mode === "practice" ? "rhythm-normal" : (payload.mode || "rhythm-normal");
        applyMode(mode);
        if (payload.mode === "practice") send("mode.change", { mode });
        break;
      }
      case "wheel.layout": {
        const slots = payload.slots || [];
        if (slots.length >= 6) state.wheelSides = slots.slice(0, 6).map(({ x, y }) => {
          const dx = Number(x) - .5, dy = Number(y) - .5;
          if (!Number.isFinite(dx) || !Number.isFinite(dy) || Math.hypot(dx, dy) < .05) return "中心";
          const directions = ["右", "右下", "下方", "左下", "左", "左上", "上方", "右上"];
          return directions[(Math.round(Math.atan2(dy, dx) / (Math.PI / 4)) + 8) % 8];
        });
        requestRender();
        break;
      }
      case "speed.state": state.speed = Number(payload.speedPercent ?? 100); $("speed-range").value = String(state.speed); $("speed-value").textContent = `${state.speed}%`; break;
      case "playback.tick": {
        const receivedAt = performance.now(), incomingUs = Number(payload.currentTimeUs ?? state.nowUs), nextPlaying = Boolean(payload.playing);
        if (state.playing && nextPlaying && Number.isFinite(state.tickAt)) {
          const predictedUs = state.nowUs + Math.max(0, receivedAt - state.tickAt) * 1000 * state.speed / 100;
          const correctionUs = Math.max(-12_000, Math.min(12_000, (incomingUs - predictedUs) * .12));
          state.nowUs = predictedUs + correctionUs;
        } else state.nowUs = incomingUs;
        state.tickAt = receivedAt; state.durationUs = Number(payload.durationUs ?? state.durationUs); state.playing = nextPlaying;
        $("lane-key-settings").disabled = state.playing;
        document.querySelectorAll("[data-capture-lane], #lane-key-reset").forEach(button => { button.disabled = state.playing; });
        $("playfield").classList.toggle("is-playing", state.playing || (state.mode === "practice" && Boolean(state.practiceState?.playing)));
        requestRender();
        break;
      }
      case "playback.countdown": {
        const overlay = $("countdown-overlay");
        overlay.hidden = !payload.active;
        if (payload.active) $("countdown-number").textContent = String(payload.seconds ?? 3);
        break;
      }
      case "playback.start-page.request":
        if (!state.playing && startPagePicker.hidden) openStartPagePicker();
        break;
      case "practice.state": state.practiceState = payload; $("session-message").textContent = payload.currentRequiredKeys?.length ? `当前目标：游戏键 ${payload.currentRequiredKeys.join(" + ")} · ${payload.timelineRunning ? "按住推进中" : "按住开始/继续，松开暂停"}` : (payload.message || "练习已完成"); if (payload.currentPage) $("status-current").textContent = `P${payload.currentPage} · S${payload.currentSlot}`; if (payload.playing && state.mode === "practice") $("playfield").classList.add("is-playing"); else $("playfield").classList.remove("is-playing"); requestRender(); break;
      case "position.active": $("status-current").textContent = `P${payload.page} · S${payload.slot}`; break;
      case "position.prompt": $("status-next").textContent = `P${payload.targetPage} · S${payload.targetSlot}`; showStatus(payload.stateText || `请切换至 P${payload.targetPage} · Slot ${payload.targetSlot}${payload.fret != null ? ` · ${payload.fret}品` : ""}`); break;
      case "execution.mode.state": state.executionMode = payload.mode; $("execution-mode").value = payload.mode; break;
      case "position.manual-practice.state": state.manualPracticePosition = Boolean(payload.enabled); if (state.mode === "practice") $("switch-mode").value = state.manualPracticePosition ? "manual" : "auto"; break;
      case "lane-key.state": applyLaneKeys(payload.keys); break;
      case "lane-key.capture.state":
        laneCaptureActive = Boolean(payload.active);
        if (laneCaptureActive) { laneKeyStatus.textContent = `正在等待轨道 ${payload.lane} 的按键…Esc 取消`; laneKeyStatus.dataset.error = "false"; }
        else if (payload.cancelled) laneKeyStatus.textContent = "已取消按键录入";
        else if (!laneKeyStatus.textContent.includes("取消")) laneKeyStatus.textContent = "按键设置已更新";
        break;
      case "lane-key.error": laneKeyStatus.textContent = payload.message || "该键不能使用"; laneKeyStatus.dataset.error = "true"; break;
      case "position-key.bound": if ($("bound-key")) $("bound-key").textContent = payload.label || "已绑定"; showStatus(`换位键：${payload.label || "已绑定"}`); break;
      case "position-key.capture": showStatus("按下要绑定的位置切换键…"); break;
      case "position-key.error": showStatus(payload.message || "此键不能绑定", "wrong"); break;
      case "window.opacity": $("opacity-range").value = payload.percent; $("opacity-value").textContent = `${payload.percent}%`; break;
      case "window.layout": applyLayout(payload.layout); break;
      case "immersion.state": {
        state.immersive = Boolean(payload.enabled);
        state.autoImmersion = Boolean(payload.auto);
        document.body.classList.toggle("immersive", state.immersive);
        $("immersion-auto").checked = state.autoImmersion;
        $("immersion-hotkey-label").textContent = payload.hotkey || "I";
        if (!immersionCaptureActive) immersionStatus.textContent = `快捷键：${payload.hotkey || "I"}`;
        refreshPalette();
        resize();
        break;
      }
      case "immersion.hotkey.error":
        immersionCaptureActive = false;
        immersionStatus.textContent = payload.message || "快捷键设置失败";
        immersionStatus.dataset.error = "true";
        break;
      case "immersion.hotkey.capture.state":
        immersionCaptureActive = Boolean(payload.active);
        if (payload.message) immersionStatus.textContent = payload.message;
        else if (payload.cancelled) immersionStatus.textContent = "已取消快捷键设置";
        immersionStatus.dataset.error = String(Boolean(payload.error));
        break;
      case "input.feedback": {
        const cell = document.querySelector(`.keybar [data-lane="${payload.lane}"]`); if (cell) cell.classList.toggle("active", Boolean(payload.pressed));
        if (payload.lane === 7 && payload.pressed) showStatus("换位键已按下", "correct"); break;
      }
      case "note.feedback": {
        const id = String(payload.noteId || "");
        if (!id) break;
        const noteDurationMs = Number(payload.durationUs || 0) / 1000 * 100 / Math.max(25, state.speed);
        const feedbackDurationMs = payload.grade === "Miss" ? Math.max(500, noteDurationMs) : Math.max(120, noteDurationMs);
        state.noteFeedback.set(id, { grade: payload.grade || "Miss", expiresAt: performance.now() + feedbackDurationMs });
        requestRender();
        break;
      }
      case "window.drag.state": dragStarted = Boolean(payload.dragging); setDraggingVisual(dragStarted); break;
      case "app.toast":
        if (payload.level === "error" && state.libraryLoading) { state.libraryLoading = false; librarySearch.disabled = false; libraryResults.querySelectorAll("button").forEach(item => { item.disabled = false; }); $("library-status").textContent = payload.message || "载入失败，请重新选择。"; }
        showStatus(payload.message || "状态已更新", payload.level === "error" ? "wrong" : ""); break;
    }
  });
  applyMode("rhythm-normal"); resize(); send("web.ready", { version: "3.4-responsive-falling-lanes" }); requestRender();
})();
