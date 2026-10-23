using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Recognition;

public sealed class MelodyBiasSettings
{
    // 是否启用固定音高中心偏好
    public bool UsePitchCenterBias { get; set; } = false;

    // 是否启用旋律连续性奖励 (默认关闭，杜绝过度偏向级进/三度，保全大跳与琶音原曲主旋律)
    public bool UseTransitionBias { get; set; } = false;

    // 仅在启用固定音高偏好时使用
    public int PitchCenter { get; set; } = 67;

    public double PitchCenterWeight { get; set; } = 1.2;
}

public static class ContinuousMelodyMode
{
    public static double CalculateDurationScore(RawMidiNote note)
    {
        double durationMs = note.DurationUs / 1000.0;
        return Math.Min(20.0, durationMs * 0.08);
    }

    public static double CalculateVelocityScore(RawMidiNote note)
    {
        return (note.Velocity / 127.0) * 8.0;
    }

    public static double CalculateTrackScore(RawMidiNote note, IReadOnlyDictionary<int, string>? trackNames = null)
    {
        if (trackNames != null && trackNames.TryGetValue(note.TrackId, out var tName))
        {
            if (tName.Contains("melody") || tName.Contains("vocal") || tName.Contains("lead") || tName.Contains("主旋律"))
                return 20.0;
        }
        return 0.0;
    }

    public static RecognitionResult Extract(
        IReadOnlyList<RawMidiNote> rawNotes,
        IReadOnlyList<TrackMetadata>? tracks,
        MelodyBiasSettings? settings = null)
    {
        settings ??= new MelodyBiasSettings();
        var nonDrumNotes = rawNotes.Where(n => n.Channel != 9).OrderBy(n => n.StartUs).ToList();
        if (nonDrumNotes.Select(note => note.TrackId).Distinct().Take(2).Count() <= 1)
        {
            var extracted = PolyphonicMelodyExtractor.ExtractSingleTrackMelody(nonDrumNotes);
            double singleTrackStepRatio = extracted.Count > 1
                ? extracted.Zip(extracted.Skip(1), (a, b) => Math.Abs(a.NoteNumber - b.NoteNumber) <= 4).Count(isStep => isStep) / (double)(extracted.Count - 1)
                : extracted.Count == 1 ? 1.0 : 0.0;
            return new RecognitionResult
            {
                Mode = RecognitionMode.ContinuousMelody,
                Notes = extracted,
                MelodyScore = extracted.Count == 0 ? 0 : Math.Clamp((int)(singleTrackStepRatio * 40 + 55), 60, 98),
                Name = "连续旋律 ★",
                Summary = extracted.Count == 0 ? "未找到有效单轨旋律音符" : $"复音旋律路径 ({extracted.Count}音符, 级进平滑度{(int)(singleTrackStepRatio * 100)}%)"
            };
        }
        if (nonDrumNotes.Count == 0)
        {
            return new RecognitionResult
            {
                Mode = RecognitionMode.ContinuousMelody,
                Notes = new List<RawMidiNote>(),
                MelodyScore = 0,
                Name = "连续旋律 ★",
                Summary = "未找到有效非打击乐音符"
            };
        }

        // 1. Group into time slices (tolerance 20ms)
        var slices = new List<List<RawMidiNote>>();
        List<RawMidiNote>? currentSlice = null;
        long sliceStartUs = -1;

        foreach (var note in nonDrumNotes)
        {
            if (currentSlice == null || note.StartUs - sliceStartUs > 20_000)
            {
                currentSlice = new List<RawMidiNote>();
                slices.Add(currentSlice);
                sliceStartUs = note.StartUs;
            }
            currentSlice.Add(note);
        }

        if (slices.Count == 0)
        {
            return new RecognitionResult
            {
                Mode = RecognitionMode.ContinuousMelody,
                Notes = new List<RawMidiNote>(),
                MelodyScore = 50,
                Name = "连续旋律 ★",
                Summary = "空曲目"
            };
        }

        // Limit candidate count per slice to top 4 (by pitch/duration) to keep DP lightning fast (<5ms)
        var filteredSlices = new List<List<RawMidiNote>>(slices.Count);
        foreach (var s in slices)
        {
            if (s.Count <= 4)
            {
                filteredSlices.Add(s);
            }
            else
            {
                // Prioritize top pitches and longer durations
                var top = s.OrderByDescending(n => n.NoteNumber * 2 + (n.DurationUs > 50_000 ? 20 : 0)).Take(4).ToList();
                filteredSlices.Add(top);
            }
        }

        var trackNameDict = tracks?.ToDictionary(t => t.TrackId, t => t.Name.ToLowerInvariant()) ?? new Dictionary<int, string>();

        // 2. Local Score calculation helper
        double GetLocalScore(RawMidiNote note)
        {
            double score = 0;

            // 固定音高偏好：可关闭
            if (settings.UsePitchCenterBias)
            {
                score -= settings.PitchCenterWeight
                         * Math.Abs(note.Pitch - settings.PitchCenter);
            }

            // 其他与音高中心无关的特征继续独立评分
            score += CalculateDurationScore(note);
            score += CalculateVelocityScore(note);
            score += CalculateTrackScore(note, trackNameDict);
            // 基础高音声部权重 (在并发和弦切片中，高音声部为主旋律的概率最高)
            score += (note.NoteNumber / 127.0) * 12.0;

            return score;
        }

        // 3. Transition Score calculation helper
        double GetTransitionScore(RawMidiNote prev, RawMidiNote curr)
        {
            if (!settings.UseTransitionBias)
            {
                return 0.0;
            }

            long timeGapUs = curr.StartUs - (prev.StartUs + prev.DurationUs);

            // Long rest / phrase pause (> 1.2s): do not penalize pitch leaps across phrase boundaries
            if (timeGapUs > 1_200_000)
            {
                return 0.0;
            }

            int pitchDelta = Math.Abs(curr.NoteNumber - prev.NoteNumber);

            return pitchDelta switch
            {
                0 => 18.0,                       // Repeated note
                1 or 2 => 32.0,                  // Stepwise motion (whole/half step) -> peak melody reward!
                3 or 4 => 22.0,                  // Minor/Major third
                5 or 7 => 12.0,                  // Perfect fourth / fifth
                >= 8 and <= 12 => 0.0,           // Sixth to octave
                _ => -4.0 * (pitchDelta - 12)    // Penalize extreme leaps
            };
        }

        // 4. Dynamic Programming (Viterbi style)
        int sliceCount = filteredSlices.Count;
        var dp = new double[sliceCount][];
        var parent = new int[sliceCount][];

        // Initialize slice 0
        dp[0] = new double[filteredSlices[0].Count];
        parent[0] = new int[filteredSlices[0].Count];
        for (int j = 0; j < filteredSlices[0].Count; j++)
        {
            dp[0][j] = GetLocalScore(filteredSlices[0][j]);
            parent[0][j] = -1;
        }

        // Forward pass
        for (int t = 1; t < sliceCount; t++)
        {
            var prevCandidates = filteredSlices[t - 1];
            var currCandidates = filteredSlices[t];

            dp[t] = new double[currCandidates.Count];
            parent[t] = new int[currCandidates.Count];

            for (int j = 0; j < currCandidates.Count; j++)
            {
                double bestVal = double.NegativeInfinity;
                int bestIdx = 0;
                double local = GetLocalScore(currCandidates[j]);

                for (int i = 0; i < prevCandidates.Count; i++)
                {
                    double trans = GetTransitionScore(prevCandidates[i], currCandidates[j]);
                    double val = dp[t - 1][i] + trans + local;
                    if (val > bestVal)
                    {
                        bestVal = val;
                        bestIdx = i;
                    }
                }

                dp[t][j] = bestVal;
                parent[t][j] = bestIdx;
            }
        }

        // Find best end index
        int bestEndIdx = 0;
        double maxFinalScore = double.NegativeInfinity;
        for (int j = 0; j < dp[sliceCount - 1].Length; j++)
        {
            if (dp[sliceCount - 1][j] > maxFinalScore)
            {
                maxFinalScore = dp[sliceCount - 1][j];
                bestEndIdx = j;
            }
        }

        // 5. Backtrack
        var path = new List<RawMidiNote>(sliceCount);
        int currentIdx = bestEndIdx;
        for (int t = sliceCount - 1; t >= 0; t--)
        {
            path.Add(filteredSlices[t][currentIdx]);
            currentIdx = parent[t][currentIdx];
        }
        path.Reverse();

        // 6. Calculate melody quality score (0..100)
        int stepMoves = 0;
        for (int i = 1; i < path.Count; i++)
        {
            if (Math.Abs(path[i].NoteNumber - path[i - 1].NoteNumber) <= 4)
            {
                stepMoves++;
            }
        }
        double stepRatio = path.Count > 1 ? (double)stepMoves / (path.Count - 1) : 1.0;
        int melodyScore = Math.Clamp((int)(stepRatio * 40 + 55), 60, 98);

        return new RecognitionResult
        {
            Mode = RecognitionMode.ContinuousMelody,
            Notes = path,
            MelodyScore = melodyScore,
            Name = "连续旋律 ★",
            Summary = $"最优连贯路径 ({path.Count}音符, 级进平滑度{(int)(stepRatio * 100)}%)"
        };
    }
}

