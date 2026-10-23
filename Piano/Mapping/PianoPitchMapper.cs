using MidiAutoPlayer.Models;
using MidiAutoPlayer.Piano.Models;

namespace MidiAutoPlayer.Piano.Mapping;

public static class PianoPitchMapper
{
    public const int PlayableMinMidi = 48;
    public const int PlayableMaxMidi = 83;
    private static readonly HashSet<int> NaturalPitchClasses = [0, 2, 4, 5, 7, 9, 11];

    public static int FoldIntoPlayableRange(int midiPitch, int minMidi = PlayableMinMidi, int maxMidi = PlayableMaxMidi)
    {
        if (minMidi > maxMidi) throw new ArgumentOutOfRangeException(nameof(minMidi));
        if (midiPitch >= minMidi && midiPitch <= maxMidi) return midiPitch;

        // Keep the pitch class intact while folding by octaves. The game range spans
        // three complete octaves, so every chromatic pitch has a valid destination.
        int pitchClass = FloorMod(midiPitch, 12);
        int first = minMidi + FloorMod(pitchClass - minMidi, 12);
        if (first > maxMidi) return Math.Clamp(midiPitch, minMidi, maxMidi);

        int last = first + ((maxMidi - first) / 12) * 12;
        int lower = midiPitch < first ? first : first + ((midiPitch - first) / 12) * 12;
        lower = Math.Clamp(lower, first, last);
        int upper = Math.Min(last, lower + 12);
        return Math.Abs(midiPitch - lower) <= Math.Abs(upper - midiPitch) ? lower : upper;
    }

    public static int FindBestAutoOctaveTranspose(IReadOnlyList<RawMidiNote> notes, int userTransposeSemitones = 0)
    {
        if (notes.Count == 0 || userTransposeSemitones != 0) return userTransposeSemitones;

        int[] shifts = Enumerable.Range(-18, 37).ToArray();
        int bestShift = 0;
        double bestScore = double.NegativeInfinity;
        foreach (int shift in shifts)
        {
            double score = -Math.Abs(shift) * 0.65;
            foreach (RawMidiNote note in notes)
            {
                if (note.Channel == 9 || note.DurationUs <= 0) continue;
                int pitch = note.NoteNumber + shift;
                double weight = Math.Clamp(note.DurationUs / 100_000d, 0.5, 6) * Math.Clamp(note.Velocity / 100d, 0.25, 1.27);
                if (pitch is < PlayableMinMidi or > PlayableMaxMidi)
                    score -= (12 + Math.Min(36, Math.Min(Math.Abs(pitch - PlayableMinMidi), Math.Abs(pitch - PlayableMaxMidi)))) * weight;
                else
                {
                    score += 12 * weight;
                    if (pitch is >= 55 and <= 79) score += 1.5 * weight;
                    if (NaturalPitchClasses.Contains(FloorMod(pitch, 12))) score += 2.5 * weight;
                }
                if (pitch is < 0 or > 127) score -= 2 * weight;
            }
            if (score > bestScore)
            {
                bestScore = score;
                bestShift = shift;
            }
        }
        return bestShift;
    }

    public static int FindBestTwentyOneKeyTranspose(IReadOnlyList<RawMidiNote> notes, PianoLayoutProfile layout, int userTransposeSemitones = 0)
    {
        if (userTransposeSemitones != 0 || layout.ScaleProfile is null || notes.Count == 0) return userTransposeSemitones;
        var scale = layout.ScaleProfile;
        int tonic = scale.TonicMidiPitchAtMiddleRow;
        int min = tonic - 12, max = tonic + 23;
        var scalePcs = scale.Intervals.Select(interval => FloorMod(interval, 12)).ToHashSet();
        int[] candidates = Enumerable.Range(-18, 37).ToArray();
        double bestScore = double.NegativeInfinity;
        int bestShift = 0;
        foreach (int shift in candidates.Distinct())
        {
            double score = -0.65 * Math.Abs(shift);
            foreach (var note in notes)
            {
                if (note.Channel == 9 || note.DurationUs <= 0 || note.StartUs < 0) continue;
                double weight = Math.Clamp(note.DurationUs / 100_000d, 0.5, 6) * Math.Clamp(note.Velocity / 100d, 0.25, 1.27);
                int pitch = note.NoteNumber + shift;
                if (pitch is < 0 or > 127) { score -= 2 * weight; continue; }
                if (pitch < min || pitch > max)
                    score -= (12 + Math.Min(36, Math.Min(Math.Abs(pitch - min), Math.Abs(pitch - max)))) * weight;
                else
                {
                    score += 12 * weight;
                    if (pitch is >= 55 and <= 79) score += 1.5 * weight;
                    score += scalePcs.Contains(FloorMod(pitch - tonic, 12)) ? 15.0 * weight : -20.0 * weight;
                }
            }
            if (score > bestScore) { bestScore = score; bestShift = shift; }
        }
        return bestShift;
    }

    public static PianoMapResult TryMapTwelveKey(
        int rawMidiPitch,
        PianoLayoutProfile layout,
        out bool wasOctaveFolded,
        PianoModifier? preferredModifier = null)
    {
        wasOctaveFolded = false;
        if (layout.Kind != PianoLayoutKind.TwelveKeyChromatic || layout.ModifierProfile is null)
            return Fail(PianoIssueCode.InvalidLayout, "当前布局没有有效的十二键半音配置。");

        var basePitches = layout.Bindings
            .Where(binding => binding.IsVerified && binding.ScanCode is > 0 && binding.BaseMidiPitch.HasValue)
            .Select(binding => binding.BaseMidiPitch!.Value)
            .ToArray();
        if (basePitches.Length == 0)
            return Fail(PianoIssueCode.MissingBinding, "十二键布局没有可用的自然音键位。");

        int minPitch = Math.Min(PlayableMinMidi, basePitches.Min());
        int maxPitch = Math.Max(PlayableMaxMidi, basePitches.Max());
        int foldedPitch = FoldIntoPlayableRange(rawMidiPitch, minPitch, maxPitch);
        wasOctaveFolded = foldedPitch != rawMidiPitch;

        IReadOnlyList<PianoPitchCandidate> candidates = FindTwelveKeyCandidates(foldedPitch, layout);
        if (candidates.Count == 0)
            return Fail(PianoIssueCode.MissingBinding, "当前十二键布局没有对应的模拟键位。");

        PianoPitchCandidate selected = candidates
            .OrderBy(candidate => candidate.Modifier == PianoModifier.None ? 0 : 1)
            .ThenBy(candidate => preferredModifier.HasValue && candidate.Modifier == preferredModifier.Value ? 0 : 1)
            .ThenBy(candidate => Math.Abs(candidate.Binding.BaseMidiPitch.GetValueOrDefault() - foldedPitch))
            .ThenBy(candidate => candidate.Binding.Row)
            .ThenBy(candidate => candidate.Binding.Position)
            .First();

        var mapped = new PianoMappedNote(
            foldedPitch,
            selected.Binding.Position,
            selected.Binding.Row,
            selected.Binding,
            selected.Modifier);
        return new PianoMapResult(mapped, null);
    }

    public static IReadOnlyList<PianoPitchCandidate> FindTwelveKeyCandidates(int targetPitch, PianoLayoutProfile layout)
    {
        if (layout.Kind != PianoLayoutKind.TwelveKeyChromatic || layout.ModifierProfile is null)
            return Array.Empty<PianoPitchCandidate>();

        var candidates = new List<PianoPitchCandidate>();
        foreach (PianoKeyBinding binding in layout.Bindings)
        {
            if (!binding.IsVerified || binding.ScanCode is null or 0 || binding.BaseMidiPitch is not int basePitch)
                continue;
            if (basePitch == targetPitch)
                candidates.Add(new PianoPitchCandidate(binding, PianoModifier.None, targetPitch));
            AddModifierCandidate(binding, targetPitch, layout.ModifierProfile.Shift, PianoModifier.Shift, candidates);
            AddModifierCandidate(binding, targetPitch, layout.ModifierProfile.Control, PianoModifier.Control, candidates);
        }

        return candidates
            .OrderBy(candidate => candidate.Modifier == PianoModifier.None ? 0 : 1)
            .ThenBy(candidate => candidate.Modifier)
            .ThenBy(candidate => candidate.Binding.Row)
            .ThenBy(candidate => candidate.Binding.Position)
            .ToArray();
    }

    public static PianoMapResult TryMapSevenKey(int rawMidiPitch, PianoLayoutProfile layout) =>
        TryMapSevenKey(rawMidiPitch, layout, out _);

    public static PianoMapResult TryMapSevenKey(int rawMidiPitch, PianoLayoutProfile layout, out bool wasAdapted)
        => TryMapSevenKey(rawMidiPitch, layout, out wasAdapted, null);

    public static PianoMapResult TryMapSevenKey(int rawMidiPitch, PianoLayoutProfile layout, out bool wasAdapted, ISet<(PianoRow Row, int Degree)>? occupiedInChord)
    {
        wasAdapted = false;
        if (layout.Kind != PianoLayoutKind.SevenKeyScale || layout.ScaleProfile is null)
            return Fail(PianoIssueCode.InvalidLayout, "当前布局没有有效的七键调式配置。");

        var scale = layout.ScaleProfile;
        int minPitch = scale.TonicMidiPitchAtMiddleRow - 12;
        int maxPitch = scale.TonicMidiPitchAtMiddleRow + 23;
        int pitch = FoldIntoPlayableRange(rawMidiPitch, minPitch, maxPitch);
        wasAdapted = pitch != rawMidiPitch;

        int delta = pitch - scale.TonicMidiPitchAtMiddleRow;
        int octave = FloorDiv(delta, 12);
        int pitchClass = FloorMod(delta, 12);
        int degreeIndex = scale.Intervals.IndexOf(pitchClass);
        if (degreeIndex < 0)
        {
            wasAdapted = true;
            var nearest = scale.Intervals.Select((interval, index) => (index, distance: Math.Min(FloorMod(pitchClass - interval, 12), FloorMod(interval - pitchClass, 12))))
                .ToList();
            int bestDistance = nearest.Min(item => item.distance);
            var tied = nearest.Where(item => item.distance == bestDistance).ToList();
            var free = tied.FirstOrDefault(item => occupiedInChord?.Contains((octave < 0 ? PianoRow.Low : octave == 0 ? PianoRow.Middle : PianoRow.High, item.index + 1)) == false);
            degreeIndex = free == default ? tied[0].index : free.index;
        }

        PianoRow row = octave < 0 ? PianoRow.Low : octave == 0 ? PianoRow.Middle : PianoRow.High;
        int degree = degreeIndex + 1;
        PianoKeyBinding? binding = layout.Bindings.FirstOrDefault(item => item.Row == row && item.Position == degree);
        if (binding is null || binding.ScanCode is null or 0 || !binding.IsVerified)
            return Fail(PianoIssueCode.MissingBinding, $"{row} 音区第 {degree} 级没有可用的模拟键位。");

        return new PianoMapResult(new PianoMappedNote(pitch, degree, row, binding, PianoModifier.None), null);
    }

    public static int FloorDiv(int value, int divisor)
    {
        int quotient = value / divisor;
        int remainder = value % divisor;
        return remainder < 0 ? quotient - 1 : quotient;
    }

    public static int FloorMod(int value, int divisor)
    {
        int remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private static void AddModifierCandidate(
        PianoKeyBinding binding,
        int targetPitch,
        PianoModifierKey modifierKey,
        PianoModifier modifier,
        ICollection<PianoPitchCandidate> candidates)
    {
        if (!modifierKey.IsVerified || modifierKey.ScanCode is null or 0 || modifierKey.SemitoneDelta == 0)
            return;
        if (binding.BaseMidiPitch!.Value + modifierKey.SemitoneDelta == targetPitch)
            candidates.Add(new PianoPitchCandidate(binding, modifier, targetPitch));
    }

    private static PianoMapResult Fail(PianoIssueCode code, string message) =>
        new(null, new PianoMapIssue(code, message));
}
