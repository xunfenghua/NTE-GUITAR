const NATURAL_SEMITONES = new Set([0, 2, 4, 5, 7, 9, 11]);
const pressedPitchSets = new WeakMap();

export function getPianoPitchChanges(previous, next) {
  const pressed = new Set();
  const released = new Set();
  for (const pitch of next) if (!previous.has(pitch)) pressed.add(pitch);
  for (const pitch of previous) if (!next.has(pitch)) released.add(pitch);
  return { pressed, released };
}

export function getPianoKeyboardPitches(layoutKind) {
  const pitches = [];
  if (layoutKind === "SevenKeyScale" || layoutKind === 0) {
    for (let octave = 0; octave < 3; octave++) {
      for (const semitone of NATURAL_SEMITONES) pitches.push(48 + octave * 12 + semitone);
    }
    return pitches;
  }

  for (let pitch = 48; pitch < 84; pitch++) pitches.push(pitch);
  return pitches;
}

export function getActivePitchSet(activeNotes) {
  const pitches = new Set();
  for (const note of Array.isArray(activeNotes) ? activeNotes : []) {
    const pitch = Number(note?.midiPitch ?? note?.MidiPitch);
    if (Number.isInteger(pitch) && pitch >= 0 && pitch <= 127) pitches.add(pitch);
  }
  return pitches;
}

export function renderPianoKeyboard(container, layoutKind) {
  if (!container) return;
  const pitches = getPianoKeyboardPitches(layoutKind);
  const whitePitches = pitches.filter(pitch => NATURAL_SEMITONES.has(pitch % 12));
  const keyboardRange = layoutKind === "SevenKeyScale" || layoutKind === 0
    ? Array.from({ length: 36 }, (_, index) => index + 48)
    : pitches;
  const whitePosition = new Map();
  const fragment = document.createDocumentFragment();
  const noteNames = ["C", "C♯", "D", "D♯", "E", "F", "F♯", "G", "G♯", "A", "A♯", "B"];

  whitePitches.forEach((pitch, index) => {
    whitePosition.set(pitch, index);
    const key = document.createElement("div");
    key.className = "piano-key piano-key-white";
    key.dataset.midi = String(pitch);
    key.setAttribute("aria-hidden", "true");
    if (index % 3 === 0 || pitch % 12 === 0) {
      const label = document.createElement("span");
      label.className = "piano-key-label";
      label.textContent = `${noteNames[pitch % 12]}${Math.floor(pitch / 12) - 1}`;
      key.append(label);
    }
    fragment.append(key);
  });

  keyboardRange.filter(pitch => !NATURAL_SEMITONES.has(pitch % 12)).forEach(pitch => {
    const precedingWhiteIndex = whitePosition.get(pitch - 1);
    if (precedingWhiteIndex === undefined) return;
    const key = document.createElement("div");
    key.className = "piano-key piano-key-black";
    key.dataset.midi = String(pitch);
    key.style.left = `${(precedingWhiteIndex + 1) / whitePitches.length * 100}%`;
    key.setAttribute("aria-hidden", "true");
    fragment.append(key);
  });

  container.replaceChildren(fragment);
  pressedPitchSets.delete(container);
  container.dataset.layout = pitches.length === 21 ? "21" : "36";
  container.setAttribute("aria-label", `${pitches.length} 键钢琴键盘`);
  const range = document.getElementById("piano-keyboard-range");
  if (range) range.textContent = pitches.length === 21
    ? "21 键 · C3 — B5"
    : "36 键 · C3 — B5（含半音）";
}

export function updateActivePianoKeys(container, activeNotes) {
  if (!container) return;
  const activePitches = getActivePitchSet(activeNotes);
  const previous = pressedPitchSets.get(container) || new Set();
  const changes = getPianoPitchChanges(previous, activePitches);
  for (const pitch of changes.pressed) {
    container.querySelector(`[data-midi="${pitch}"]`)?.classList.add("is-pressed");
  }
  for (const pitch of changes.released) {
    container.querySelector(`[data-midi="${pitch}"]`)?.classList.remove("is-pressed");
  }
  pressedPitchSets.set(container, activePitches);
}
