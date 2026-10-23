using MidiAutoPlayer.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MidiAutoPlayer.Guitar;

public static class MidiPolyphonyReducer
{
    public static List<RawMidiNote> ReduceForGuitar(
        IReadOnlyList<RawMidiNote> rawNotes,
        long onsetSnapWindowUs = 0,
        int maxVoicesPerFrame = 6)
    {
        if (rawNotes == null || rawNotes.Count == 0)
            return new List<RawMidiNote>();

        onsetSnapWindowUs = Math.Max(0, onsetSnapWindowUs);
        maxVoicesPerFrame = Math.Max(1, maxVoicesPerFrame);
        var notes = rawNotes.Where(n => n.Channel != 9 && n.Velocity >= 12 && n.DurationUs > 5_000 &&
            !(n.Program is >= 88 and <= 103 && n.DurationUs > 1_500_000))
            .OrderBy(n => n.StartUs).ThenByDescending(n => n.NoteNumber).ToList();
        var result = new List<RawMidiNote>(notes.Count);

        for (int i = 0; i < notes.Count;)
        {
            long anchor = notes[i].StartUs;
            int j = i + 1;
            var seenTracks = new HashSet<int> { notes[i].TrackId };
            while (j < notes.Count && notes[j].StartUs - anchor <= onsetSnapWindowUs)
            {
                // A later attack on the same track is a new note, not humanized
                // timing between tracks. Simultaneous notes on one track remain a chord.
                if (notes[j].StartUs > anchor && seenTracks.Contains(notes[j].TrackId))
                    break;
                seenTracks.Add(notes[j].TrackId);
                j++;
            }

            var pitches = notes.GetRange(i, j - i)
                .GroupBy(n => n.NoteNumber)
                .Select(g => g.OrderByDescending(n => n.IsPrimaryMelody).ThenByDescending(n => n.Velocity).ThenByDescending(n => n.DurationUs).First())
                .OrderByDescending(n => n.IsPrimaryMelody).ThenByDescending(n => n.NoteNumber).ToList();
            if (pitches.Count > maxVoicesPerFrame)
            {
                var chosen = pitches.Where(note => note.IsPrimaryMelody).Take(maxVoicesPerFrame).ToList();
                var pitchClasses = chosen.Select(note => (int)note.NoteNumber % 12).ToHashSet();
                if (chosen.Count == 0) chosen.Add(pitches[0]);
                pitchClasses.Add(pitches[0].NoteNumber % 12);
                if (maxVoicesPerFrame > 1)
                {
                    if (chosen.Count < maxVoicesPerFrame && !chosen.Contains(pitches[^1])) chosen.Add(pitches[^1]);
                    pitchClasses.Add(pitches[^1].NoteNumber % 12);
                }
                foreach (var note in pitches.OrderByDescending(n => n.IsPrimaryMelody).ThenByDescending(n => n.Velocity))
                {
                    if (chosen.Count >= maxVoicesPerFrame) break;
                    if (!chosen.Contains(note) && pitchClasses.Add(note.NoteNumber % 12)) chosen.Add(note);
                }
                pitches = chosen;
            }

            foreach (var note in pitches)
            {
                result.Add(new RawMidiNote
                {
                    StartUs = note.StartUs,
                    DurationUs = note.DurationUs,
                    NoteNumber = note.NoteNumber,
                    Velocity = note.Velocity,
                    Channel = note.Channel,
                    TrackId = note.TrackId,
                    Program = note.Program,
                    IsPrimaryMelody = note.IsPrimaryMelody,
                    OrigString = note.OrigString,
                    OrigFret = note.OrigFret,
                    VoiceIndex = note.VoiceIndex,
                    IsLetRing = note.IsLetRing,
                    IsPalmMute = note.IsPalmMute,
                    IsBrushChord = note.IsBrushChord,
                    IsBrushUp = note.IsBrushUp,
                    ExplicitChordName = note.ExplicitChordName,
                    SourceFormat = note.SourceFormat
                });
            }
            i = j;
        }

        return result;
    }
}
