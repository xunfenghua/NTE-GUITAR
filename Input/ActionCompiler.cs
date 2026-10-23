using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Input;

public sealed class CompilerOptions
{
    public int TapDurationUs { get; set; } = 30_000;
    public int MinRetriggerIntervalUs { get; set; } = 8_000;
    public int MinHoldUs { get; set; } = 20_000;
    public int ReleaseGapUs { get; set; } = 2_000;
    public int LatencyOffsetUs { get; set; } = 0;
    public bool EnableDynamicSustain { get; set; } = false;
    // Retained for existing callers; sustain is now based on per-key timing.
    public int CloseNotesThresholdUs { get; set; } = 40_000;
    public int MaxSustainUs { get; set; } = 1_500_000;
    public int SamePhysicalKeySustainGapUs { get; set; } = 40_000;
    public int MaxConcurrentHeldKeys { get; set; } = 4;
}

public static class ActionCompiler
{
    private sealed class PlannedHold
    {
        public long StartUs { get; init; }
        public long EndUs { get; set; }
        public ushort ScanCode { get; init; }
        public bool Extended { get; init; }
        public byte NoteIndex { get; init; }
    }

    public static List<InputBatch> Compile(
        IReadOnlyList<GameNoteEvent> gameNotes,
        KeyPreset preset,
        CompilerOptions? options = null)
    {
        options ??= new CompilerOptions();
        long minHoldUs = Math.Max(5_000, options.MinHoldUs);
        long tapDurationUs = Math.Max(minHoldUs, options.TapDurationUs);
        long releaseGapUs = Math.Max(1_000, options.ReleaseGapUs);
        int maxHeld = Math.Clamp(options.MaxConcurrentHeldKeys, 1, 4);

        var mappingByIndex = new Dictionary<byte, KeyMapping>();
        foreach (KeyMapping mapping in preset.Keys) mappingByIndex[mapping.NoteIndex] = mapping;
        var keyTriggers = new Dictionary<(ushort ScanCode, bool Extended), List<(long TimeUs, long DurationUs, byte NoteIndex)>>();
        foreach (GameNoteEvent note in gameNotes)
        {
            if (!mappingByIndex.TryGetValue(note.NoteIndex, out KeyMapping mapping)) continue;
            long time = Math.Max(0, note.TimeUs + options.LatencyOffsetUs);
            var key = (mapping.ScanCode, mapping.Extended);
            if (!keyTriggers.TryGetValue(key, out var triggers)) keyTriggers[key] = triggers = new();
            triggers.Add((time, note.DurationUs, note.NoteIndex));
        }

        var planned = new List<PlannedHold>();
        foreach (var entry in keyTriggers)
        {
            (ushort scanCode, bool extended) = entry.Key;
            var ordered = entry.Value.OrderBy(item => item.TimeUs).ThenByDescending(item => item.NoteIndex).ToList();
            var unique = new List<(long TimeUs, long DurationUs, byte NoteIndex)>();
            foreach (var item in ordered)
                if (unique.Count == 0 || item.TimeUs != unique[^1].TimeUs) unique.Add(item);

            long previousUpTime = long.MinValue;
            for (int index = 0; index < unique.Count; index++)
            {
                var item = unique[index];
                long downTime = Math.Max(item.TimeUs, previousUpTime == long.MinValue ? item.TimeUs : previousUpTime + releaseGapUs);
                bool isLast = index + 1 == unique.Count;
                long nextDown = isLast ? long.MaxValue : unique[index + 1].TimeUs;
                long hold = tapDurationUs;
                long maxSustain = Math.Max(tapDurationUs, Math.Min(1_500_000, options.MaxSustainUs));
                if (options.EnableDynamicSustain && item.DurationUs > tapDurationUs)
                {
                    long sameKeyGap = nextDown - downTime;
                    if (isLast || sameKeyGap >= Math.Max(40_000, options.SamePhysicalKeySustainGapUs))
                        hold = Math.Clamp(item.DurationUs, tapDurationUs, maxSustain);
                }

                if (!isLast)
                {
                    long latestRelease = nextDown - releaseGapUs;
                    if (latestRelease >= downTime + minHoldUs)
                        hold = Math.Min(hold, latestRelease - downTime);
                    else
                        hold = tapDurationUs;
                }
                long upTime = downTime + Math.Max(minHoldUs, hold);
                planned.Add(new PlannedHold
                {
                    StartUs = downTime,
                    EndUs = upTime,
                    ScanCode = scanCode,
                    Extended = extended,
                    NoteIndex = item.NoteIndex
                });
                previousUpTime = upTime;
            }
        }

        // Keep at most four physical keys held. For simultaneous attacks, retain
        // the highest notes first. Only truncate a held key after its minimum hold.
        var accepted = new List<PlannedHold>();
        var active = new List<PlannedHold>();
        foreach (PlannedHold hold in planned.OrderBy(item => item.StartUs).ThenByDescending(item => item.NoteIndex))
        {
            active.RemoveAll(item => item.EndUs <= hold.StartUs);
            while (active.Count >= maxHeld)
            {
                PlannedHold? releasable = active
                    .Where(item => item.StartUs + minHoldUs <= hold.StartUs)
                    .OrderBy(item => item.StartUs)
                    .FirstOrDefault();
                if (releasable == null) break;
                releasable.EndUs = hold.StartUs;
                active.Remove(releasable);
            }
            if (active.Count >= maxHeld) continue;
            accepted.Add(hold);
            active.Add(hold);
        }

        var actions = new List<KeyAction>(accepted.Count * 2);
        foreach (PlannedHold hold in accepted)
        {
            actions.Add(new KeyAction(hold.StartUs, hold.ScanCode, isDown: true, extended: hold.Extended));
            actions.Add(new KeyAction(hold.EndUs, hold.ScanCode, isDown: false, extended: hold.Extended));
        }
        actions.Sort((left, right) =>
        {
            int time = left.TimeUs.CompareTo(right.TimeUs);
            return time != 0 ? time : left.IsDown.CompareTo(right.IsDown);
        });

        var batches = new List<InputBatch>();
        InputBatch? batch = null;
        foreach (KeyAction action in actions)
        {
            if (batch == null || batch.TimeUs != action.TimeUs)
            {
                batch = new InputBatch { TimeUs = action.TimeUs };
                batches.Add(batch);
            }
            batch.Actions.Add(action);
        }
        return batches;
    }
}
