using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Recognition;

public static class TrackPriorityMode
{
    private static readonly string[] VocalKeywords =
    {
        "melody", "vocal", "voice", "lead", "solo", "主旋律", "旋律", "主唱", "歌词", "唱"
    };

    private static readonly string[] SecondaryLeadKeywords =
    {
        "flute", "violin", "trumpet", "sax", "guitar", "笛", "琴", "箫", "二胡", "古筝"
    };

    private static readonly string[] PenaltyKeywords =
    {
        "bass", "drum", "perc", "cymb", "低音", "鼓", "伴奏", "chord", "rhythm", "pad", "strings", "backing"
    };

    public static RecognitionResult Extract(IReadOnlyList<RawMidiNote> rawNotes, IReadOnlyList<TrackMetadata>? tracks)
    {
        var nonDrumNotes = rawNotes.Where(n => n.Channel != 9).ToList();
        if (nonDrumNotes.Count == 0)
        {
            return new RecognitionResult
            {
                Mode = RecognitionMode.TrackPriority,
                Notes = new List<RawMidiNote>(),
                MelodyScore = 0,
                Name = "轨道优先",
                Summary = "未找到有效非打击乐音符"
            };
        }

        var trackGroups = nonDrumNotes.GroupBy(n => n.TrackId).ToList();
        if (trackGroups.Count <= 1)
        {
            return new RecognitionResult
            {
                Mode = RecognitionMode.TrackPriority,
                Notes = nonDrumNotes,
                MelodyScore = 70,
                Name = "轨道优先",
                Summary = $"单轨文件 (共 {nonDrumNotes.Count} 音符)"
            };
        }

        int bestScore = int.MinValue;
        IGrouping<int, RawMidiNote>? bestGroup = null;
        string bestSummary = "";

        var metaDict = tracks?.ToDictionary(t => t.TrackId) ?? new Dictionary<int, TrackMetadata>();

        foreach (var group in trackGroups)
        {
            var notesList = group.ToList();
            if (notesList.Count == 0) continue;

            int score = 50;
            if (notesList.Count < 5) score -= 10;
            string trackName = "";
            int program = -1;

            if (metaDict.TryGetValue(group.Key, out var meta))
            {
                trackName = meta.Name;
                program = meta.Program;
            }
            else
            {
                program = notesList[0].Program;
            }

            // 1. Name keywords analysis
            string lowerName = trackName.ToLowerInvariant();
            if (VocalKeywords.Any(k => lowerName.Contains(k)))
            {
                score += 45;
            }
            else if (SecondaryLeadKeywords.Any(k => lowerName.Contains(k)))
            {
                score += 20;
            }

            if (PenaltyKeywords.Any(k => lowerName.Contains(k)))
            {
                score -= 45;
            }

            // 2. GM Instrument Program analysis
            if (program >= 0)
            {
                switch (program)
                {
                    case >= 32 and <= 39: // Bass
                        score -= 50;
                        break;
                    case 42 or 43: // Cello / Contrabass
                        score -= 30;
                        break;
                    case 40: // Violin
                    case 56 or 57: // Trumpet
                    case >= 64 and <= 71: // Sax, Oboe, Clarinet
                    case >= 72 and <= 79: // Flute, Piccolo
                    case >= 80 and <= 87: // Synth Lead
                        score += 30;
                        break;
                    case >= 88 and <= 95: // Synth Pad
                        score -= 20;
                        break;
                }
            }

            // 3. Pitch register score
            double avgPitch = notesList.Average(n => n.NoteNumber);
            if (avgPitch is >= 58 and <= 80)
            {
                score += 25; // Human singing range (D4 ~ G#5)
            }
            else if (avgPitch < 48)
            {
                score -= 40; // Bass register
            }
            else if (avgPitch > 88)
            {
                score -= 15; // Shrill register
            }

            // 4. Monophonic ratio
            int sliceCount = 0;
            int singleNoteSlices = 0;
            long lastTime = -1;
            int currentNotesInSlice = 0;

            for (int i = 0; i < notesList.Count; i++)
            {
                if (lastTime == -1 || Math.Abs(notesList[i].StartUs - lastTime) > 20_000)
                {
                    if (currentNotesInSlice == 1) singleNoteSlices++;
                    if (currentNotesInSlice > 0) sliceCount++;

                    lastTime = notesList[i].StartUs;
                    currentNotesInSlice = 1;
                }
                else
                {
                    currentNotesInSlice++;
                }
            }
            if (currentNotesInSlice == 1) singleNoteSlices++;
            if (currentNotesInSlice > 0) sliceCount++;

            double monoRatio = sliceCount > 0 ? (double)singleNoteSlices / sliceCount : 0;
            if (monoRatio > 0.85) score += 25;
            else if (monoRatio < 0.4) score -= 20;

            if (score > bestScore)
            {
                bestScore = score;
                bestGroup = group;
                bestSummary = string.IsNullOrWhiteSpace(trackName)
                    ? $"Track {group.Key} ({notesList.Count}音符, 单音率{(int)(monoRatio * 100)}%)"
                    : $"Track {group.Key}: {trackName} ({notesList.Count}音符)";
            }
        }

        bestGroup ??= trackGroups.OrderByDescending(g => g.Count()).First();
        int finalScore = Math.Clamp(bestScore, 10, 99);

        return new RecognitionResult
        {
            Mode = RecognitionMode.TrackPriority,
            Notes = bestGroup.OrderBy(n => n.StartUs).ToList(),
            MelodyScore = finalScore,
            Name = "轨道优先",
            Summary = string.IsNullOrWhiteSpace(bestSummary) ? $"Track {bestGroup.Key}" : bestSummary
        };
    }
}
