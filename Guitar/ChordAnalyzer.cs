using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Guitar;

/// <summary>
/// 游戏四页 24 和弦元数据定义
/// </summary>
public sealed class ChordDefinition
{
    public ChordId Id { get; }
    public string Name { get; }
    public byte Page { get; } // 1..4
    public byte Slot { get; } // 1..6
    public byte RootPitchClass { get; } // 0..11
    public HashSet<byte> PitchClasses { get; }
    public byte? SpecialBassPitchClass { get; } // 用于 Slash Chord (G/B, Am7/G, D/F#, F/A)

    public ChordDefinition(
        ChordId id,
        string name,
        byte page,
        byte slot,
        byte root,
        IEnumerable<byte> tones,
        byte? specialBass = null)
    {
        Id = id;
        Name = name;
        Page = page;
        Slot = slot;
        RootPitchClass = root;
        PitchClasses = new HashSet<byte>(tones);
        SpecialBassPitchClass = specialBass;
    }

    public override string ToString() => $"[P{Page} Slot{Slot}] {Name}";
}

public sealed class ChordAnalyzerOptions
{
    public ChordRecognitionMode Mode { get; set; } = ChordRecognitionMode.Loose;
    public long AttackClusterUs { get; set; } = 80_000;    // 攻击聚类窗口 80ms (适合攻击形态聚类)
    public long AnalysisWindowUs { get; set; } = 450_000;  // 观察分析窗口 450ms
    public double HysteresisMargin { get; set; } = 0.12;   // 迟滞防抖超额阈值
    public double MinHoldScore { get; set; } = 0.25;        // 最低保持阈值
    public long MinChordStabilityUs { get; set; } = 180_000; // 最小和弦稳定区 180ms (150~250ms 防止相近和弦高频抖动)
}

/// <summary>
/// 24 和弦分析器: 支持游戏全部 4 页 24 和弦识别、Slash Chord 低音判定与防抖 (v4.3 升级)
/// </summary>
public static class ChordAnalyzer
{
    /// <summary>
    /// 游戏真实存在的 24 个和弦目录 (4页，每页 6 槽位，完全对齐游戏截图)
    /// </summary>
    public static readonly IReadOnlyList<ChordDefinition> SupportedChords = new ChordDefinition[]
    {
        // ================= Page 1 =================
        new(ChordId.G,        "G",     1, 1, 7,  new byte[] { 7, 11, 2 }),          // Slot 1
        new(ChordId.Am,       "Am",    1, 2, 9,  new byte[] { 9, 0, 4 }),           // Slot 2
        new(ChordId.Bm,       "Bm",    1, 3, 11, new byte[] { 11, 2, 6 }),          // Slot 3
        new(ChordId.C,        "C",     1, 4, 0,  new byte[] { 0, 4, 7 }),           // Slot 4
        new(ChordId.D,        "D",     1, 5, 2,  new byte[] { 2, 6, 9 }),           // Slot 5
        new(ChordId.Em,       "Em",    1, 6, 4,  new byte[] { 4, 7, 11 }),          // Slot 6

        // ================= Page 2 =================
        new(ChordId.Cmaj7,    "Cmaj7", 2, 1, 0,  new byte[] { 0, 4, 7, 11 }),       // Slot 1
        new(ChordId.C7,       "C7",    2, 2, 0,  new byte[] { 0, 4, 7, 10 }),       // Slot 2
        new(ChordId.Cm7,      "Cm7",   2, 3, 0,  new byte[] { 0, 3, 7, 10 }),       // Slot 3
        new(ChordId.G7,       "G7",    2, 4, 7,  new byte[] { 7, 11, 2, 5 }),       // Slot 4
        new(ChordId.C9,       "C9",    2, 5, 0,  new byte[] { 0, 4, 7, 10, 2 }),    // Slot 5
        new(ChordId.Cm9,      "Cm9",   2, 6, 0,  new byte[] { 0, 3, 7, 10, 2 }),    // Slot 6

        // ================= Page 3 =================
        new(ChordId.C5,       "C5",    3, 1, 0,  new byte[] { 0, 7 }),              // Slot 1
        new(ChordId.D5,       "D5",    3, 2, 2,  new byte[] { 2, 9 }),              // Slot 2
        new(ChordId.G5,       "G5",    3, 3, 7,  new byte[] { 7, 2 }),              // Slot 3
        new(ChordId.A5,       "A5",    3, 4, 9,  new byte[] { 9, 4 }),              // Slot 4
        new(ChordId.Cmaj9,    "Cmaj9", 3, 5, 0,  new byte[] { 0, 4, 7, 11, 2 }),    // Slot 5
        new(ChordId.Cadd9,    "Cadd9", 3, 6, 0,  new byte[] { 0, 4, 7, 2 }),        // Slot 6

        // ================= Page 4 =================
        new(ChordId.G_B,      "G/B",   4, 1, 7,  new byte[] { 7, 11, 2 }, specialBass: 11), // Slot 1: B 低音
        new(ChordId.Am7_G,    "Am7/G", 4, 2, 9,  new byte[] { 9, 0, 4, 7 }, specialBass: 7), // Slot 2: G 低音
        new(ChordId.D_Fsharp, "D/F#",  4, 3, 2,  new byte[] { 2, 6, 9 }, specialBass: 6),   // Slot 3: F# 低音
        new(ChordId.F_A,      "F/A",   4, 4, 5,  new byte[] { 5, 9, 0 }, specialBass: 9),   // Slot 4: A 低音
        new(ChordId.A,        "A",     4, 5, 9,  new byte[] { 9, 1, 4 }),           // Slot 5
        new(ChordId.Dm,       "Dm",    4, 6, 2,  new byte[] { 2, 5, 9 })            // Slot 6
    };

    private static readonly Dictionary<ChordId, ChordDefinition> _chordMap =
        SupportedChords.ToDictionary(c => c.Id);

    public static ChordDefinition? GetDefinition(ChordId id) =>
        _chordMap.TryGetValue(id, out var def) ? def : null;

    /// <summary>
    /// 分析 MIDI 音符并生成和弦切换时间线 (兼容旧版输出)
    /// </summary>
    public static List<ChordChangeEvent> Analyze(
        IReadOnlyList<RawMidiNote> rawNotes,
        ChordAnalyzerOptions? options = null)
    {
        var detailed = AnalyzeDetailed(rawNotes, options);
        var events = new List<ChordChangeEvent>();
        ChordId current = ChordId.Unknown;

        foreach (var ev in detailed)
        {
            if (ev.Chord != current && ev.Chord != ChordId.Unknown)
            {
                current = ev.Chord;
                events.Add(new ChordChangeEvent(ev.StartUs, ev.Chord, (float)ev.Confidence, ev.Page, ev.Slot));
            }
        }

        return events;
    }

    /// <summary>
    /// 分析 MIDI 和声流并生成保留源音符与演奏形态的详细和弦事件列表 (方案 6.2 & 6.3)
    /// </summary>
    public static List<ChordEvent> AnalyzeDetailed(
        IReadOnlyList<RawMidiNote> rawNotes,
        ChordAnalyzerOptions? options = null)
    {
        if (rawNotes == null || rawNotes.Count == 0)
            return new List<ChordEvent>();

        options ??= new ChordAnalyzerOptions();

        // 过滤打击乐通道
        var notes = rawNotes.Where(n => n.Channel != 9).OrderBy(n => n.StartUs).ToList();
        if (notes.Count == 0) return new List<ChordEvent>();

        // 1. 攻击形态聚类
        var attackClusters = ClusterAttacks(notes, options.AttackClusterUs);

        var events = new List<ChordEvent>();
        ChordId currentChord = ChordId.Unknown;
        ChordId consecutiveCandidate = ChordId.Unknown;
        int consecutiveCount = 0;
        long lastSwitchTimeUs = -options.MinChordStabilityUs;

        foreach (var cluster in attackClusters)
        {
            long attackTime = cluster[0].StartUs;
            long winEnd = attackTime + options.AnalysisWindowUs;

            // 统计分析窗口内持续发声的活跃音符 (Active Notes)
            double[] evidence = new double[12];
            double totalEvidence = 0;
            int lowestMidiNote = int.MaxValue;
            byte lowestBassPc = 255;
            var activeNotes = new List<RawMidiNote>();

            foreach (var note in notes)
            {
                long noteEnd = note.StartUs + (note.DurationUs > 0 ? note.DurationUs : 300_000);
                if (note.StartUs >= winEnd || noteEnd <= attackTime) continue;

                long overlap = Math.Min(noteEnd, winEnd) - Math.Max(note.StartUs, attackTime);
                if (overlap <= 0) continue;

                activeNotes.Add(note);
                double weight = (overlap / (double)options.AnalysisWindowUs) * (note.Velocity / 127.0);
                byte pc = (byte)(note.NoteNumber % 12);
                evidence[pc] += weight;
                totalEvidence += weight;

                if (note.NoteNumber < lowestMidiNote)
                {
                    lowestMidiNote = note.NoteNumber;
                    lowestBassPc = pc;
                }
            }

            if (totalEvidence < 0.05) continue;

            // 2. 在全部 24 个和弦模板中评分并选择最优和弦
            var (bestChord, bestScore, currentScore) = ScoreAll24Chords(
                evidence,
                totalEvidence,
                lowestBassPc,
                currentChord,
                options.Mode);

            // 3. 稳定区与迟滞防抖逻辑 (Hysteresis + Stability Window)
            bool shouldSwitch = false;
            long timeSinceLastSwitch = attackTime - lastSwitchTimeUs;

            if (currentChord == ChordId.Unknown)
            {
                if (bestScore >= 0.35) shouldSwitch = true;
            }
            else
            {
                if (bestChord == currentChord)
                {
                    consecutiveCandidate = ChordId.Unknown;
                    consecutiveCount = 0;
                }
                else
                {
                    // 必须满足最小稳定区 (150~250ms) 或者是极显著和弦变更
                    if (timeSinceLastSwitch >= options.MinChordStabilityUs || bestScore >= (currentScore + 0.35))
                    {
                        if (bestScore >= currentScore + options.HysteresisMargin)
                        {
                            shouldSwitch = true;
                        }
                        else if (currentScore < options.MinHoldScore && bestScore >= 0.35)
                        {
                            shouldSwitch = true;
                        }
                        else
                        {
                            if (bestChord == consecutiveCandidate)
                            {
                                consecutiveCount++;
                                if (consecutiveCount >= 2 && bestScore >= 0.35) shouldSwitch = true;
                            }
                            else
                            {
                                consecutiveCandidate = bestChord;
                                consecutiveCount = 1;
                            }
                        }
                    }
                }
            }

            if (shouldSwitch && bestChord != ChordId.Unknown && bestChord != currentChord)
            {
                currentChord = bestChord;
                lastSwitchTimeUs = attackTime;
                consecutiveCandidate = ChordId.Unknown;
                consecutiveCount = 0;
            }

            // 4. 判定攻击演奏形态 (Attack Articulation: Block / Strum / Arpeggio)
            var (articulation, dir) = DetectAttackArticulation(cluster);

            var activeId = (currentChord != ChordId.Unknown) ? currentChord : bestChord;
            var def = GetDefinition(activeId);
            byte page = def?.Page ?? 1;
            byte slot = def?.Slot ?? 1;

            var ev = new ChordEvent
            {
                StartUs = attackTime,
                EndUs = winEnd,
                Chord = activeId,
                Page = page,
                Slot = slot,
                Confidence = Math.Clamp(bestScore, 0.0, 1.0),
                SourceNotes = activeNotes,
                AttackNotes = cluster.ToList(),
                Articulation = articulation,
                Direction = dir
            };

            events.Add(ev);
        }

        return events;
    }

    private static List<List<RawMidiNote>> ClusterAttacks(List<RawMidiNote> notes, long clusterUs)
    {
        var clusters = new List<List<RawMidiNote>>();
        if (notes.Count == 0) return clusters;

        var cur = new List<RawMidiNote> { notes[0] };
        long start = notes[0].StartUs;

        for (int i = 1; i < notes.Count; i++)
        {
            if (notes[i].StartUs - start <= clusterUs)
            {
                cur.Add(notes[i]);
            }
            else
            {
                clusters.Add(cur);
                cur = new List<RawMidiNote> { notes[i] };
                start = notes[i].StartUs;
            }
        }

        if (cur.Count > 0) clusters.Add(cur);
        return clusters;
    }

    /// <summary>
    /// 24 和弦评分公式 (文档 6.2)
    /// score = 0.58 * chordToneCoverage + 0.30 * expectedToneCompleteness + rootPresenceBonus + bassMatchBonus - extraTonePenalty - missingTonePenalty
    /// </summary>
    public static (ChordId BestChord, double BestScore, double CurrentScore) ScoreAll24Chords(
        double[] evidence,
        double totalEvidence,
        byte lowestBassPc,
        ChordId currentChord,
        ChordRecognitionMode mode)
    {
        ChordId bestChord = ChordId.Unknown;
        double bestScore = double.MinValue;
        double currentChordScore = 0;

        foreach (var def in SupportedChords)
        {
            double toneEvidence = 0;
            double nonToneEvidence = 0;
            int tonesPresent = 0;

            for (byte pc = 0; pc < 12; pc++)
            {
                if (def.PitchClasses.Contains(pc))
                {
                    toneEvidence += evidence[pc];
                    if (evidence[pc] > 0.02) tonesPresent++;
                }
                else
                {
                    nonToneEvidence += evidence[pc];
                }
            }

            double chordToneCoverage = toneEvidence / Math.Max(0.001, totalEvidence);
            double expectedToneCompleteness = tonesPresent / (double)def.PitchClasses.Count;

            // 根音奖励
            double rootBonus = (evidence[def.RootPitchClass] > 0.02) ? 0.12 : 0.0;

            // 特殊低音 (Slash Chord) 与常规低音判定
            double bassBonus = 0;
            if (def.SpecialBassPitchClass.HasValue)
            {
                if (lowestBassPc == def.SpecialBassPitchClass.Value)
                {
                    bassBonus = 0.40; // 斜杠和弦低音精确匹配
                }
                else
                {
                    bassBonus = -0.55; // 低音不符时强烈惩罚，防止普通和弦误判为斜杠和弦
                }
            }
            else
            {
                if (lowestBassPc == def.RootPitchClass)
                {
                    bassBonus = 0.18; // 普通和弦根音为低音
                }
            }

            // 非和弦音与缺失音惩罚
            double extraPenalty = (mode == ChordRecognitionMode.Strict) ? 0.45 * (nonToneEvidence / Math.Max(0.001, totalEvidence))
                                                                       : 0.25 * (nonToneEvidence / Math.Max(0.001, totalEvidence));
            double missingPenalty = 0.15 * (1.0 - expectedToneCompleteness);

            double score = 0.58 * chordToneCoverage
                         + 0.30 * expectedToneCompleteness
                         + rootBonus
                         + bassBonus
                         - extraPenalty
                         - missingPenalty;

            // 历史连续性奖励
            if (def.Id == currentChord)
            {
                score += 0.15;
                currentChordScore = score;
            }

            if (score > bestScore)
            {
                bestScore = score;
                bestChord = def.Id;
            }
        }

        return (bestChord, bestScore, currentChordScore);
    }

    /// <summary>
    /// 获取 24 个和弦各弦的发音音高 (1=最高音弦 E4, 6=最低音弦 E2)
    /// </summary>
    public static byte[] GetChordStringPitches(ChordId chord) => chord switch
    {
        // Page 1
        ChordId.G        => new byte[] { 67, 59, 55, 50, 47, 43 }, // G4, B3, G3, D3, B2, G2
        ChordId.Am       => new byte[] { 64, 60, 57, 52, 45, 40 }, // E4, C4, A3, E3, A2, E2
        ChordId.Bm       => new byte[] { 66, 62, 59, 54, 47, 42 }, // F#4, D4, B3, F#3, B2, F#2
        ChordId.C        => new byte[] { 64, 60, 55, 52, 48, 40 }, // E4, C4, G3, E3, C3, E2
        ChordId.D        => new byte[] { 66, 62, 57, 50, 45, 38 }, // F#4, D4, A3, D3, A2, D2
        ChordId.Em       => new byte[] { 64, 59, 55, 52, 47, 40 }, // E4, B3, G3, E3, B2, E2

        // Page 2
        ChordId.Cmaj7    => new byte[] { 64, 59, 55, 52, 48, 40 }, // E4, B3, G3, E3, C3, E2
        ChordId.C7       => new byte[] { 64, 58, 55, 52, 48, 40 }, // E4, Bb3, G3, E3, C3, E2
        ChordId.Cm7      => new byte[] { 63, 58, 55, 51, 48, 39 }, // Eb4, Bb3, G3, Eb3, C3, Eb2
        ChordId.G7       => new byte[] { 67, 59, 53, 50, 47, 43 }, // G4, B3, F3, D3, B2, G2
        ChordId.C9       => new byte[] { 62, 58, 55, 52, 48, 40 }, // D4, Bb3, G3, E3, C3, E2
        ChordId.Cm9      => new byte[] { 62, 58, 55, 51, 48, 39 }, // D4, Bb3, G3, Eb3, C3, Eb2

        // Page 3
        ChordId.C5       => new byte[] { 67, 60, 55, 48, 43, 36 }, // G4, C4, G3, C3, G2, C2
        ChordId.D5       => new byte[] { 69, 62, 57, 50, 45, 38 }, // A4, D4, A3, D3, A2, D2
        ChordId.G5       => new byte[] { 67, 55, 55, 50, 43, 43 }, // G4, G3, G3, D3, G2, G2
        ChordId.A5       => new byte[] { 69, 57, 57, 52, 45, 45 }, // A4, A3, A3, E3, A2, A2
        ChordId.Cmaj9    => new byte[] { 62, 59, 55, 52, 48, 40 }, // D4, B3, G3, E3, C3, E2
        ChordId.Cadd9    => new byte[] { 62, 60, 55, 52, 48, 40 }, // D4, C4, G3, E3, C3, E2

        // Page 4
        ChordId.G_B      => new byte[] { 67, 59, 55, 50, 47, 47 }, // B2 低音
        ChordId.Am7_G    => new byte[] { 64, 60, 57, 52, 45, 43 }, // G2 低音
        ChordId.D_Fsharp => new byte[] { 66, 62, 57, 50, 45, 42 }, // F#2 低音
        ChordId.F_A      => new byte[] { 65, 60, 57, 53, 45, 45 }, // A2 低音
        ChordId.A        => new byte[] { 64, 61, 57, 52, 45, 40 }, // E4, C#4, A3, E3, A2, E2
        ChordId.Dm       => new byte[] { 65, 62, 57, 50, 45, 38 }, // F4, D4, A3, D3, A2, D2

        _                => new byte[] { 64, 59, 55, 50, 45, 40 }  // 默认空弦 E4, B3, G3, D3, A2, E2
    };

    /// <summary>
    /// 将目标音符智能匹配到当前和弦下的最优琴弦 (1..6)
    /// </summary>
    public static byte MatchNoteToChordString(byte noteNumber, ChordId chord, HashSet<byte>? occupiedStrings = null)
    {
        var pitches = GetChordStringPitches(chord);

        // 1. 精确音高匹配且未被占用
        for (int i = 0; i < 6; i++)
        {
            byte strNo = (byte)(i + 1);
            if (occupiedStrings != null && occupiedStrings.Contains(strNo)) continue;
            if (pitches[i] == noteNumber) return strNo;
        }

        // 2. 音名 (Pitch Class) 一致，在未占用琴弦中选择绝对音高最接近的
        byte targetPc = (byte)(noteNumber % 12);
        byte bestString = 0;
        int minDistance = int.MaxValue;

        for (int i = 0; i < 6; i++)
        {
            byte strNo = (byte)(i + 1);
            if (occupiedStrings != null && occupiedStrings.Contains(strNo)) continue;

            if ((pitches[i] % 12) == targetPc)
            {
                int dist = Math.Abs(pitches[i] - noteNumber);
                if (dist < minDistance)
                {
                    minDistance = dist;
                    bestString = strNo;
                }
            }
        }

        if (bestString != 0) return bestString;

        // 3. 兜底: 在未占用琴弦中选择绝对音高最接近的
        bestString = 0;
        minDistance = int.MaxValue;
        for (int i = 0; i < 6; i++)
        {
            byte strNo = (byte)(i + 1);
            if (occupiedStrings != null && occupiedStrings.Contains(strNo)) continue;

            int dist = Math.Abs(pitches[i] - noteNumber);
            if (dist < minDistance)
            {
                minDistance = dist;
                bestString = strNo;
            }
        }

        if (bestString != 0) return bestString;

        // 若 6 弦全被占用，返回绝对音高最近的琴弦
        int absMinDist = int.MaxValue;
        byte fallbackStr = 1;
        for (int i = 0; i < 6; i++)
        {
            int dist = Math.Abs(pitches[i] - noteNumber);
            if (dist < absMinDist)
            {
                absMinDist = dist;
                fallbackStr = (byte)(i + 1);
            }
        }
        return fallbackStr;
    }

    /// <summary>
    /// 从原始 MIDI 音符与和弦切换流中提取按键拨弦事件 (支持琴弦同按、长按延音与 VoicingProfile)
    /// </summary>
    public static List<ChordPluckEvent> ExtractPluckEvents(
        IReadOnlyList<RawMidiNote> rawNotes,
        IReadOnlyList<ChordChangeEvent> chordEvents,
        ChordVoicingProfile? voicingProfile = null)
    {
        if (rawNotes == null || rawNotes.Count == 0)
            return new List<ChordPluckEvent>();

        voicingProfile ??= new ChordVoicingProfile();
        var notes = rawNotes.Where(n => n.Channel != 9).OrderBy(n => n.StartUs).ToList();
        if (notes.Count == 0) return new List<ChordPluckEvent>();

        var sortedChords = (chordEvents != null)
            ? chordEvents.OrderBy(c => c.TimeUs).ToList()
            : new List<ChordChangeEvent>();

        var plucks = new List<ChordPluckEvent>(notes.Count);

        // 同一时刻 (<= 15ms) 聚类，避免同时发声的音符抢占同一根弦 (琴弦同按支持)
        int i = 0;
        while (i < notes.Count)
        {
            int j = i;
            long clusterTime = notes[i].StartUs;
            var clusterNotes = new List<RawMidiNote>();

            while (j < notes.Count && (notes[j].StartUs - clusterTime) <= 15_000)
            {
                clusterNotes.Add(notes[j]);
                j++;
            }

            // 查找该时刻的生效和弦
            ChordId activeChord = ChordId.G;
            for (int k = sortedChords.Count - 1; k >= 0; k--)
            {
                if (sortedChords[k].TimeUs <= clusterTime)
                {
                    activeChord = sortedChords[k].Chord;
                    break;
                }
            }

            // 对簇内音符按音高从高到低分配琴弦 (高音对应细弦1，低音对应粗弦6)
            clusterNotes.Sort((a, b) => b.NoteNumber.CompareTo(a.NoteNumber));
            var occupied = new HashSet<byte>();

            foreach (var n in clusterNotes)
            {
                byte strNo = voicingProfile.MatchNoteToString(n.NoteNumber, activeChord, occupied, out _);
                occupied.Add(strNo);

                plucks.Add(new ChordPluckEvent(
                    timeUs: n.StartUs,
                    stringNo: strNo,
                    noteNumber: n.NoteNumber,
                    velocity: n.Velocity,
                    durationUs: n.DurationUs > 0 ? n.DurationUs : 300_000
                ));
            }

            i = j;
        }

        return plucks;
    }

    /// <summary>
    /// 攻击形态判定算法 (方案 6.3 & 6.4: Block / Strum / Arpeggio / Auto)
    /// </summary>
    public static (ChordArticulation Articulation, StrumDirectionHint Direction) DetectAttackArticulation(
        IReadOnlyList<RawMidiNote> cluster)
    {
        if (cluster == null || cluster.Count == 0)
            return (ChordArticulation.Auto, StrumDirectionHint.Unknown);

        if (cluster.Count == 1)
        {
            return (ChordArticulation.Arpeggio, StrumDirectionHint.Unknown);
        }

        long minStart = cluster.Min(n => n.StartUs);
        long maxStart = cluster.Max(n => n.StartUs);
        long attackSpanUs = maxStart - minStart;

        // 若 >= 3 个音，AttackSpan <= 20ms: Block (齐奏块和弦)
        if (cluster.Count >= 3 && attackSpanUs <= 20_000)
        {
            return (ChordArticulation.Block, StrumDirectionHint.Unknown);
        }

        // 若 >= 3 个音，20ms < AttackSpan <= 100ms 且音高具有单调趋势: Strum
        if (cluster.Count >= 3 && attackSpanUs > 20_000 && attackSpanUs <= 100_000)
        {
            var ordered = cluster.OrderBy(n => n.StartUs).ToList();
            int asc = 0, desc = 0;
            for (int k = 0; k < ordered.Count - 1; k++)
            {
                int diff = ordered[k + 1].NoteNumber - ordered[k].NoteNumber;
                if (diff > 0) asc++;
                else if (diff < 0) desc++;
            }
            if (asc > desc) return (ChordArticulation.Strum, StrumDirectionHint.LowToHigh);
            if (desc > asc) return (ChordArticulation.Strum, StrumDirectionHint.HighToLow);
            return (ChordArticulation.Strum, StrumDirectionHint.Unknown);
        }

        // 若为 2 个音且起音时间相差极小 (<= 20ms)，作为双音齐奏 Block 处理
        if (cluster.Count == 2 && attackSpanUs <= 20_000)
        {
            return (ChordArticulation.Block, StrumDirectionHint.Unknown);
        }

        // 若音符起音分散 (例如间距 > 40ms)，判定为分解和弦 / 琶音
        if (attackSpanUs > 40_000)
        {
            return (ChordArticulation.Arpeggio, StrumDirectionHint.Unknown);
        }

        return (ChordArticulation.Auto, StrumDirectionHint.Unknown);
    }

    /// <summary>
    /// 全曲尝试 -11 ~ +11 半音统一转调，计算在游戏 24 和弦集合中的覆盖率 (方案 6.6)
    /// </summary>
    public static (int BestTranspose, double Coverage, Dictionary<int, double> AllTransposes) FindBestKeyTransposeFor24Chords(
        IReadOnlyList<RawMidiNote> rawNotes)
    {
        var result = new Dictionary<int, double>();
        if (rawNotes == null || rawNotes.Count == 0) return (0, 1.0, result);

        var notes = rawNotes.Where(n => n.Channel != 9).ToList();
        if (notes.Count == 0) return (0, 1.0, result);

        int bestTranspose = 0;
        double maxCoverage = -1.0;

        for (int t = -11; t <= 11; t++)
        {
            var shiftedNotes = notes.Select(n => new RawMidiNote
            {
                NoteNumber = (byte)Math.Clamp(n.NoteNumber + t, 0, 127),
                StartUs = n.StartUs,
                DurationUs = n.DurationUs,
                Velocity = n.Velocity,
                Channel = n.Channel
            }).ToList();

            var events = Analyze(shiftedNotes);
            if (events.Count == 0)
            {
                result[t] = 0.0;
                continue;
            }

            double avgConfidence = events.Average(e => e.Confidence);
            result[t] = avgConfidence;

            if (avgConfidence > maxCoverage)
            {
                maxCoverage = avgConfidence;
                bestTranspose = t;
            }
        }

        return (bestTranspose, maxCoverage, result);
    }
}
