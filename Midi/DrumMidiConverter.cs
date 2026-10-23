using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Midi;

public enum DrumType
{
    Bass,
    Snare,
    Light,
    Heavy
}

public sealed record DrumConversionOptions
{
    public int MaxSimultaneousKeys { get; init; } = 2;
    public long MinKeyCooldownUs { get; init; } = 55_000;
    public long HeavyCrashCooldownUs { get; init; } = 2_000_000;
    public long ClusterWindowUs { get; init; } = 20_000;
    public long TapHoldUs { get; init; } = 25_000;
}

public static class DrumMidiConverter
{
    private sealed record DrumNote(long TimeUs, byte Pitch, byte Velocity, byte Channel, long Bar,
        double Beat, int BeatsPerBar, int Denominator);

    private sealed class OnsetCluster
    {
        public long TimeUs { get; init; }
        public long Bar { get; init; }
        public double Beat { get; init; }
        public int BeatsPerBar { get; init; }
        public int Denominator { get; init; }
        public List<DrumNote> Drums { get; } = new();
        public List<DrumNote> Melody { get; } = new();
    }

    private static readonly IReadOnlyDictionary<byte, DrumType> GmDrumMap = new Dictionary<byte, DrumType>
    {
        [35] = DrumType.Bass, [36] = DrumType.Bass, [41] = DrumType.Bass, [43] = DrumType.Bass,
        [45] = DrumType.Bass, [47] = DrumType.Bass, [60] = DrumType.Bass, [61] = DrumType.Bass,
        [64] = DrumType.Bass, [65] = DrumType.Bass, [66] = DrumType.Bass,
        [37] = DrumType.Snare, [38] = DrumType.Snare, [39] = DrumType.Snare, [40] = DrumType.Snare,
        [48] = DrumType.Snare, [50] = DrumType.Snare, [54] = DrumType.Snare, [56] = DrumType.Snare,
        [62] = DrumType.Snare, [63] = DrumType.Snare, [75] = DrumType.Snare, [76] = DrumType.Snare,
        [42] = DrumType.Light, [44] = DrumType.Light, [51] = DrumType.Light, [53] = DrumType.Light,
        [59] = DrumType.Light, [69] = DrumType.Light, [70] = DrumType.Light, [80] = DrumType.Light,
        [81] = DrumType.Light,
        [46] = DrumType.Heavy, [49] = DrumType.Heavy, [52] = DrumType.Heavy, [55] = DrumType.Heavy,
        [57] = DrumType.Heavy, [58] = DrumType.Heavy
    };

    private static readonly DrumType[] Priority = [DrumType.Heavy, DrumType.Snare, DrumType.Bass, DrumType.Light];

    public static List<InputBatch> Convert(
        IReadOnlyList<RawMidiNote> notes,
        IReadOnlyList<ushort> scanCodes,
        DrumConversionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (scanCodes is null || scanCodes.Count != 4)
            throw new ArgumentException("鼓点模式需要按 D、F、J、K 顺序提供四个扫描码。", nameof(scanCodes));
        options ??= new DrumConversionOptions();

        var events = notes
            .Where(n => n.Velocity > 0 && n.StartUs >= 0)
            .Select(n => ToDrumNote(n))
            .OrderBy(n => n.TimeUs)
            .ThenBy(n => n.Pitch)
            .ToArray();
        if (events.Length == 0) return new List<InputBatch>();

        var melodic = events.Where(n => n.Channel != 9).ToArray();
        double pitchP30 = GetPercentile(melodic.Select(n => (double)n.Pitch), 0.30, 55);
        double pitchP70 = GetPercentile(melodic.Select(n => (double)n.Pitch), 0.70, 72);
        double velocityP85 = GetPercentile(melodic.Select(n => (double)n.Velocity), 0.85, 100);
        bool flatVelocity = CalculateStdDev(melodic.Select(n => (double)n.Velocity)) < 8.0;

        var activeDrumBars = events.Where(n => n.Channel == 9).Select(n => n.Bar).ToHashSet();
        long firstBar = events.Min(n => n.Bar);
        var clusters = Cluster(events, Math.Max(0, options.ClusterWindowUs));
        var lastTriggerUs = Enumerable.Repeat(long.MinValue, 4).ToArray();
        long lastAutoHeavyCrashUs = long.MinValue;
        long previousClusterUs = 0;
        int previousMelodyPitch = 60;
        DrumType? previousAutoDrum = null;
        int repeatedAutoDrumCount = 0;
        var actionEvents = new List<KeyAction>();

        foreach (OnsetCluster cluster in clusters)
        {
            var candidates = new HashSet<DrumType>();
            bool useDrumTrack = activeDrumBars.Contains(cluster.Bar);

            if (useDrumTrack)
            {
                foreach (DrumNote note in cluster.Drums)
                    candidates.Add(MapDrumPitch(note.Pitch));
            }
            else if (cluster.Melody.Count > 0)
            {
                int minPitch = cluster.Melody.Min(n => n.Pitch);
                int maxPitch = cluster.Melody.Max(n => n.Pitch);
                int maxVelocity = cluster.Melody.Max(n => n.Velocity);
                int noteCount = cluster.Melody.Count;
                double beat = NormalizeBeat(cluster.Beat, cluster.BeatsPerBar);
                int beatIndex = (int)Math.Floor(beat);
                double beatPhase = beat - beatIndex;
                // MIDI note onsets are often a few milliseconds early/late. Quantize the
                // quarter-note beat to a sixteenth grid before assigning a drum role.
                int sixteenth = (int)Math.Round(beatPhase * 4, MidpointRounding.AwayFromZero);
                if (sixteenth == 4) { beatIndex = (beatIndex + 1) % cluster.BeatsPerBar; sixteenth = 0; }
                long phraseBar = cluster.Bar - firstBar;
                bool onBeat = sixteenth == 0;
                bool eighthOffBeat = sixteenth == 2;
                bool isFillZone = phraseBar % 4 == 3 &&
                                  beat >= cluster.BeatsPerBar - 1.0 - 0.13;
                bool isFirstBeat = beatIndex == 0 && onBeat;
                bool isPrimaryBeat = onBeat && (beatIndex % 2 == 0);
                bool isBackBeat = onBeat && (cluster.BeatsPerBar == 4
                    ? beatIndex is 1 or 3
                    : beatIndex == cluster.BeatsPerBar / 2);
                bool afterLongRest = previousClusterUs > 0 && cluster.TimeUs - previousClusterUs > 1_200_000;

                bool accent = flatVelocity
                    ? noteCount >= 4
                    : maxVelocity >= velocityP85 && noteCount >= 2;
                bool heavyCooldownReady = lastAutoHeavyCrashUs == long.MinValue ||
                                          cluster.TimeUs - lastAutoHeavyCrashUs >= Math.Max(0, options.HeavyCrashCooldownUs);
                if (((isFirstBeat && (accent || phraseBar % 4 == 0)) || afterLongRest) && heavyCooldownReady)
                {
                    candidates.Add(DrumType.Heavy);
                    lastAutoHeavyCrashUs = cluster.TimeUs;
                }

                DrumType primary;
                if (isFillZone)
                {
                    // A four-step fill in the final beat of every fourth bar.
                    primary = sixteenth switch
                    {
                        0 => DrumType.Bass,
                        1 => DrumType.Snare,
                        2 => DrumType.Light,
                        _ => DrumType.Heavy
                    };
                }
                else if (onBeat)
                {
                    // Keep bass accompaniment from claiming every onset: only the
                    // structural downbeats get bass; backbeats are assigned to snare.
                    primary = isBackBeat ? DrumType.Snare : isPrimaryBeat ? DrumType.Bass : DrumType.Snare;
                }
                else if (eighthOffBeat)
                {
                    primary = (minPitch <= pitchP30 - 4 && beatIndex % 2 == 0 &&
                               previousAutoDrum != DrumType.Bass)
                        ? DrumType.Bass
                        : (noteCount >= 4 && maxVelocity >= velocityP85 ? DrumType.Snare : DrumType.Light);
                }
                else
                {
                    // Sixteenth-note runs follow melodic contour, alternating on repeated
                    // pitches so dense piano passages do not become a stream of one drum.
                    int pitchDelta = maxPitch - previousMelodyPitch;
                    primary = pitchDelta > 1 ? DrumType.Light
                        : pitchDelta < -1 ? DrumType.Snare
                        : previousAutoDrum == DrumType.Light ? DrumType.Snare : DrumType.Light;
                }

                // Protect the groove when a source repeats the same onset/low note on every
                // beat. Preserve the designated backbeat and fill; otherwise vary after 2.
                if (!isBackBeat && !isFillZone && primary == previousAutoDrum)
                    repeatedAutoDrumCount++;
                else
                    repeatedAutoDrumCount = 1;
                if (!isBackBeat && !isFillZone && repeatedAutoDrumCount > 2)
                {
                    primary = primary == DrumType.Bass ? DrumType.Light : DrumType.Snare;
                    repeatedAutoDrumCount = 1;
                }

                candidates.Add(primary);
                if ((isFirstBeat || isPrimaryBeat) && maxPitch >= pitchP70 && options.MaxSimultaneousKeys > 1)
                    candidates.Add(DrumType.Light);

                previousAutoDrum = primary;
                previousMelodyPitch = maxPitch;
            }

            previousClusterUs = cluster.TimeUs;
            if (candidates.Count == 0) continue;

            var valid = candidates
                .Where(d => lastTriggerUs[(int)d] == long.MinValue ||
                            cluster.TimeUs - lastTriggerUs[(int)d] >= Math.Max(0, options.MinKeyCooldownUs))
                .OrderBy(d => Array.IndexOf(Priority, d))
                .Take(Math.Clamp(options.MaxSimultaneousKeys, 1, 4))
                .ToArray();

            foreach (DrumType drum in valid)
            {
                lastTriggerUs[(int)drum] = cluster.TimeUs;
                ushort scanCode = scanCodes[(int)drum];
                actionEvents.Add(new KeyAction(cluster.TimeUs, scanCode, true));
                actionEvents.Add(new KeyAction(cluster.TimeUs + Math.Max(5_000, options.TapHoldUs), scanCode, false));
            }
        }

        actionEvents.Sort((left, right) =>
        {
            int time = left.TimeUs.CompareTo(right.TimeUs);
            if (time != 0) return time;
            int releaseFirst = left.IsDown.CompareTo(right.IsDown);
            return releaseFirst != 0 ? releaseFirst : left.ScanCode.CompareTo(right.ScanCode);
        });

        var batches = new List<InputBatch>();
        foreach (KeyAction action in actionEvents)
        {
            if (batches.Count == 0 || batches[^1].TimeUs != action.TimeUs)
                batches.Add(new InputBatch { TimeUs = action.TimeUs });
            batches[^1].Actions.Add(action);
        }
        return batches;
    }

    private static DrumNote ToDrumNote(RawMidiNote note)
    {
        if (note.HasMeterPosition)
            return new DrumNote(note.StartUs, note.NoteNumber, note.Velocity, note.Channel, note.BarIndex,
                note.BeatInBar, Math.Max(1, (int)note.BeatsPerBar), Math.Max(1, (int)note.BeatUnitDenominator));

        // Old imported songs without an archived source.mid use a conservative 120 BPM 4/4 estimate.
        double absoluteBeat = note.StartUs / 500_000.0;
        long bar = (long)Math.Floor(absoluteBeat / 4.0);
        return new DrumNote(note.StartUs, note.NoteNumber, note.Velocity, note.Channel, bar,
            absoluteBeat % 4.0, 4, 4);
    }

    private static List<OnsetCluster> Cluster(IReadOnlyList<DrumNote> events, long windowUs)
    {
        var clusters = new List<OnsetCluster>();
        foreach (DrumNote note in events)
        {
            if (clusters.Count == 0 || note.TimeUs - clusters[^1].TimeUs > windowUs)
            {
                clusters.Add(new OnsetCluster
                {
                    TimeUs = note.TimeUs,
                    Bar = note.Bar,
                    Beat = note.Beat,
                    BeatsPerBar = note.BeatsPerBar,
                    Denominator = note.Denominator,
                });
            }

            OnsetCluster cluster = clusters[^1];
            if (note.Channel == 9) cluster.Drums.Add(note);
            else cluster.Melody.Add(note);
        }
        return clusters;
    }

    private static DrumType MapDrumPitch(byte pitch)
    {
        if (GmDrumMap.TryGetValue(pitch, out DrumType mapped)) return mapped;
        return pitch < 45 ? DrumType.Bass : pitch < 55 ? DrumType.Snare : DrumType.Light;
    }

    private static double NormalizeBeat(double beat, int beatsPerBar)
    {
        if (!double.IsFinite(beat)) return 0;
        double length = Math.Max(1, beatsPerBar);
        beat %= length;
        return beat < 0 ? beat + length : beat;
    }

    private static double GetPercentile(IEnumerable<double> values, double percentile, double fallback)
    {
        double[] sorted = values.OrderBy(value => value).ToArray();
        if (sorted.Length == 0) return fallback;
        int index = (int)Math.Clamp(Math.Floor(sorted.Length * percentile), 0, sorted.Length - 1);
        return sorted[index];
    }

    private static double CalculateStdDev(IEnumerable<double> values)
    {
        double[] array = values.ToArray();
        if (array.Length <= 1) return 0;
        double mean = array.Average();
        return Math.Sqrt(array.Sum(value => (value - mean) * (value - mean)) / array.Length);
    }
}
