using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Recognition;

public static class HighestNoteMode
{
    public static RecognitionResult Extract(IReadOnlyList<RawMidiNote> rawNotes)
    {
        var nonDrumNotes = rawNotes.Where(n => n.Channel != 9).OrderBy(n => n.StartUs).ToList();
        if (nonDrumNotes.Count == 0)
        {
            return new RecognitionResult
            {
                Mode = RecognitionMode.HighestNote,
                Notes = new List<RawMidiNote>(),
                MelodyScore = 0,
                Name = "高音优先",
                Summary = "未找到有效非打击乐音符"
            };
        }

        // Group into slices within 18ms tolerance
        var slices = new List<List<RawMidiNote>>();
        List<RawMidiNote>? currentSlice = null;
        long sliceStartUs = -1;

        foreach (var note in nonDrumNotes)
        {
            if (currentSlice == null || note.StartUs - sliceStartUs > 18_000)
            {
                currentSlice = new List<RawMidiNote>();
                slices.Add(currentSlice);
                sliceStartUs = note.StartUs;
            }
            currentSlice.Add(note);
        }

        var result = new List<RawMidiNote>(slices.Count);
        RawMidiNote? prevNote = null;
        int smoothTransitions = 0;
        int totalTransitions = 0;

        foreach (var slice in slices)
        {
            // Filter extreme short grace notes (< 15ms) if normal duration notes exist
            var candidates = slice;
            if (candidates.Count > 1 && candidates.Any(n => n.DurationUs >= 30_000))
            {
                candidates = candidates.Where(n => n.DurationUs >= 15_000).ToList();
                if (candidates.Count == 0) candidates = slice;
            }

            // Sort candidates by pitch descending
            var sorted = candidates.OrderByDescending(n => n.NoteNumber).ToList();

            // Filter extreme solitary high outliers (e.g. > 96 / C7) if much higher than rest
            if (sorted.Count > 1 && sorted[0].NoteNumber > 96 && (sorted[0].NoteNumber - sorted[1].NoteNumber >= 15))
            {
                sorted.RemoveAt(0);
            }

            RawMidiNote chosen = sorted[0];

            // If there's an octave jump from previous note, check if second-highest note is much smoother
            if (prevNote != null && sorted.Count > 1)
            {
                int leap1 = Math.Abs(sorted[0].NoteNumber - prevNote.NoteNumber);
                int leap2 = Math.Abs(sorted[1].NoteNumber - prevNote.NoteNumber);

                if (leap1 >= 14 && leap2 <= 6)
                {
                    chosen = sorted[1];
                }
            }

            if (prevNote != null)
            {
                totalTransitions++;
                if (Math.Abs(chosen.NoteNumber - prevNote.NoteNumber) <= 7)
                {
                    smoothTransitions++;
                }
            }

            result.Add(chosen);
            prevNote = chosen;
        }

        double smoothRatio = totalTransitions > 0 ? (double)smoothTransitions / totalTransitions : 1.0;
        int score = (int)(smoothRatio * 60 + 35);
        score = Math.Clamp(score, 40, 95);

        return new RecognitionResult
        {
            Mode = RecognitionMode.HighestNote,
            Notes = result,
            MelodyScore = score,
            Name = "高音优先",
            Summary = $"提取顶声部 {result.Count} 个音符 (平滑度 {(int)(smoothRatio * 100)}%)"
        };
    }
}
