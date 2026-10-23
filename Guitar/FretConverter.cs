using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Guitar;

/// <summary>
/// 标准六弦吉他调弦与音域配置 (EADGBE)
/// 1弦=E4(64), 2弦=B3(59), 3弦=G3(55), 4弦=D3(50), 5弦=A2(45), 6弦=E2(40)
/// 覆盖 0~24 品: 最低音 6弦0品 E2(40) ~ 最高音 1弦24品 E6(88) 共 49 个全半音
/// </summary>
public sealed class GuitarTuning
{
    public byte[] OpenStrings { get; set; } = new byte[6]
    {
        64, // 1弦 E4 (最高音弦, 游戏键 6)
        59, // 2弦 B3 (游戏键 5)
        55, // 3弦 G3 (游戏键 4)
        50, // 4弦 D3 (游戏键 3)
        45, // 5弦 A2 (游戏键 2)
        40  // 6弦 E2 (最低音弦, 游戏键 1)
    };

    public byte GetOpenMidiNote(int stringNumber) =>
        (stringNumber >= 1 && stringNumber <= 6) ? OpenStrings[stringNumber - 1] : (byte)64;

    public byte MinPlayableNote => OpenStrings[5]; // 6弦0品: 40 (E2)
    public byte MaxPlayableNote => (byte)(OpenStrings[0] + 24); // 1弦24品: 64 + 24 = 88 (E6)

    public static readonly GuitarTuning Standard = new();
}

/// <summary>
/// 琴弦与品格候选位置 (0~24 品与 5 页轮盘槽位映射)
/// </summary>
public readonly struct FretCandidate
{
    public byte StringNo { get; } // 1..6
    public byte Fret { get; }     // 0..24
    public byte Page { get; }     // 1..5 (P5/S1 = 0 品空弦)
    public byte Slot { get; }     // 1..6
    public bool IsSkip { get; }   // 是否为跳音/静音候选

    public FretCandidate(byte stringNo, byte fret, bool isSkip = false)
    {
        StringNo = stringNo;
        Fret = fret;
        IsSkip = isSkip;
        if (isSkip)
        {
            Page = 0;
            Slot = 0;
        }
        else
        {
            var (p, s) = FretConverter.GetPageAndSlot(fret);
            Page = p;
            Slot = s;
        }
    }

    public static FretCandidate Skip => new(1, 0, isSkip: true);

    public override string ToString() => IsSkip ? "SKIP" : $"S{StringNo}-F{Fret}(P{Page}S{Slot})";
}

public sealed class FretConverterOptions
{
    public double PageChangeCost { get; set; } = 6.0;          // 动态换页基准成本 (v4.3 降低至 4~8 范围，避免选怪异高品)
    public double FretDistanceCost { get; set; } = 1.0;        // 换品距离权重
    public double StringDistanceCost { get; set; } = 0.8;      // 换弦距离权重
    public double HandPositionShiftPenalty { get; set; } = 1.2; // 跨大弦距惩罚 (>= 3 弦大跳)
    public double HighFretCost { get; set; } = 0.6;            // 13~18品中等惩罚
    public double ExtremeHighFretCost { get; set; } = 2.0;     // 19~24品较高惩罚
    public double NaturalPositionCost { get; set; } = 2.0;     // 自然弦位偏好成本
    public double SlotChangeCost { get; set; } = 3.0;          // 同页内轮盘换槽成本 (优先在同品不同弦弹奏，避免频繁换槽)
    public double SkipNoteCost { get; set; } = 2000.0;         // 跳音安全兜底成本
    
    // v4.3 快速翻页与轮盘稳定参数
    public long PageKeyHoldUs { get; set; } = 25_000;          // R/T 按键保持时间 25ms (20~35ms)
    public long PageInterTapGapUs { get; set; } = 12_000;      // 连续 R/T 按键间隔 12ms (8~20ms)
    public long PageInputSafetyUs { get; set; } = 8_000;       // 调度抖动安全余量 8ms (5~15ms)
    public long WheelMouseSettleUs { get; set; } = 55_000;     // 轮盘选槽稳定时间 55ms (标准提前量)
    public long SlotSelectDelayUs { get; set; } = 35_000;      // 选槽物理耗时 35ms (移鼠+点击+稳定)
    public byte InitialPage { get; set; } = 1;                 // 初始起始页面 (默认稳定为 P1)
    public bool IsMonophonicSource { get; set; } = false;      // 是否已经是单音识别结果 (若是，跳过 Sanitize)
    public bool AvoidStrum { get; set; }

    // 兼容旧属性
    public long PageSwitchDelayUs
    {
        get => PageKeyHoldUs + PageInterTapGapUs + PageInputSafetyUs;
        set
        {
            if (value > 0)
            {
                PageKeyHoldUs = (long)(value * 0.55);
                PageInterTapGapUs = (long)(value * 0.30);
                PageInputSafetyUs = (long)(value * 0.15);
            }
        }
    }

    public long SlotSettleUs
    {
        get => WheelMouseSettleUs;
        set { if (value > 0) WheelMouseSettleUs = value; }
    }
}

/// <summary>
/// 品格模式转换器: 0~24 品完整映射、整曲统一八度移调与 Viterbi DP 路径规划
/// </summary>
public static class FretConverter
{
    /// <summary>
    /// 根据品格编号计算所属轮盘页面 (1..5) 与扇区槽位 (1..6)
    /// </summary>
    public static (byte Page, byte Slot) GetPageAndSlot(byte fret)
    {
        if (fret == 0)
        {
            return (5, 1); // P5: 0 品空弦
        }

        byte page = (byte)(((fret - 1) / 6) + 1);
        byte slot = (byte)(((fret - 1) % 6) + 1);
        return (page, slot);
    }

    /// <summary>优先生成 1~24 品候选；仅在没有按弦位置时使用 P5 空弦。</summary>
    public static List<FretCandidate> GenerateCandidates(byte noteNumber, GuitarTuning tuning)
    {
        var list = new List<FretCandidate>(6);
        for (int s = 1; s <= 6; s++)
        {
            int openNote = tuning.GetOpenMidiNote(s);
            int fret = noteNumber - openNote;
            if (fret >= 1 && fret <= 24)
            {
                list.Add(new FretCandidate((byte)s, (byte)fret));
            }
        }

        if (list.Count == 0)
        {
            for (int s = 1; s <= 6; s++)
            {
                if (noteNumber == tuning.GetOpenMidiNote(s))
                {
                    list.Add(new FretCandidate((byte)s, 0));
                }
            }
        }

        return list;
    }

    /// <summary>
    /// 吉他品格独奏保护：确保输入序列为纯净主旋律线。单音模式直通；仅在多音输入且真实时间区间重叠时保留主旋律音符。
    /// </summary>
    public static List<RawMidiNote> SanitizeForFretSolo(IReadOnlyList<RawMidiNote> notes, bool isMonophonic = false, bool avoidStrum = false)
    {
        if (notes == null || notes.Count <= 1) return notes?.ToList() ?? new List<RawMidiNote>();

        // 过滤打击乐通道
        var nonDrum = notes.Where(n => n.Channel != 9).OrderBy(n => n.StartUs).ToList();
        if (nonDrum.Count <= 1) return nonDrum;

        // 若输入源已经标记为单音识别结果 (ContinuousMelody, TrackPriority 单轨, Monophonic, HighestNote)，完全跳过 Sanitize
        if (isMonophonic && !avoidStrum)
        {
            return nonDrum;
        }

        // 同步复音去重：22ms 内的跨轨同音和微小时差属于同一次起音。
        // 起音时间不同的连续音符 (如琶音、颤音、连续单音) 完整保留，绝不因延音跨度而吞噬后续音符
        const long simultaneousToleranceUs = 22_000;
        const long strumRollWindowUs = 65_000;
        var result = new List<RawMidiNote>(nonDrum.Count);
        long activeMelodyEndUs = long.MinValue / 2;
        byte activeMelodyPitch = 0;
        
        int i = 0;
        while (i < nonDrum.Count)
        {
            if (avoidStrum)
            {
                long rollStartUs = nonDrum[i].StartUs;
                int r = i + 1;
                while (r < nonDrum.Count && nonDrum[r].StartUs - rollStartUs <= strumRollWindowUs &&
                       nonDrum[r].StartUs - nonDrum[r - 1].StartUs <= 25_000) r++;
                var roll = nonDrum.GetRange(i, r - i);
                if (roll.Count >= 3 && (roll.Any(n => n.IsBrushChord) ||
                    (roll.Select(n => n.NoteNumber).Distinct().Count() >= 3 && roll.Max(n => n.NoteNumber) - roll.Min(n => n.NoteNumber) >= 5 &&
                     (roll[^1].StartUs - roll[0].StartUs <= 25_000 ||
                      roll.Zip(roll.Skip(1), (a, b) => Math.Sign(b.NoteNumber - a.NoteNumber)).Count(d => d > 0) >= (roll.Count - 1) * .7 ||
                      roll.Zip(roll.Skip(1), (a, b) => Math.Sign(b.NoteNumber - a.NoteNumber)).Count(d => d < 0) >= (roll.Count - 1) * .7))))
                {
                    var lead = roll.OrderByDescending(n => n.IsPrimaryMelody).ThenByDescending(n => n.NoteNumber).ThenByDescending(n => n.Velocity).First();
                    bool isUnderSustainedMelody = !roll.Any(n => n.IsPrimaryMelody) &&
                        rollStartUs < activeMelodyEndUs - 30_000 && activeMelodyPitch > lead.NoteNumber + 4;
                    if (!isUnderSustainedMelody)
                    {
                        result.Add(new RawMidiNote { StartUs = rollStartUs, DurationUs = lead.DurationUs, NoteNumber = lead.NoteNumber,
                            Velocity = lead.Velocity, Channel = lead.Channel, TrackId = lead.TrackId, Program = lead.Program,
                            IsPrimaryMelody = lead.IsPrimaryMelody, OrigString = lead.OrigString, OrigFret = lead.OrigFret, SourceFormat = lead.SourceFormat });
                        activeMelodyEndUs = rollStartUs + Math.Max(150_000, lead.DurationUs);
                        activeMelodyPitch = lead.NoteNumber;
                    }
                    i = r;
                    continue;
                }
            }
            // Anchor the tolerance window to the first onset in the group. Comparing against
            // `best.StartUs` lets a higher note move the window forward and chain-merge notes
            // whose total onset spread is greater than the simultaneous tolerance.
            long groupStartUs = nonDrum[i].StartUs;
            var best = nonDrum[i];
            int j = i + 1;

            while (j < nonDrum.Count && (nonDrum[j].StartUs - groupStartUs) < simultaneousToleranceUs)
            {
                // 主旋律提取准则：并发和弦中优先保留高音声部 (主旋律)，相同音高保留最高力度
                if (nonDrum[j].NoteNumber > best.NoteNumber ||
                   (nonDrum[j].NoteNumber == best.NoteNumber && nonDrum[j].Velocity > best.Velocity))
                {
                    best = nonDrum[j];
                }
                j++;
            }

            result.Add(best);
            if (best.NoteNumber >= activeMelodyPitch - 2 || best.StartUs >= activeMelodyEndUs)
            {
                activeMelodyEndUs = best.StartUs + Math.Max(150_000, best.DurationUs);
                activeMelodyPitch = best.NoteNumber;
            }
            i = j;
        }

        return result;
    }

    /// <summary>
    /// 将主旋律音符序列转换为最优品格演奏事件 (0~24 品全局 DP)
    /// </summary>
    public static List<FretPlayEvent> Convert(
        IReadOnlyList<RawMidiNote> melodyNotes,
        GuitarTuning? tuning = null,
        FretConverterOptions? options = null)
    {
        if (melodyNotes == null || melodyNotes.Count == 0)
            return new List<FretPlayEvent>();

        tuning ??= GuitarTuning.Standard;
        options ??= new FretConverterOptions();

        // 0. 吉他品格独奏保护：确保输入序列为纯净主旋律线，绝不整曲重跑最高音切片
        var cleanNotes = SanitizeForFretSolo(melodyNotes, options.IsMonophonicSource, options.AvoidStrum);
        if (cleanNotes.Count == 0) return new List<FretPlayEvent>();

        // 1. 全局最佳统一八度移调 (加权评分，禁止逐音 Clamp)
        int bestOctaveShift = FindBestGlobalOctaveShift(cleanNotes, tuning);

        // 2. 为每个音符生成候选指法，超音域音符生成 Skip 候选
        var candidateGrid = new List<List<FretCandidate>>(cleanNotes.Count);
        var finalNotes = new List<byte>(cleanNotes.Count);

        foreach (var note in cleanNotes)
        {
            int shifted = note.NoteNumber + bestOctaveShift;
            if (shifted >= tuning.MinPlayableNote && shifted <= tuning.MaxPlayableNote)
            {
                byte finalNote = (byte)shifted;
                finalNotes.Add(finalNote);

                var candidates = GenerateCandidates(finalNote, tuning);
                candidates.Add(FretCandidate.Skip); // 兜底候选
                candidateGrid.Add(candidates);
            }
            else
            {
                // 超音域，直接置为 Skip 候选，绝不暴力 Clamp 产生刺耳错音 (方案 5.1)
                finalNotes.Add((byte)Math.Clamp(shifted, 0, 127));
                candidateGrid.Add(new List<FretCandidate> { FretCandidate.Skip });
            }
        }

        // 3. 动态规划寻找全局最优指法路径 (Viterbi DP)
        var (bestPath, conflictFlags) = FingeringDpSolve(candidateGrid, cleanNotes, finalNotes, options);

        // 4. 构建输出事件
        var result = new List<FretPlayEvent>(cleanNotes.Count);
        for (int i = 0; i < cleanNotes.Count; i++)
        {
            var note = cleanNotes[i];
            var chosen = bestPath[i];
            bool isSkipped = chosen.IsSkip;

            result.Add(new FretPlayEvent(
                timeUs: note.StartUs,
                stringNo: chosen.StringNo,
                fret: chosen.Fret,
                noteNumber: finalNotes[i],
                velocity: note.Velocity,
                durationUs: note.DurationUs > 0 ? note.DurationUs : 300_000,
                page: chosen.Page,
                slot: chosen.Slot,
                isTimingConflict: conflictFlags[i],
                isSkipped: isSkipped
            ));
        }

        return result;
    }

    /// <summary>
    /// 全局最佳八度移调 (方案 5.2: 动态扩展候选八度范围，结合时长/力度/音乐重要度评分)
    /// 采用时长与力度加权评分，禁止对音域外音符暴力 Clamp
    /// </summary>
    public static int FindBestGlobalOctaveShift(IReadOnlyList<RawMidiNote> notes, GuitarTuning tuning)
    {
        if (notes == null || notes.Count == 0) return 0;

        int minNote = notes.Min(n => n.NoteNumber);
        int maxNote = notes.Max(n => n.NoteNumber);

        // 动态根据音域覆盖生成候选整数八度
        var candidateShifts = new HashSet<int> { 0, -12, 12, -24, 24 };
        for (int s = -48; s <= 48; s += 12)
        {
            if (maxNote + s >= tuning.MinPlayableNote && minNote + s <= tuning.MaxPlayableNote)
            {
                candidateShifts.Add(s);
            }
        }

        int bestShift = 0;
        double maxScore = double.MinValue;

        foreach (int shift in candidateShifts)
        {
            double score = 0;
            foreach (var n in notes)
            {
                int val = n.NoteNumber + shift;
                double durWeight = Math.Clamp((n.DurationUs > 0 ? n.DurationUs : 300_000) / 100_000.0, 0.5, 10.0);
                double velWeight = Math.Clamp(n.Velocity / 127.0, 0.4, 1.0);
                double weight = durWeight * velWeight;

                if (val >= tuning.MinPlayableNote && val <= tuning.MaxPlayableNote)
                {
                    score += 10.0 * weight;
                    // 仅对超过 20 品的极端高把位 (> 1弦20品: 64 + 20 = 84) 施加轻微偏好，绝不惩罚 67~84 之间的自然人声主旋律
                    if (val > 84)
                    {
                        score -= (val - 84) * 0.05 * weight;
                    }
                }
                else
                {
                    // 超出吉他可奏音域 (40~88)，施加重度不可奏扣分
                    score -= 50.0 * weight;
                }
            }

            // 强烈倾向于原调保持音高真实性 (shift == 0)。
            // 只要原曲在吉他音域内可奏，每偏离 1 个八度施加 240 分大额惩罚，杜绝无故八度下沉
            score -= Math.Abs(shift) * 20.0;

            if (score > maxScore)
            {
                maxScore = score;
                bestShift = shift;
            }
        }

        return bestShift;
    }

    public static double GetPositionalCost(FretCandidate cand, FretConverterOptions opt, byte noteNumber = 0, RawMidiNote? sourceNote = null)
    {
        if (cand.IsSkip) return 0;

        double cost = 0;

        // 1. 分段阶梯高品惩罚: 0~12品零成本，13~18品低成本，19~24品高成本
        if (cand.Fret > 18)
        {
            cost += (6 * opt.HighFretCost) + (cand.Fret - 18) * opt.ExtremeHighFretCost;
        }
        else if (cand.Fret > 12)
        {
            cost += (cand.Fret - 12) * opt.HighFretCost;
        }

        // 2. 自然弦位偏好:
        // 高音 (>= 64, E4+) 优先 1/2 弦
        // 中音 (50..63, D3..D#4) 优先 2/3/4 弦
        // 低音 (< 50, < D3) 优先 4/5/6 弦
        if (noteNumber > 0)
        {
            bool isNatural = false;
            if (noteNumber >= 64)
            {
                isNatural = (cand.StringNo <= 2);
            }
            else if (noteNumber >= 50)
            {
                isNatural = (cand.StringNo >= 2 && cand.StringNo <= 4);
            }
            else
            {
                isNatural = (cand.StringNo >= 4);
            }

            if (!isNatural)
            {
                cost += opt.NaturalPositionCost;
            }
        }

        // GTP's original/equivalent fingering is a soft prior. A playable same-page
        // alternative can still win when the source fingering needs an unsafe page switch.
        if (sourceNote?.OrigString == cand.StringNo && sourceNote?.OrigFret == cand.Fret)
            cost -= 4.5;

        return cost;
    }

    private static (List<FretCandidate> Path, List<bool> ConflictFlags) FingeringDpSolve(
        List<List<FretCandidate>> grid,
        IReadOnlyList<RawMidiNote> notes,
        IReadOnlyList<byte> finalNotes,
        FretConverterOptions options)
    {
        int n = grid.Count;
        if (n == 0) return (new List<FretCandidate>(), new List<bool>());

        var dp = new double[n][];
        var parent = new int[n][];
        var conflict = new bool[n][];

        // Step 0
        dp[0] = new double[grid[0].Count];
        parent[0] = new int[grid[0].Count];
        conflict[0] = new bool[grid[0].Count];
        bool hasPlayable = grid[0].Any(cand => !cand.IsSkip);

        for (int c = 0; c < grid[0].Count; c++)
        {
            var cand = grid[0][c];
            if (cand.IsSkip)
            {
                // 若首音有可奏候选，首音绝无准备不及时的理由 (PreRoll 提前准备)，禁止跳过首音
                dp[0][c] = hasPlayable ? double.PositiveInfinity : options.SkipNoteCost;
                conflict[0][c] = true;
            }
            else
            {
                // 首音若偏离初始页面，由 PreRoll 提前消化翻页，施加轻微初始偏好
                int initSteps = CircularPageNavigator.GetShortestPath(options.InitialPage, cand.Page, 5).Steps;
                double initPageCost = initSteps * (options.PageChangeCost * 0.2);
                dp[0][c] = initPageCost + GetPositionalCost(cand, options, finalNotes[0], notes[0]);
                conflict[0][c] = false;
            }
            parent[0][c] = -1;
        }

        // Steps 1 to n - 1
        for (int i = 1; i < n; i++)
        {
            var curCandidates = grid[i];
            var prevCandidates = grid[i - 1];
            long deltaUs = notes[i].StartUs - notes[i - 1].StartUs;
            byte curNote = finalNotes[i];

            dp[i] = new double[curCandidates.Count];
            parent[i] = new int[curCandidates.Count];
            conflict[i] = new bool[curCandidates.Count];

            for (int c = 0; c < curCandidates.Count; c++)
            {
                var cur = curCandidates[c];
                double minCost = double.PositiveInfinity;
                int bestPrev = 0;
                bool bestConflict = false;

                for (int p = 0; p < prevCandidates.Count; p++)
                {
                    double prevDp = dp[i - 1][p];
                    if (double.IsPositiveInfinity(prevDp)) continue;

                    var prev = prevCandidates[p];
                    FretCandidate transitionPrevious = prev;
                    long transitionDeltaUs = deltaUs;
                    if (prev.IsSkip && TryFindPreviousPlayedState(grid, parent, i - 1, p, out var lastPlayed, out int lastPlayedIndex))
                    {
                        transitionPrevious = lastPlayed;
                        transitionDeltaUs = notes[i].StartUs - notes[lastPlayedIndex].StartUs;
                    }
                    var (stepCost, isConflict) = CalculateTransitionCost(transitionPrevious, cur, transitionDeltaUs, options, curNote, notes[i]);
                    if (double.IsPositiveInfinity(stepCost)) continue;

                    double totalCost = prevDp + stepCost;
                    if (totalCost < minCost)
                    {
                        minCost = totalCost;
                        bestPrev = p;
                        bestConflict = isConflict;
                    }
                }

                dp[i][c] = minCost;
                parent[i][c] = bestPrev;
                conflict[i][c] = bestConflict;
            }
        }

        // Find minimum at last step
        double finalMin = double.PositiveInfinity;
        int bestLastIdx = 0;
        for (int c = 0; c < grid[n - 1].Count; c++)
        {
            if (dp[n - 1][c] < finalMin)
            {
                finalMin = dp[n - 1][c];
                bestLastIdx = c;
            }
        }

        // 极端兜底: 若末尾全为 +Infinity，选 Skip 节点
        if (double.IsPositiveInfinity(finalMin))
        {
            int skipIdx = grid[n - 1].FindIndex(cand => cand.IsSkip);
            bestLastIdx = skipIdx >= 0 ? skipIdx : 0;
        }

        // Backtrack
        var path = new FretCandidate[n];
        var flags = new bool[n];
        int currIdx = bestLastIdx;
        for (int i = n - 1; i >= 0; i--)
        {
            path[i] = grid[i][currIdx];
            flags[i] = conflict[i][currIdx] || path[i].IsSkip;
            currIdx = parent[i][currIdx];
            if (currIdx < 0 && i > 0)
            {
                currIdx = 0;
            }
        }

        return (path.ToList(), flags.ToList());
    }

    private static bool TryFindPreviousPlayedState(
        IReadOnlyList<List<FretCandidate>> grid,
        IReadOnlyList<int[]> parents,
        int stageIndex,
        int stateIndex,
        out FretCandidate lastPlayed,
        out int lastPlayedIndex)
    {
        for (int index = stageIndex; index >= 0; index--)
        {
            if (stateIndex < 0 || stateIndex >= grid[index].Count) break;
            FretCandidate candidate = grid[index][stateIndex];
            if (!candidate.IsSkip)
            {
                lastPlayed = candidate;
                lastPlayedIndex = index;
                return true;
            }
            stateIndex = parents[index][stateIndex];
        }
        lastPlayed = FretCandidate.Skip;
        lastPlayedIndex = -1;
        return false;
    }

    public static (double Cost, bool IsTimingConflict) CalculateTransitionCost(
        FretCandidate prev,
        FretCandidate cur,
        long deltaUs,
        FretConverterOptions opt,
        byte curNoteNumber = 0,
        RawMidiNote? sourceNote = null)
    {
        if (cur.IsSkip)
        {
            return (opt.SkipNoteCost, true);
        }

        if (prev.IsSkip)
        {
            return (GetPositionalCost(cur, opt, curNoteNumber, sourceNote), false);
        }

        int pageSteps = CircularPageNavigator.GetShortestPath(prev.Page, cur.Page, 5).Steps;
        bool fretChanged = (cur.Fret != prev.Fret);
        bool slotChanged = (cur.Slot != prev.Slot);

        // 实际物理准备耗时 (v4.3 快速翻页串行模型: 串行 TapHold + InterTapGap + MouseSettle + Safety)
        long singleTapUs = opt.PageKeyHoldUs + opt.PageInterTapGapUs;
        long requiredPrepareUs = 0;
        if (pageSteps > 0)
        {
            // Page navigation neutralizes the gamepad stick; even the same
            // numbered slot must be selected again on the destination page.
            requiredPrepareUs = pageSteps * singleTapUs
                + (slotChanged ? opt.WheelMouseSettleUs : 0)
                + opt.PageInputSafetyUs;
        }
        else if (slotChanged || fretChanged)
        {
            requiredPrepareUs = opt.WheelMouseSettleUs + opt.PageInputSafetyUs;
        }
        else
        {
            requiredPrepareUs = 0; // 同页同品槽: 0ms
        }

        // 核心硬约束: 当物理准备时间不足时，直接返回 +Infinity，配合 SkipNote 兜底，绝不在旧品位发音
        if (deltaUs > 0 && deltaUs < requiredPrepareUs)
        {
            return (double.PositiveInfinity, true);
        }

        // 动态换页代价 (与实测控制耗时挂钩，避免迫使 DP 选怪品)
        double dynamicPageCost = opt.PageChangeCost * (singleTapUs / 37000.0);
        double cost = 0;
        cost += pageSteps * dynamicPageCost;
        if (pageSteps == 0 && (slotChanged || fretChanged))
        {
            cost += opt.SlotChangeCost;
        }

        // The mouse needs a longer anti-race bias. A gamepad stick reaches its
        // slot in one report, so keep only a short preference for stable frets.
        long rapidWindowUs = 320_000;
        double rapidPenalty = 6.0;
        if (deltaUs > 0 && deltaUs < rapidWindowUs && (slotChanged || fretChanged || pageSteps > 0))
        {
            double rapidFactor = (rapidWindowUs - deltaUs) / (double)rapidWindowUs;
            cost += rapidFactor * rapidPenalty;
        }

        cost += Math.Abs(cur.Fret - prev.Fret) * opt.FretDistanceCost;
        cost += Math.Abs(cur.StringNo - prev.StringNo) * opt.StringDistanceCost;
        if (Math.Abs(cur.StringNo - prev.StringNo) >= 3)
        {
            cost += opt.HandPositionShiftPenalty;
        }
        double positionalCost = GetPositionalCost(cur, opt, curNoteNumber, sourceNote);
        if (deltaUs > 0 && deltaUs < 250_000 && pageSteps == 0 && cur.Fret is >= 13 and <= 18)
        {
            // A rapid same-page equivalent fingering is preferable to a page
            // change. Temporarily remove its ordinary 13..18 fret surcharge.
            positionalCost -= (cur.Fret - 12) * opt.HighFretCost;
        }
        cost += positionalCost;

        return (cost, false);
    }
}
