using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Recognition;

public static class MonophonicMode
{
    public static RecognitionResult Extract(IReadOnlyList<RawMidiNote> rawNotes, IReadOnlyList<TrackMetadata>? tracks)
    {
        var nonDrumNotes = rawNotes.Where(n => n.Channel != 9).OrderBy(n => n.StartUs).ToList();
        if (nonDrumNotes.Count == 0)
        {
            return new RecognitionResult
            {
                Mode = RecognitionMode.Monophonic,
                Notes = new List<RawMidiNote>(),
                MelodyScore = 0,
                Name = "单音优先",
                Summary = "未找到有效非打击乐音符"
            };
        }

        // Group by (TrackId, Channel)
        var voiceGroups = nonDrumNotes.GroupBy(n => (n.TrackId, n.Channel)).ToList();

        int bestScore = int.MinValue;
        IGrouping<(int TrackId, byte Channel), RawMidiNote>? bestVoice = null;
        string bestSummary = "";

        var metaDict = tracks?.ToDictionary(t => t.TrackId) ?? new Dictionary<int, TrackMetadata>();

        foreach (var group in voiceGroups)
        {
            var notes = group.ToList();
            if (notes.Count < 10) continue;

            int score = 50;

            // 1. Monophonic ratio
            int sliceCount = 0;
            int singleCount = 0;
            long lastTime = -1;
            int currentSliceNotes = 0;

            for (int i = 0; i < notes.Count; i++)
            {
                if (lastTime == -1 || Math.Abs(notes[i].StartUs - lastTime) > 20_000)
                {
                    if (currentSliceNotes == 1) singleCount++;
                    if (currentSliceNotes > 0) sliceCount++;

                    lastTime = notes[i].StartUs;
                    currentSliceNotes = 1;
                }
                else
                {
                    currentSliceNotes++;
                }
            }
            if (currentSliceNotes == 1) singleCount++;
            if (currentSliceNotes > 0) sliceCount++;

            double monoRatio = sliceCount > 0 ? (double)singleCount / sliceCount : 0.0;
            if (monoRatio >= 0.90) score += 40;
            else if (monoRatio >= 0.75) score += 20;
            else if (monoRatio < 0.40) score -= 30;

            // 2. Pitch register analysis
            var sortedPitches = notes.Select(n => (int)n.NoteNumber).OrderBy(p => p).ToList();
            int medianPitch = sortedPitches[sortedPitches.Count / 2];

            if (medianPitch is >= 58 and <= 82)
            {
                score += 30; // Sweet singing/solo range
            }
            else if (medianPitch < 50)
            {
                score -= 60; // Strong bass penalty
            }
            else if (medianPitch > 88)
            {
                score -= 15;
            }

            // 3. Instrument Program hint
            int program = notes[0].Program;
            if (program >= 32 && program <= 39) score -= 50; // Bass
            if (program >= 80 && program <= 87) score += 20; // Synth Lead

            if (score > bestScore)
            {
                bestScore = score;
                bestVoice = group;

                string tName = metaDict.TryGetValue(group.Key.TrackId, out var meta) ? meta.Name : "";
                bestSummary = string.IsNullOrWhiteSpace(tName)
                    ? $"Track {group.Key.TrackId} Ch {group.Key.Channel + 1} ({notes.Count}音符, 单音率{(int)(monoRatio * 100)}%)"
                    : $"{tName} ({notes.Count}音符, 单音率{(int)(monoRatio * 100)}%)";
            }
        }

        bestVoice ??= voiceGroups.OrderByDescending(g => g.Count()).First();
        var selectedNotes = bestVoice.OrderBy(n => n.StartUs).ToList();

        // If the selected voice has minor simultaneous chords, collapse to top note
        var deduped = new List<RawMidiNote>();
        long lastSliceTime = -1;
        RawMidiNote? topNoteInSlice = null;

        foreach (var note in selectedNotes)
        {
            if (lastSliceTime == -1 || Math.Abs(note.StartUs - lastSliceTime) > 20_000)
            {
                if (topNoteInSlice != null) deduped.Add(topNoteInSlice);
                lastSliceTime = note.StartUs;
                topNoteInSlice = note;
            }
            else
            {
                if (topNoteInSlice != null && note.NoteNumber > topNoteInSlice.NoteNumber)
                {
                    topNoteInSlice = note;
                }
            }
        }
        if (topNoteInSlice != null) deduped.Add(topNoteInSlice);

        int finalScore = Math.Clamp(bestScore, 30, 95);

        return new RecognitionResult
        {
            Mode = RecognitionMode.Monophonic,
            Notes = deduped,
            MelodyScore = finalScore,
            Name = "单音优先",
            Summary = string.IsNullOrWhiteSpace(bestSummary) ? $"Track {bestVoice.Key.TrackId}" : bestSummary
        };
    }
}
