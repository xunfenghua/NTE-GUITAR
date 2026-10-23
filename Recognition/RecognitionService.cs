using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Recognition;

public static class RecognitionService
{
    public static Dictionary<RecognitionMode, RecognitionResult> GenerateAllCandidates(
        IReadOnlyList<RawMidiNote> rawNotes,
        IReadOnlyList<TrackMetadata>? tracks)
    {
        var dict = new Dictionary<RecognitionMode, RecognitionResult>();

        // 1. Full Song (Default Primary Feature)
        dict[RecognitionMode.FullSong] = new RecognitionResult
        {
            Mode = RecognitionMode.FullSong,
            Notes = rawNotes.ToList(),
            MelodyScore = 100,
            Name = "完整多音",
            Summary = $"保留全部声部与和弦伴奏 ({rawNotes.Count:N0} 音符)"
        };

        // 2. Mode A: Track Priority
        dict[RecognitionMode.TrackPriority] = TrackPriorityMode.Extract(rawNotes, tracks);

        // 3. Mode B: Highest Note
        dict[RecognitionMode.HighestNote] = HighestNoteMode.Extract(rawNotes);

        // 4. Mode C: Continuous Melody DP ★
        dict[RecognitionMode.ContinuousMelody] = ContinuousMelodyMode.Extract(rawNotes, tracks);

        // 5. Mode D: Monophonic Priority
        dict[RecognitionMode.Monophonic] = MonophonicMode.Extract(rawNotes, tracks);

        // 6. Mode E: AutoLeadFusion (多轨主旋律融合与画像)
        dict[RecognitionMode.AutoLeadFusion] = AutoLeadFusionMode.Extract(rawNotes, tracks);

        return dict;
    }
}
