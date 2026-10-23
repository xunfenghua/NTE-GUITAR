using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Recognition;

/// <summary>
/// 实验性主旋律提取模式: AutoLeadFusion (多轨主旋律融合与轨道画像)
/// 核心原则:
/// 1. 先画像，再提取: 计算每轨音域、密度、复音率与乐器先验；
/// 2. 候选构建含显式 REST (休止状态)，避免静音期强塞伴奏音；
/// 3. 两阶段 Viterbi DP: 综合局部显著度与转移连续性，抑制跨轨频繁跳跃；
/// 4. 输出可解释的候选决策 (Confidence 与 ReasonCode)。
/// </summary>
public static class AutoLeadFusionMode
{
    public static RecognitionResult Extract(
        IReadOnlyList<RawMidiNote> rawNotes,
        IReadOnlyList<TrackMetadata>? tracks)
    {
        var nonDrum = rawNotes.Where(n => n.Channel != 9).OrderBy(n => n.StartUs).ToList();
        if (nonDrum.Count == 0)
        {
            return new RecognitionResult
            {
                Mode = RecognitionMode.AutoLeadFusion,
                Notes = new List<RawMidiNote>(),
                MelodyScore = 0,
                Name = "多轨融合 ★",
                Summary = "未找到有效非打击乐音符"
            };
        }

        // 1. 轨道画像 (Track Profiling)
        var profiles = ComputeTrackProfiles(nonDrum, tracks);

        // 挑选前 K 条高质量候选音轨 (优先选择具有主旋律特征的轨道)
        var candidateTrackIds = new HashSet<int>(
            profiles.OrderByDescending(p => p.PriorScore)
                    .Take(Math.Min(5, profiles.Count))
                    .Select(p => p.TrackId));

        // 确保如果有明确标注为 Lead/Vocal/Melody 的轨道，必然入选候选
        foreach (var p in profiles)
        {
            if (p.PriorScore >= 25.0)
            {
                candidateTrackIds.Add(p.TrackId);
            }
        }

        var candidateNotes = nonDrum.Where(n => candidateTrackIds.Contains(n.TrackId)).ToList();
        if (candidateNotes.Count == 0) candidateNotes = nonDrum;

        // 2. 按时间窗口分组 (25ms 聚类)
        var timeGroups = new List<List<RawMidiNote>>();
        List<RawMidiNote>? currentGroup = null;
        long groupStartUs = -1;

        foreach (var note in candidateNotes)
        {
            if (currentGroup == null || note.StartUs - groupStartUs > 25_000)
            {
                currentGroup = new List<RawMidiNote>();
                timeGroups.Add(currentGroup);
                groupStartUs = note.StartUs;
            }
            currentGroup.Add(note);
        }

        // 3. 构建候选状态集 (每个时间组保留 Top-3 音符 + 1 个显式 REST 状态)
        var profileMap = profiles.ToDictionary(p => p.TrackId, p => p);
        var stateGrid = new List<List<MelodyCandidate?>>(timeGroups.Count);

        for (int g = 0; g < timeGroups.Count; g++)
        {
            var grp = timeGroups[g];
            var topNotes = grp.OrderByDescending(n =>
            {
                double prior = profileMap.TryGetValue(n.TrackId, out var tp) ? tp.PriorScore : 0.0;
                double pitchScore = 30.0 - Math.Abs(n.NoteNumber - 69) * 0.8;
                return prior + pitchScore + (n.Velocity / 127.0) * 10.0;
            }).Take(3).ToList();

            var list = new List<MelodyCandidate?>(topNotes.Count + 1);
            foreach (var n in topNotes)
            {
                list.Add(new MelodyCandidate(
                    n.StartUs,
                    n.DurationUs,
                    n.NoteNumber,
                    n.Velocity,
                    n.TrackId,
                    n.Channel,
                    n.Program
                ));
            }
            // 显式加入 REST (休止) 候选状态
            list.Add(null);
            stateGrid.Add(list);
        }

        // 4. Viterbi 动态规划求解全局最优主旋律路径
        int nSteps = stateGrid.Count;
        var dp = new double[nSteps][];
        var parent = new int[nSteps][];
        var reasonCodes = new string[nSteps][];

        // Step 0 初始化
        dp[0] = new double[stateGrid[0].Count];
        parent[0] = new int[stateGrid[0].Count];
        reasonCodes[0] = new string[stateGrid[0].Count];

        for (int c = 0; c < stateGrid[0].Count; c++)
        {
            var cand = stateGrid[0][c];
            if (cand == null)
            {
                dp[0][c] = -5.0; // REST 轻微起始惩罚
                reasonCodes[0][c] = "INIT_REST";
            }
            else
            {
                dp[0][c] = ComputeLocalScore(cand, profileMap);
                reasonCodes[0][c] = "INIT_NOTE";
            }
            parent[0][c] = -1;
        }

        // Steps 1 .. nSteps - 1
        for (int i = 1; i < nSteps; i++)
        {
            var curStates = stateGrid[i];
            var prevStates = stateGrid[i - 1];

            dp[i] = new double[curStates.Count];
            parent[i] = new int[curStates.Count];
            reasonCodes[i] = new string[curStates.Count];

            for (int c = 0; c < curStates.Count; c++)
            {
                var cur = curStates[c];
                double localScore = (cur != null) ? ComputeLocalScore(cur, profileMap) : 0.0;
                double maxVal = double.NegativeInfinity;
                int bestPrev = 0;
                string bestReason = (cur == null) ? "REST" : "CONTINUITY";

                for (int p = 0; p < prevStates.Count; p++)
                {
                    double prevDp = dp[i - 1][p];
                    if (double.IsNegativeInfinity(prevDp)) continue;

                    var prev = prevStates[p];
                    double transScore = ComputeTransitionScore(prev, cur);
                    double total = prevDp + transScore + localScore;

                    if (total > maxVal)
                    {
                        maxVal = total;
                        bestPrev = p;
                        if (cur != null && prev != null && cur.TrackIndex == prev.TrackIndex)
                        {
                            bestReason = "SAME_TRACK_CONTINUITY";
                        }
                        else if (cur != null && prev != null && cur.TrackIndex != prev.TrackIndex)
                        {
                            bestReason = "TRACK_HOP";
                        }
                    }
                }

                dp[i][c] = maxVal;
                parent[i][c] = bestPrev;
                reasonCodes[i][c] = bestReason;
            }
        }

        // 寻找终点最优状态
        double bestFinalScore = double.NegativeInfinity;
        int bestLast = 0;
        for (int c = 0; c < stateGrid[nSteps - 1].Count; c++)
        {
            if (dp[nSteps - 1][c] > bestFinalScore)
            {
                bestFinalScore = dp[nSteps - 1][c];
                bestLast = c;
            }
        }

        // 回溯获得路径
        var decisions = new MelodyDecision[nSteps];
        int currIdx = bestLast;
        for (int i = nSteps - 1; i >= 0; i--)
        {
            var cand = stateGrid[i][currIdx];
            double conf = Math.Clamp(dp[i][currIdx] / Math.Max(1.0, (i + 1) * 30.0), 0.2, 0.98);
            decisions[i] = new MelodyDecision(cand, conf, reasonCodes[i][currIdx]);
            currIdx = parent[i][currIdx];
            if (currIdx < 0 && i > 0) currIdx = 0;
        }

        // 提取最终选定的非 REST 音符
        var resultNotes = new List<RawMidiNote>();
        foreach (var d in decisions)
        {
            if (d.Selected != null)
            {
                resultNotes.Add(new RawMidiNote
                {
                    StartUs = d.Selected.StartUs,
                    DurationUs = d.Selected.DurationUs,
                    NoteNumber = (byte)d.Selected.Pitch,
                    Velocity = (byte)d.Selected.Velocity,
                    Channel = (byte)d.Selected.Channel,
                    TrackId = d.Selected.TrackIndex,
                    Program = d.Selected.Program
                });
            }
        }

        // 综合评分计算
        int melodyScore = 80;
        if (resultNotes.Count > 0)
        {
            double avgConf = decisions.Average(d => d.Confidence);
            melodyScore = Math.Clamp((int)(avgConf * 100), 50, 99);
        }

        return new RecognitionResult
        {
            Mode = RecognitionMode.AutoLeadFusion,
            Notes = resultNotes,
            MelodyScore = melodyScore,
            Name = "多轨融合 ★",
            Summary = $"融合 {profiles.Count} 轨主旋律特征，智能识别休止与连音 ({resultNotes.Count} 音符)",
            Decisions = decisions.ToList(),
            Profiles = profiles
        };
    }

    private static List<TrackProfile> ComputeTrackProfiles(
        IReadOnlyList<RawMidiNote> notes,
        IReadOnlyList<TrackMetadata>? tracks)
    {
        var metaMap = tracks?.ToDictionary(t => t.TrackId, t => t) ?? new Dictionary<int, TrackMetadata>();
        var grouped = notes.GroupBy(n => n.TrackId);
        var list = new List<TrackProfile>();

        foreach (var g in grouped)
        {
            var trackNotes = g.OrderBy(n => n.StartUs).ToList();
            int count = trackNotes.Count;
            if (count == 0) continue;

            int minPitch = trackNotes.Min(n => n.NoteNumber);
            int maxPitch = trackNotes.Max(n => n.NoteNumber);
            long spanUs = Math.Max(1_000_000, trackNotes[^1].StartUs - trackNotes[0].StartUs);
            double density = count / (spanUs / 1_000_000.0);

            // 复音重叠率计算
            int overlaps = 0;
            for (int i = 0; i < count - 1; i++)
            {
                long endUs = trackNotes[i].StartUs + trackNotes[i].DurationUs;
                if (trackNotes[i + 1].StartUs < endUs)
                {
                    overlaps++;
                }
            }
            double polyRate = (double)overlaps / Math.Max(1, count - 1);
            double avgDurMs = trackNotes.Average(n => n.DurationUs) / 1000.0;

            string trackName = metaMap.TryGetValue(g.Key, out var meta) ? meta.Name : $"Track {g.Key}";
            int program = meta?.Program ?? trackNotes[0].Program;

            // 先验评分计算
            double prior = 10.0;
            string lowerName = trackName.ToLowerInvariant();
            if (lowerName.Contains("vocal") || lowerName.Contains("melody") || lowerName.Contains("lead") || lowerName.Contains("主旋律"))
                prior += 30.0;
            if (lowerName.Contains("solo") || lowerName.Contains("flute") || lowerName.Contains("violin") || lowerName.Contains("sax"))
                prior += 20.0;
            if (lowerName.Contains("bass") || lowerName.Contains("drum") || lowerName.Contains("perc"))
                prior -= 30.0;
            if (lowerName.Contains("chord") || lowerName.Contains("pad") || lowerName.Contains("strum"))
                prior -= 15.0;

            // GM Program 先验
            if (program is >= 72 and <= 79) prior += 15.0; // Pipe / Flute
            else if (program is >= 80 and <= 87) prior += 15.0; // Synth Lead
            else if (program is >= 40 and <= 43) prior += 12.0; // Solo Strings
            else if (program is >= 32 and <= 39) prior -= 25.0; // Bass

            // 音域适中加分 (E3~E5: 52~76)
            if (minPitch >= 48 && maxPitch <= 90) prior += 10.0;
            if (polyRate < 0.25) prior += 12.0; // 单音性好，更具旋律性
            if (polyRate > 0.70) prior -= 15.0; // 伴奏和弦高复音

            list.Add(new TrackProfile(
                g.Key,
                trackName,
                count,
                minPitch,
                maxPitch,
                density,
                polyRate,
                avgDurMs,
                program,
                prior
            ));
        }

        return list;
    }

    private static double ComputeLocalScore(MelodyCandidate note, Dictionary<int, TrackProfile> profileMap)
    {
        double score = 0.0;

        // 1. 轨道画像先验分
        if (profileMap.TryGetValue(note.TrackIndex, out var prof))
        {
            score += prof.PriorScore * 0.8;
            if (prof.PolyphonyRate > 0.6) score -= 12.0;
        }

        // 2. 音域适中评分: 以 A4 (69) 为甜蜜区
        score += 25.0 - Math.Abs(note.Pitch - 69) * 0.7;
        if (note.Pitch < 48) score -= 30.0; // 重罚超低伴奏音
        if (note.Pitch > 92) score -= 15.0; // 高频刺耳装饰音

        // 3. 力度与时值权重
        score += (note.Velocity / 127.0) * 8.0;
        double durMs = note.DurationUs / 1000.0;
        score += Math.Min(15.0, durMs * 0.05);

        return score;
    }

    private static double ComputeTransitionScore(MelodyCandidate? prev, MelodyCandidate? cur)
    {
        // 1. 涉及休止状态的转移
        if (prev == null && cur == null)
        {
            return -2.0; // 连续休止，轻微成本
        }
        if (prev == null || cur == null)
        {
            return 0.0; // 进出休止，中性
        }

        long timeGapUs = cur.StartUs - (prev.StartUs + prev.DurationUs);

        // 长停顿 (> 1.2s) 跨乐句不严罚音程跳跃
        if (timeGapUs > 1_200_000)
        {
            return (cur.TrackIndex == prev.TrackIndex) ? 2.0 : -4.0;
        }

        double score = 0.0;

        // 2. 音程跳跃评分
        int interval = Math.Abs(cur.Pitch - prev.Pitch);
        if (interval == 0) score += 3.0;        // 同音保持
        else if (interval <= 2) score += 5.0;   // 级进二度 (最典型的优美旋律走向)
        else if (interval <= 4) score += 3.5;   // 三度跳进
        else if (interval <= 5) score += 2.0;   // 四度
        else if (interval <= 7) score += 0.5;   // 五度
        else if (interval == 12) score += 1.5;  // 八度自然跳跃
        else score -= (interval - 7) * 1.5;     // 大跳惩罚

        // 3. 跨轨切换成本 (Track-hop Penalty: 避免在不同轨道间逐音反复乒乓跳跃)
        if (cur.TrackIndex != prev.TrackIndex)
        {
            score -= 16.0;
        }
        else
        {
            score += 4.0; // 同轨连续奖励
        }

        // 4. 重叠冲突惩罚
        if (cur.StartUs < prev.StartUs + Math.Min(prev.DurationUs, 40_000))
        {
            score -= 10.0;
        }

        return score;
    }
}
