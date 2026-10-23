using MidiAutoPlayer.Models;
using MidiAutoPlayer.Recognition;

namespace MidiAutoPlayer.Midi;

public static class MidiConverter
{
    public const byte GameMinMidi = 52; // E3
    public const byte GameMaxMidi = 87; // D#6
    public const int NoteRange = GameMaxMidi - GameMinMidi + 1; // 36 notes

    public static ConvertedSong Convert(
        IReadOnlyList<RawMidiNote> rawNotes,
        string sourceName,
        string sourceHash,
        string displayName,
        ConvertOptions options)
    {
        // Ensure notes are sorted by StartUs
        bool isSorted = true;
        for (int i = 1; i < rawNotes.Count; i++)
        {
            if (rawNotes[i].StartUs < rawNotes[i - 1].StartUs)
            {
                isSorted = false;
                break;
            }
        }
        if (!isSorted)
        {
            var sortedList = new List<RawMidiNote>(rawNotes);
            sortedList.Sort((a, b) =>
            {
                int cmp = a.StartUs.CompareTo(b.StartUs);
                return cmp != 0 ? cmp : a.NoteNumber.CompareTo(b.NoteNumber);
            });
            rawNotes = sortedList;
        }

        HashSet<(int TrackId, byte Channel, long StartUs, byte Pitch)> primaryKeys = new();
        if (string.Equals(options.RecognitionModeName, nameof(RecognitionMode.FullSong), StringComparison.OrdinalIgnoreCase))
        {
            var nonDrum = rawNotes.Where(note => note.Channel != 9).ToList();
            var primary = nonDrum.Select(note => note.TrackId).Distinct().Take(2).Count() <= 1
                ? PolyphonicMelodyExtractor.ExtractSingleTrackMelody(nonDrum)
                : AutoLeadFusionMode.Extract(nonDrum, tracks: null).Notes;
            primaryKeys = primary.Select(note => (note.TrackId, note.Channel, note.StartUs, note.NoteNumber)).ToHashSet();
        }

        var candidates = new List<RawMidiNote>(rawNotes.Count);
        foreach (var raw in rawNotes)
        {
            if (raw.Velocity == 0 || (options.IgnoreDrumChannel && raw.Channel == 9)
                || (options.EnabledTracks != null && !options.EnabledTracks.Contains(raw.TrackId))) continue;
            int transposed = Math.Clamp(raw.NoteNumber + options.Transpose, 0, 127);
            var candidate = PhraseOctaveFolder.CloneWithPitch(raw, transposed);
            candidate.IsPrimaryMelody = raw.IsPrimaryMelody || primaryKeys.Contains((raw.TrackId, raw.Channel, raw.StartUs, raw.NoteNumber));
            candidates.Add(candidate);
        }
        bool foldPhrases = string.Equals(options.OutOfRangeMode, "OctaveFold", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<RawMidiNote> mappedSources = foldPhrases
            ? PhraseOctaveFolder.Apply(candidates, GameMinMidi, GameMaxMidi)
            : candidates.Where(note => note.NoteNumber >= GameMinMidi && note.NoteNumber <= GameMaxMidi).ToList();
        if (foldPhrases && mappedSources.Any(note => note.IsPrimaryMelody))
        {
            var primaryNotes = mappedSources.Where(note => note.IsPrimaryMelody).OrderBy(note => note.StartUs).ToList();
            var activePrimary = new List<RawMidiNote>();
            var collisionFiltered = new List<RawMidiNote>(mappedSources.Count);
            int nextPrimary = 0;
            foreach (var note in mappedSources.OrderBy(note => note.StartUs).ThenByDescending(note => note.NoteNumber))
            {
                while (nextPrimary < primaryNotes.Count && primaryNotes[nextPrimary].StartUs <= note.StartUs)
                    activePrimary.Add(primaryNotes[nextPrimary++]);
                activePrimary.RemoveAll(lead => note.StartUs - lead.StartUs >= Math.Max(0, lead.DurationUs));
                bool collidesWithLead = note.WasOctaveShifted && activePrimary.Any(lead =>
                    Math.Abs(lead.NoteNumber - note.NoteNumber) < 4);
                if (note.IsPrimaryMelody || !collidesWithLead) collisionFiltered.Add(note);
            }
            mappedSources = collisionFiltered;
        }

        var resultNotes = new List<GameNoteEvent>(Math.Min(mappedSources.Count, 100_000));
        long maxTimeUs = 0;
        long currentTimestamp = -1;
        ulong currentMask = 0;

        foreach (var raw in mappedSources)
        {
            int note = raw.NoteNumber;

            // Stage E: Convert to 0~35 game note index
            byte noteIndex = (byte)(note - GameMinMidi);
            if (noteIndex >= NoteRange) continue;

            // Stage F: Simultaneous same-note deduplication (O(1) register bitmask per timestamp)
            long timeUs = raw.StartUs;
            if (timeUs != currentTimestamp)
            {
                currentTimestamp = timeUs;
                currentMask = 0;
            }

            ulong bit = 1UL << noteIndex;
            if ((currentMask & bit) == 0)
            {
                currentMask |= bit;
                resultNotes.Add(new GameNoteEvent(timeUs, noteIndex, raw.DurationUs));
                long endUs = timeUs + Math.Max(raw.DurationUs, 30_000);
                if (endUs > maxTimeUs) maxTimeUs = endUs;
            }
        }

        // Sort by TimeUs, then NoteIndex
        resultNotes.Sort((a, b) =>
        {
            int cmp = a.TimeUs.CompareTo(b.TimeUs);
            return cmp != 0 ? cmp : a.NoteIndex.CompareTo(b.NoteIndex);
        });

        if (resultNotes.Count > 0 && maxTimeUs < resultNotes[^1].TimeUs + 500_000)
        {
            maxTimeUs = resultNotes[^1].TimeUs + 500_000;
        }

        return new ConvertedSong
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileNameWithoutExtension(sourceName) : displayName,
            CreatedAt = DateTime.Now,
            SourceName = sourceName,
            SourceHash = sourceHash,
            DurationUs = maxTimeUs,
            Notes = resultNotes,
            RawNotes = rawNotes.ToList(),
            Options = options,
            FormatVersion = 1,
            TargetInstrument = options.TargetInstrument,
            RecognitionModeName = options.RecognitionModeName
        };
    }
}
