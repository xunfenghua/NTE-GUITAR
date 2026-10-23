using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Guitar;

/// <summary>Beat arranged chord accompaniment with a fret mode fallback for uncertain chords.</summary>
public static class ArrangedAccompanimentCompiler
{
    private sealed record Frame(long TimeUs, GuitarPlayModeType Mode, byte Page, byte Slot,
        IReadOnlyList<(byte StringNo, byte Pitch)> Notes, bool Strum, StrumDirectionHint Direction);

    public static List<GuitarAction> Compile(IReadOnlyList<RawMidiNote> rawNotes,
        IReadOnlyList<ChordEvent> analyzedChords, int semitoneShift, double currentSpeed,
        WheelPreset preset, GuitarCompilerOptions options)
    {
        if (rawNotes.Count == 0 || analyzedChords.Count == 0) return new();
        var events = AccompanimentEventFilter.Prepare(rawNotes, analyzedChords, semitoneShift, currentSpeed);
        var frames = new List<Frame>();
        foreach (var ev in events)
        {
            bool hasTruth = GameChordGroundTruth.Table.TryGetValue(ev.Chord, out var truth);
            bool fretAssist = !hasTruth || (ev.Articulation != ChordArticulation.Strum &&
                (ev.Confidence < .48 || truth!.SafeCleanStrings.Length < 4));
            var source = ev.AttackNotes.Count > 0 ? ev.AttackNotes : ev.SourceNotes;
            if (fretAssist && TryBuildFretFrame(ev.StartUs, source, out var fretFrame))
            {
                frames.Add(fretFrame);
                continue;
            }

            byte page = truth?.Page ?? ev.Page;
            byte slot = truth?.Slot ?? ev.Slot;
            bool wantsStrum = options.PlayStyle == GuitarPlayStyle.StrumOnly ||
                (options.PlayStyle is not GuitarPlayStyle.PluckOnly and not GuitarPlayStyle.BlockOnly &&
                 ev.Articulation == ChordArticulation.Strum);
            bool strum = wantsStrum && options.AutoStrum != AutoStrumMode.Disabled && hasTruth;
            var mapped = new List<(byte, byte)>();
            if (!strum && truth != null)
            {
                var occupied = new HashSet<byte>();
                foreach (var note in source.OrderBy(n => n.StartUs).ThenByDescending(n => n.NoteNumber))
                {
                    byte s = GameChordGroundTruth.FindPitchClassCompatibleString(ev.Chord, note.NoteNumber, occupied);
                    if (s > 0 && occupied.Add(s)) mapped.Add((s, truth.ActualMidi1To6[s - 1]));
                }
                if (mapped.Count == 0)
                    foreach (byte s in truth.SafeCleanStrings.Take(4)) mapped.Add((s, truth.ActualMidi1To6[s - 1]));
            }
            frames.Add(new Frame(ev.StartUs, GuitarPlayModeType.Chord, page, slot, mapped, strum, ev.Direction));
        }
        // The beat arranger intentionally uses only recognized harmony. Preserve an explicit
        // fret assist for source events the 24 chord wheel cannot represent.
        foreach (var ev in analyzedChords.Where(e => e.Chord == ChordId.Unknown &&
                     !events.Any(prepared => prepared.Chord == ChordId.Unknown && prepared.StartUs == e.StartUs)))
        {
            var source = ev.AttackNotes.Count > 0 ? ev.AttackNotes : ev.SourceNotes;
            if (TryBuildFretFrame(ev.StartUs, source, out var fallback)) frames.Add(fallback);
        }
        frames = frames.OrderBy(f => f.TimeUs).ToList();
        return Emit(frames, preset, options);
    }

    private static bool TryBuildFretFrame(long time, IReadOnlyList<RawMidiNote> notes, out Frame frame)
    {
        frame = null!;
        if (notes.Count == 0) return false;
        var tuning = GuitarTuning.Standard;
        int pitch = notes.Min(n => n.NoteNumber);
        while (pitch < 41) pitch += 12;
        while (pitch > 56) pitch -= 12;
        int fret6 = pitch - tuning.GetOpenMidiNote(6), fret5 = pitch - tuning.GetOpenMidiNote(5);
        byte stringNo, fret;
        if (fret6 is >= 1 and <= 12) { stringNo = 6; fret = (byte)fret6; }
        else if (fret5 is >= 1 and <= 12) { stringNo = 5; fret = (byte)fret5; }
        else return false;
        var (page, slot) = FretConverter.GetPageAndSlot(fret);
        var notesToPlay = new List<(byte StringNo, byte Pitch)> { (stringNo, (byte)pitch) };
        if (notes.Count >= 2)
        {
            byte companion = (byte)Math.Max(2, stringNo - 2);
            byte companionPitch = (byte)(tuning.GetOpenMidiNote(companion) + fret);
            notesToPlay.Add((companion, companionPitch));
        }
        frame = new Frame(time, GuitarPlayModeType.Fret, page, slot, notesToPlay, false, StrumDirectionHint.Unknown);
        return true;
    }

    private static List<GuitarAction> Emit(IReadOnlyList<Frame> frames, WheelPreset preset, GuitarCompilerOptions options)
    {
        var actions = new List<GuitarAction>();
        var lastDown = new Dictionary<byte, long>();
        var lastUp = new Dictionary<byte, long>();
        var mode = GuitarPlayModeType.Chord;
        byte page = options.InitialPage, slot = 0;
        long lastControl = long.MinValue / 2;
        foreach (var f in frames)
        {
            bool switchMode = f.Mode != mode;
            byte startPage = switchMode ? (byte)1 : page;
            int pageCount = f.Mode == GuitarPlayModeType.Fret ? 5 : 4;
            var (pageSteps, pageDirection) = CircularPageNavigator.GetShortestPath(startPage, f.Page, pageCount);
            bool select = switchMode || pageSteps != 0 || slot != f.Slot;
            long attack = f.TimeUs;
            if (select)
            {
                long step = Math.Max(25_000, options.PageKeyHoldUs) + Math.Max(15_000, options.PageInterTapGapUs);
                int steps = pageSteps + (switchMode ? 1 : 0);
                long wheelSettleUs = Math.Max(55_000, options.WheelMouseSettleUs);
                long start = Math.Max(attack - wheelSettleUs - steps * step, lastControl + 6_000);
                long cursor = start;
                if (switchMode)
                {
                    byte target = f.Mode == GuitarPlayModeType.Fret ? (byte)1 : (byte)2;
                    actions.Add(new GuitarAction(cursor, GuitarActionType.ModeSwitchDown, target, GameScanCodes.KeyZ,
                        deadlineUs: cursor + 500_000, resource: GameActionResource.Nav));
                    actions.Add(new GuitarAction(cursor + Math.Max(25_000, options.PageKeyHoldUs), GuitarActionType.ModeSwitchUp, target, GameScanCodes.KeyZ,
                        deadlineUs: cursor + step + 500_000, resource: GameActionResource.Nav));
                    cursor += step; mode = f.Mode; page = 1; slot = 0;
                }
                if (pageSteps > 0)
                {
                    ushort scan = pageDirection > 0 ? preset.PageNextScanCode : preset.PagePrevScanCode;
                    for (int n = 0; n < pageSteps; n++)
                    {
                        byte p = CircularPageNavigator.StepPage(page, pageDirection, pageCount);
                        actions.Add(new GuitarAction(cursor, GuitarActionType.PageNav, p, scan, deadlineUs: cursor + 500_000, resource: GameActionResource.Nav));
                        actions.Add(new GuitarAction(cursor + Math.Max(25_000, options.PageKeyHoldUs), GuitarActionType.PageNavUp, p, scan, deadlineUs: cursor + step + 500_000, resource: GameActionResource.Nav));
                        cursor += step; page = p;
                    }
                }
                actions.Add(new GuitarAction(cursor, GuitarActionType.WheelSelect, f.Slot, deadlineUs: cursor + 500_000,
                    requiredPage: f.Page, requiredSlot: f.Slot, resource: GameActionResource.Wheel));
                slot = f.Slot; lastControl = cursor + wheelSettleUs;
            }
            attack = Math.Max(attack, lastControl);
            if (f.Strum)
            {
                var button = f.Direction == StrumDirectionHint.HighToLow ? preset.UpStrumButton : preset.DownStrumButton;
                actions.Add(new GuitarAction(attack, GuitarActionType.StrumClick, 0, mouseButton: button, deadlineUs: attack + 60_000,
                    requiredPage: f.Page, requiredSlot: f.Slot, resource: GameActionResource.Mouse));
                continue;
            }
            foreach (var (s, pitch) in f.Notes)
            {
                ushort scan = preset.StringScanCodes is { Length: >= 6 } ? preset.StringScanCodes[s - 1] : GameScanCodes.ForGuitarString(s);
                long down = Math.Max(attack, Math.Max(lastDown.GetValueOrDefault(s, long.MinValue / 2) + options.MinHoldUs + options.ReleaseGapUs,
                    lastUp.GetValueOrDefault(s, long.MinValue / 2) + options.ReleaseGapUs));
                long up = down + Math.Max(25_000, options.StringTapDurationUs);
                actions.Add(new GuitarAction(down, GuitarActionType.StringDown, s, scan, deadlineUs: down + 60_000,
                    requiredPage: f.Page, requiredSlot: f.Slot, resource: GameActionResource.StringKeys, noteNumber: pitch));
                actions.Add(new GuitarAction(up, GuitarActionType.StringUp, s, scan, deadlineUs: up + 500_000,
                    requiredPage: f.Page, requiredSlot: f.Slot, resource: GameActionResource.StringKeys));
                lastDown[s] = down; lastUp[s] = up;
            }
        }
        actions.Sort((a,b) => a.TimeUs != b.TimeUs ? a.TimeUs.CompareTo(b.TimeUs) : a.Type.CompareTo(b.Type));
        return actions;
    }
}
