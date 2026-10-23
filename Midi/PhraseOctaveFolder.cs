using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Midi;

/// <summary>Applies one octave-equivalent shift to each phrase without changing its contour.</summary>
public static class PhraseOctaveFolder
{
    public const long PhraseBreakGapUs = 400_000;

    public static List<RawMidiNote> Apply(IReadOnlyList<RawMidiNote> notes, int minMidi, int maxMidi)
    {
        if (minMidi > maxMidi) throw new ArgumentOutOfRangeException(nameof(minMidi));
        var result = new List<RawMidiNote>(notes.Count);
        foreach (var voice in notes.GroupBy(note => (note.TrackId, note.Channel)))
        {
            var ordered = voice.OrderBy(note => note.StartUs).ThenByDescending(note => note.NoteNumber).ToList();
            for (int i = 0; i < ordered.Count;)
            {
                int end = i + 1;
                while (end < ordered.Count && ordered[end].StartUs - ordered[end - 1].StartUs < PhraseBreakGapUs)
                    end++;
                var phrase = ordered.GetRange(i, end - i);
                int shift = FindBestOctaveShift(phrase, minMidi, maxMidi);
                foreach (var note in phrase)
                {
                    int pitch = note.NoteNumber + shift;
                    if (pitch < minMidi || pitch > maxMidi) continue;
                    RawMidiNote mapped = CloneWithPitch(note, pitch);
                    mapped.WasOctaveShifted = note.WasOctaveShifted || shift != 0;
                    result.Add(mapped);
                }
                i = end;
            }
        }
        return result.OrderBy(note => note.StartUs).ThenByDescending(note => note.NoteNumber).ToList();
    }

    public static RawMidiNote CloneWithPitch(RawMidiNote note, int pitch) => new()
    {
        StartUs = note.StartUs,
        DurationUs = note.DurationUs,
        NoteNumber = checked((byte)pitch),
        Velocity = note.Velocity,
        Channel = note.Channel,
        TrackId = note.TrackId,
        Program = note.Program,
        IsPrimaryMelody = note.IsPrimaryMelody,
        WasOctaveShifted = note.WasOctaveShifted,
        OrigString = note.OrigString,
        OrigFret = note.OrigFret,
        VoiceIndex = note.VoiceIndex,
        IsLetRing = note.IsLetRing,
        IsPalmMute = note.IsPalmMute,
        IsBrushChord = note.IsBrushChord,
        IsBrushUp = note.IsBrushUp,
        ExplicitChordName = note.ExplicitChordName,
        SourceFormat = note.SourceFormat
    };

    private static int FindBestOctaveShift(IReadOnlyList<RawMidiNote> phrase, int minMidi, int maxMidi)
    {
        int minShift = (int)Math.Ceiling((minMidi - phrase.Max(note => (int)note.NoteNumber)) / 12d);
        int maxShift = (int)Math.Floor((maxMidi - phrase.Min(note => (int)note.NoteNumber)) / 12d);
        if (minShift > maxShift)
        {
            // No octave position can contain the whole phrase; choose the shift
            // with the best weighted coverage and let the caller skip outliers.
            minShift = -11;
            maxShift = 11;
        }

        int bestShift = 0;
        double bestScore = double.NegativeInfinity;
        double center = (minMidi + maxMidi) / 2d;
        for (int octave = minShift; octave <= maxShift; octave++)
        {
            int shift = octave * 12;
            double score = -Math.Abs(shift) * 0.01;
            foreach (var note in phrase)
            {
                double weight = Math.Clamp(note.DurationUs / 100_000d, 0.5, 8)
                    * Math.Clamp(note.Velocity / 100d, 0.25, 1.27);
                int pitch = note.NoteNumber + shift;
                if (pitch < minMidi) score -= weight * (100 + (minMidi - pitch) * 4);
                else if (pitch > maxMidi) score -= weight * (100 + (pitch - maxMidi) * 4);
                else
                {
                    double centerFit = 1 - Math.Abs(pitch - center) / Math.Max(1, (maxMidi - minMidi) / 2d);
                    score += weight * (100 + Math.Max(0, centerFit) * 8);
                }
            }
            if (score > bestScore || (score == bestScore && Math.Abs(shift) < Math.Abs(bestShift)))
            {
                bestScore = score;
                bestShift = shift;
            }
        }
        return bestShift;
    }
}
