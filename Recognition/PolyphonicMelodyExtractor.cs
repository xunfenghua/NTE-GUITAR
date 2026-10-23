using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Recognition;

/// <summary>
/// Extracts a single melodic voice from a polyphonic MIDI track by grouping
/// rolled chords, masking accompaniment under sustained melody notes, and
/// choosing a continuous path with dynamic programming.
/// </summary>
public static class PolyphonicMelodyExtractor
{
    public sealed class ExtractorOptions
    {
        public long ChordClusterWindowUs { get; set; } = 35_000;
        public long ArpeggioRollStepUs { get; set; } = 45_000;
        public long MinNoteDurationUs { get; set; } = 35_000;
        public int MaskingIntervalSemitones { get; set; } = 5;
        public long MaxEnvelopeMaskDurationUs { get; set; } = 1_200_000;
    }

    private sealed class NoteCluster
    {
        public long AnchorTimeUs { get; init; }
        public List<RawMidiNote> Notes { get; } = new();
    }

    private readonly record struct ActiveKey(RawMidiNote? Note);

    private sealed class PathState
    {
        public RawMidiNote? ActiveNote { get; init; }
        public double Score { get; init; }
        public int ParentIndex { get; init; }
        public RawMidiNote? SelectedNote { get; init; }
    }

    public static List<RawMidiNote> ExtractSingleTrackMelody(
        IReadOnlyList<RawMidiNote> rawNotes,
        ExtractorOptions? options = null)
    {
        options ??= new ExtractorOptions();
        var notes = rawNotes
            .Where(note => note.Channel != 9 && note.Velocity > 8 && note.DurationUs >= options.MinNoteDurationUs)
            .OrderBy(note => note.StartUs)
            .ThenByDescending(note => note.NoteNumber)
            .ToList();
        if (notes.Count == 0) return new List<RawMidiNote>();

        double splitPitch = EstimateMelodyRegisterSplitPitch(notes);
        var clusters = BuildChordAndArpeggioClusters(notes, options);
        if (clusters.Count == 0) return new List<RawMidiNote>();
        return SolveMelodyViterbiWithEnvelopeMasking(clusters, splitPitch, options);
    }

    private static double EstimateMelodyRegisterSplitPitch(IReadOnlyList<RawMidiNote> notes)
    {
        int minPitch = notes.Min(note => (int)note.NoteNumber);
        int maxPitch = notes.Max(note => (int)note.NoteNumber);
        if (maxPitch - minPitch <= 12) return minPitch - 1;

        double lowCenter = minPitch + (maxPitch - minPitch) * 0.28;
        double highCenter = minPitch + (maxPitch - minPitch) * 0.75;
        for (int iteration = 0; iteration < 8; iteration++)
        {
            double lowSum = 0, lowWeight = 0, highSum = 0, highWeight = 0;
            foreach (var note in notes)
            {
                double weight = Math.Clamp(note.DurationUs / 100_000d, 0.5, 8.0) * (note.Velocity / 100d);
                if (Math.Abs(note.NoteNumber - lowCenter) < Math.Abs(note.NoteNumber - highCenter))
                {
                    lowSum += note.NoteNumber * weight;
                    lowWeight += weight;
                }
                else
                {
                    highSum += note.NoteNumber * weight;
                    highWeight += weight;
                }
            }
            if (lowWeight > 0) lowCenter = lowSum / lowWeight;
            if (highWeight > 0) highCenter = highSum / highWeight;
        }
        return lowCenter * 0.45 + highCenter * 0.55;
    }

    private static List<NoteCluster> BuildChordAndArpeggioClusters(
        IReadOnlyList<RawMidiNote> sortedNotes,
        ExtractorOptions options)
    {
        var clusters = new List<NoteCluster>();
        NoteCluster? current = null;
        foreach (RawMidiNote note in sortedNotes)
        {
            if (current == null)
            {
                current = new NoteCluster { AnchorTimeUs = note.StartUs };
                current.Notes.Add(note);
                continue;
            }

            RawMidiNote previous = current.Notes[^1];
            long fromAnchorUs = note.StartUs - current.AnchorTimeUs;
            long fromPreviousUs = note.StartUs - previous.StartUs;
            bool simultaneous = fromAnchorUs <= options.ChordClusterWindowUs;
            bool upwardRoll = fromPreviousUs <= options.ArpeggioRollStepUs
                && fromAnchorUs <= 130_000
                && note.NoteNumber > previous.NoteNumber
                && previous.StartUs + Math.Max(0, previous.DurationUs) > note.StartUs + 50_000;
            if (simultaneous || upwardRoll)
            {
                current.Notes.Add(note);
            }
            else
            {
                clusters.Add(current);
                current = new NoteCluster { AnchorTimeUs = note.StartUs };
                current.Notes.Add(note);
            }
        }
        if (current != null) clusters.Add(current);
        return clusters;
    }

    private static List<RawMidiNote> SolveMelodyViterbiWithEnvelopeMasking(
        IReadOnlyList<NoteCluster> clusters,
        double splitPitch,
        ExtractorOptions options)
    {
        var stages = new List<List<PathState>>(clusters.Count);
        var current = new List<PathState> { new() { ActiveNote = null, Score = 0, ParentIndex = -1 } };

        foreach (var cluster in clusters)
        {
            var candidates = cluster.Notes
                .GroupBy(note => note.NoteNumber)
                .Select(group => group.OrderByDescending(note => note.Velocity).ThenByDescending(note => note.DurationUs).First())
                .OrderByDescending(note => note.NoteNumber)
                .Take(3)
                .ToList();
            var bestByActive = new Dictionary<ActiveKey, PathState>();
            PathState? bestSkipState = null;

            for (int previousIndex = 0; previousIndex < current.Count; previousIndex++)
            {
                PathState previous = current[previousIndex];
                foreach (RawMidiNote candidate in candidates)
                {
                    double local = ComputeEmissionScore(candidate, cluster.Notes, splitPitch);
                    if (IsPreviousMelodySustaining(previous.ActiveNote, cluster.AnchorTimeUs, options)
                        && candidate.NoteNumber <= previous.ActiveNote!.NoteNumber - options.MaskingIntervalSemitones
                        && candidate.NoteNumber < splitPitch + 2)
                        local -= 22.0;

                    double transition = ComputeTransitionScore(previous.ActiveNote, candidate, cluster.AnchorTimeUs, splitPitch);
                    AddBest(bestByActive, candidate, previous.Score + local + transition, previousIndex, candidate);
                }

                double skipScore = previous.Score + ComputeNullScore(previous.ActiveNote, candidates, cluster.AnchorTimeUs, splitPitch, options);
                if (bestSkipState == null || skipScore > bestSkipState.Score)
                    bestSkipState = new PathState
                    {
                        ActiveNote = previous.ActiveNote,
                        Score = skipScore,
                        ParentIndex = previousIndex,
                        SelectedNote = null
                    };
            }

            current = bestByActive.Values.OrderByDescending(state => state.ActiveNote?.NoteNumber ?? -1).ToList();
            if (bestSkipState != null) current.Add(bestSkipState);
            if (current.Count == 0) return new List<RawMidiNote>();
            stages.Add(current);
        }

        int bestIndex = 0;
        for (int i = 1; i < current.Count; i++)
            if (current[i].Score > current[bestIndex].Score) bestIndex = i;

        var selected = new List<RawMidiNote>();
        for (int stageIndex = stages.Count - 1; stageIndex >= 0; stageIndex--)
        {
            PathState state = stages[stageIndex][bestIndex];
            if (state.SelectedNote != null) selected.Add(state.SelectedNote);
            bestIndex = state.ParentIndex;
        }
        selected.Reverse();
        return selected;
    }

    private static void AddBest(
        Dictionary<ActiveKey, PathState> bestByActive,
        RawMidiNote? activeNote,
        double score,
        int parentIndex,
        RawMidiNote? selectedNote)
    {
        var key = new ActiveKey(activeNote);
        if (!bestByActive.TryGetValue(key, out var existing) || score > existing.Score)
            bestByActive[key] = new PathState
            {
                ActiveNote = activeNote,
                Score = score,
                ParentIndex = parentIndex,
                SelectedNote = selectedNote
            };
    }

    private static double ComputeEmissionScore(RawMidiNote candidate, IReadOnlyList<RawMidiNote> cluster, double splitPitch)
    {
        int topPitch = cluster.Max(note => (int)note.NoteNumber);
        double registerOffset = candidate.NoteNumber - splitPitch;
        double duration = Math.Clamp(candidate.DurationUs / 250_000d, 0.3, 2.5);
        double velocity = Math.Clamp(candidate.Velocity / 100d, 0.3, 1.3);
        return (candidate.NoteNumber == topPitch ? 12.0 : -1.5 * (topPitch - candidate.NoteNumber))
            + Math.Clamp(registerOffset * 0.6, -12.0, 10.0)
            + velocity * 4.0 + duration * 3.0;
    }

    private static double ComputeNullScore(
        RawMidiNote? activeNote,
        IReadOnlyList<RawMidiNote> candidates,
        long timeUs,
        double splitPitch,
        ExtractorOptions options)
    {
        if (candidates.Count == 0) return -15.0;
        int topPitch = candidates.Max(note => (int)note.NoteNumber);
        if (IsPreviousMelodySustaining(activeNote, timeUs, options))
        {
            int pitchDrop = activeNote!.NoteNumber - topPitch;
            return pitchDrop >= options.MaskingIntervalSemitones
                ? 18.0 + Math.Min(pitchDrop * 0.8, 12.0)
                : -6.0;
        }
        return topPitch < splitPitch - 5 ? 6.0 : -15.0;
    }

    private static double ComputeTransitionScore(RawMidiNote? previous, RawMidiNote current, long timeUs, double splitPitch)
    {
        if (previous == null) return 0;
        if (timeUs - previous.StartUs > 1_500_000)
            return Math.Abs(current.NoteNumber - previous.NoteNumber) > 16 ? -4.0 : 2.0;

        int interval = current.NoteNumber - previous.NoteNumber;
        int magnitude = Math.Abs(interval);
        double score = magnitude switch
        {
            0 => 2.5,
            <= 4 => 5.0,
            <= 7 => 3.0,
            <= 12 => -1.5,
            _ => -(magnitude - 12) * 2.2
        };
        if (magnitude > 12 && interval < 0 && current.NoteNumber < splitPitch) score -= 12.0;
        return score;
    }

    private static bool IsPreviousMelodySustaining(RawMidiNote? previous, long timeUs, ExtractorOptions options)
    {
        if (previous == null) return false;
        long effectiveDuration = Math.Min(Math.Max(0, previous.DurationUs), options.MaxEnvelopeMaskDurationUs);
        long envelopeEnd = SafeEnd(previous.StartUs, effectiveDuration);
        return timeUs < envelopeEnd - 25_000;
    }

    private static long SafeEnd(long startUs, long durationUs)
    {
        if (durationUs <= 0) return startUs;
        return startUs > long.MaxValue - durationUs ? long.MaxValue : startUs + durationUs;
    }
}


