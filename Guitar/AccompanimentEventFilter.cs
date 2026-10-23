using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Guitar;

/// <summary>
/// Follows MIDI onset timing and uses the analyzed harmony at each onset. It does not
/// synthesize a groove: MIDI attacks remain the source of strum timing.
/// </summary>
internal static class AccompanimentEventFilter
{
    public static List<ChordEvent> Prepare(
        IReadOnlyList<RawMidiNote> sourceNotes,
        IReadOnlyList<ChordEvent> rawChordEvents,
        int semitoneShift,
        double speedMultiplier = 1.0)
    {
        if (sourceNotes.Count == 0 || rawChordEvents.Count == 0)
            return rawChordEvents.ToList();

        double speed = Math.Max(0.1, speedMultiplier);
        var notes = sourceNotes.Where(note => note.Channel != 9)
            .Select(note => new RawMidiNote
            {
                StartUs = ScaleTime(note.StartUs, speed),
                DurationUs = Math.Max(20_000, ScaleTime(note.DurationUs, speed)),
                NoteNumber = (byte)Math.Clamp(note.NoteNumber + semitoneShift, 0, 127),
                Velocity = note.Velocity,
                Channel = note.Channel,
                TrackId = note.TrackId,
                Program = note.Program,
                BarIndex = note.BarIndex,
                BeatInBar = note.BeatInBar,
                BeatsPerBar = note.BeatsPerBar,
                BeatUnitDenominator = note.BeatUnitDenominator,
                HasMeterPosition = note.HasMeterPosition,
                IsBrushChord = note.IsBrushChord,
                IsBrushUp = note.IsBrushUp,
                SourceFormat = note.SourceFormat,
                OrigString = note.OrigString
            }).OrderBy(note => note.StartUs).ToList();

        var chords = rawChordEvents.Where(ev => ev.Chord != ChordId.Unknown)
            .OrderBy(ev => ev.StartUs)
            .Select(ev => new ChordEvent
            {
                StartUs = ScaleTime(ev.StartUs, speed),
                EndUs = ScaleTime(ev.EndUs, speed),
                Chord = ev.Chord,
                Page = ev.Page,
                Slot = ev.Slot,
                Confidence = ev.Confidence,
                SourceNotes = ev.SourceNotes,
                AttackNotes = ev.AttackNotes,
                Articulation = ev.Articulation,
                Direction = ev.Direction,
                DiagnosticInfo = ev.DiagnosticInfo
            }).ToList();
        if (notes.Count == 0 || chords.Count == 0) return rawChordEvents.ToList();

        long beatUs = EstimateBeatDurationUs(notes);
        var onsetGroups = GroupAttacks(notes, 30_000);
        var accompanimentGroups = onsetGroups.Where(group =>
                group.Count >= 2 || group.Any(note => note.IsBrushChord || note.NoteNumber < 57))
            .ToList();
        // Sparse or single-track files may encode a strum as one note per onset. In that
        // case retain the slower attacks while suppressing dense melodic runs.
        if (accompanimentGroups.Count < 2)
            accompanimentGroups = onsetGroups.Where((group, index) =>
            {
                long previous = index > 0 ? onsetGroups[index - 1][0].StartUs : long.MinValue / 2;
                return group.Any(note => note.IsBrushChord) ||
                    group[0].StartUs - previous >= beatUs * 0.45;
            }).ToList();

        var result = new List<ChordEvent>(accompanimentGroups.Count);
        long previousAttackUs = long.MinValue / 2;
        bool previousDown = false;
        foreach (var group in accompanimentGroups)
        {
            long timeUs = group[0].StartUs;
            var chord = chords.LastOrDefault(ev => ev.StartUs <= timeUs) ?? chords[0];
            if (result.Count > 0 && chord.Chord != result[^1].Chord &&
                timeUs - previousAttackUs < 60_000)
                continue;

            bool brushUp = group.Any(note => note.IsBrushUp);
            bool offbeat = group.Any(note => note.HasMeterPosition &&
                note.BeatInBar - Math.Floor(note.BeatInBar) is >= 0.35 and <= 0.75);
            StrumDirectionHint direction = brushUp
                ? StrumDirectionHint.HighToLow
                : chord.Direction is StrumDirectionHint.LowToHigh or StrumDirectionHint.HighToLow
                    ? chord.Direction
                    : offbeat || (timeUs - previousAttackUs < beatUs * 0.8 && previousDown)
                        ? StrumDirectionHint.HighToLow
                        : StrumDirectionHint.LowToHigh;

            var notesForAudition = BuildStrumNotes(chord.Chord, direction, timeUs);
            result.Add(new ChordEvent
            {
                StartUs = timeUs,
                EndUs = Math.Max(timeUs + 80_000, group.Max(note => note.StartUs + note.DurationUs)),
                Chord = chord.Chord,
                Page = chord.Page,
                Slot = chord.Slot,
                Confidence = chord.Confidence,
                SourceNotes = group,
                AttackNotes = notesForAudition,
                Articulation = ChordArticulation.Strum,
                Direction = direction
            });
            previousAttackUs = timeUs;
            previousDown = direction == StrumDirectionHint.LowToHigh;
        }

        if (result.Count == 0) return chords;

        // Fill only genuine long-note gaps. The source must still be sustaining and the
        // next source attack must leave room, so this does not turn silent gaps into rhythm.
        var filled = new List<ChordEvent>(result.Count);
        for (int i = 0; i < result.Count; i++)
        {
            var current = result[i];
            filled.Add(current);
            if (i + 1 >= result.Count) continue;
            var next = result[i + 1];
            long gapUs = next.StartUs - current.StartUs;
            long sustainEndUs = current.SourceNotes.Count == 0
                ? current.EndUs
                : current.SourceNotes.Max(note => note.StartUs + note.DurationUs);
            if (gapUs < beatUs * 3 / 2 || sustainEndUs <= current.StartUs + beatUs) continue;

            long fillTimeUs = current.StartUs + beatUs;
            bool down = true;
            while (fillTimeUs < Math.Min(sustainEndUs, next.StartUs - 80_000))
            {
                var direction = down ? StrumDirectionHint.LowToHigh : StrumDirectionHint.HighToLow;
                var fillNotes = BuildStrumNotes(current.Chord, direction, fillTimeUs);
                filled.Add(new ChordEvent
                {
                    StartUs = fillTimeUs,
                    EndUs = fillTimeUs + 80_000,
                    Chord = current.Chord,
                    Page = current.Page,
                    Slot = current.Slot,
                    Confidence = current.Confidence,
                    SourceNotes = fillNotes,
                    AttackNotes = fillNotes,
                    Articulation = ChordArticulation.Strum,
                    Direction = direction
                });
                down = !down;
                fillTimeUs += Math.Max(80_000, beatUs / 2);
            }
        }
        return filled.OrderBy(ev => ev.StartUs).ToList();
    }

    private static long ScaleTime(long timeUs, double speed) =>
        (long)Math.Clamp(Math.Round(timeUs / speed), 0, long.MaxValue);

    private static List<List<RawMidiNote>> GroupAttacks(IReadOnlyList<RawMidiNote> notes, long toleranceUs)
    {
        var groups = new List<List<RawMidiNote>>();
        foreach (var note in notes)
        {
            if (groups.Count == 0 || note.StartUs - groups[^1][0].StartUs > toleranceUs)
                groups.Add(new List<RawMidiNote>());
            groups[^1].Add(note);
        }
        return groups;
    }

    private static long EstimateBeatDurationUs(IReadOnlyList<RawMidiNote> notes)
    {
        var onsets = notes.Select(note => note.StartUs).Distinct().Order().ToList();
        var intervals = new List<long>();
        for (int i = 1; i < onsets.Count; i++)
        {
            long interval = onsets[i] - onsets[i - 1];
            while (interval > 0 && interval < 320_000) interval *= 2;
            while (interval > 780_000) interval /= 2;
            if (interval is >= 320_000 and <= 780_000) intervals.Add(interval);
        }
        if (intervals.Count == 0) return 500_000;
        intervals.Sort();
        return intervals[intervals.Count / 2];
    }

    private static List<RawMidiNote> BuildStrumNotes(ChordId chord, StrumDirectionHint direction, long startUs)
    {
        if (!GameChordGroundTruth.Table.TryGetValue(chord, out var truth)) return new();
        IEnumerable<byte> strings = direction == StrumDirectionHint.HighToLow
            ? truth.SafeCleanStrings.Order()
            : truth.SafeCleanStrings.OrderDescending();
        return strings.Take(4).Select((stringNo, index) => new RawMidiNote
        {
            StartUs = startUs + index * 8_000,
            DurationUs = 180_000,
            NoteNumber = truth.ActualMidi1To6[stringNo - 1],
            Velocity = (byte)(direction == StrumDirectionHint.LowToHigh ? 96 : 82),
            OrigString = stringNo
        }).ToList();
    }
}
