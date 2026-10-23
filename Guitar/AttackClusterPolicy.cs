using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Guitar;

internal static class AttackClusterPolicy
{
    public static bool CanJoin(IReadOnlyList<RawMidiNote> frame, RawMidiNote note, long frameStartUs,
        long sameTrackRollWindowUs, GuitarTuning tuning)
    {
        if (note.StartUs <= frameStartUs || frame.All(existing => existing.TrackId != note.TrackId))
            return true;

        if (note.StartUs - frameStartUs > sameTrackRollWindowUs)
            return false;

        var sameTrack = frame.Where(existing => existing.TrackId == note.TrackId).ToList();
        bool markedBrush = note.IsBrushChord && sameTrack.All(existing => existing.IsBrushChord);
        if (!markedBrush && sameTrack.Any(existing => existing.NoteNumber == note.NoteNumber))
            return false;

        if (note.OrigString is int sourceString &&
            sameTrack.Any(existing => existing.OrigString == sourceString))
            return false;

        // Source strings distinguish GTP retriggers, but transposition may move a
        // pitch to another playable string. Assign target strings from final pitches.
        sameTrack.Add(note);
        if (sameTrack.Count > 6) return false;
        var used = new bool[7];
        return CanAssign(sameTrack, 0, used, tuning);
    }

    private static bool CanAssign(IReadOnlyList<RawMidiNote> notes, int index, bool[] used, GuitarTuning tuning)
    {
        if (index == notes.Count) return true;
        var note = notes[index];
        for (int stringNo = 1; stringNo <= 6; stringNo++)
        {
            if (used[stringNo])
                continue;
            int fret = note.NoteNumber - tuning.GetOpenMidiNote((byte)stringNo);
            if (fret is < 0 or > 24) continue;
            used[stringNo] = true;
            if (CanAssign(notes, index + 1, used, tuning)) return true;
            used[stringNo] = false;
        }
        return false;
    }
}
