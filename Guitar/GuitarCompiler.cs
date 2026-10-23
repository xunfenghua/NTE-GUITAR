using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Guitar;

public enum GuitarActionType : byte
{
    PageNav = 1,     // 物理按键 R / T 翻页 (0x13 / 0x14)
    WheelSelect = 2, // 点击轮盘指定 Slot (1..6)
    StringDown = 3,  // 琴弦数字键按下 (1..6)
    StringUp = 4,    // 琴弦数字键抬起 (1..6)
    StrumClick = 5,  // 安全点扫弦点击 (左键/右键)
    PageNavUp = 6,   // 定时抬起翻页键，不阻塞播放线程
    ModeSwitchDown = 7, // Z: 切换品格/和弦模式，游戏页面归位 P1
    ModeSwitchUp = 8,
    WheelSelectUp = 9, // 手柄左摇杆显式回中
    StrumUp = 10       // 手柄右摇杆显式回中
}

public readonly struct GuitarAction
{
    public long TimeUs { get; }
    public GuitarActionType Type { get; }
    public byte Value { get; }          // SlotIndex (1..6) 或 StringNo (1..6) 或 Page
    public ushort ScanCode { get; }     // 物理按键 ScanCode (0x13=R, 0x14=T, 0x02..0x07=1..6)
    public MouseButton MouseButton { get; } // 鼠标按键
    public long DeadlineUs { get; }     // 截止时间戳 (超过则不可在旧状态执行)
    public byte RequiredPage { get; }   // 依赖的游戏页面
    public byte RequiredSlot { get; }   // 依赖的轮盘槽位
    public GameActionResource Resource { get; }
    public FallbackPolicy FallbackPolicy { get; }
    public byte NoteNumber { get; }

    public GuitarAction(
        long timeUs,
        GuitarActionType type,
        byte value,
        ushort scanCode = 0,
        MouseButton mouseButton = MouseButton.Left,
        long deadlineUs = 0,
        byte requiredPage = 0,
        byte requiredSlot = 0,
        GameActionResource resource = GameActionResource.None,
        FallbackPolicy fallbackPolicy = FallbackPolicy.Skip,
        byte noteNumber = 0)
    {
        TimeUs = timeUs;
        Type = type;
        Value = value;
        ScanCode = scanCode;
        MouseButton = mouseButton;
        DeadlineUs = (deadlineUs > 0) ? deadlineUs : (timeUs + 60_000);
        RequiredPage = requiredPage;
        RequiredSlot = requiredSlot;
        Resource = resource;
        FallbackPolicy = fallbackPolicy;
        NoteNumber = noteNumber;
    }

    public override string ToString() => $"{TimeUs} us: {Type} Value={Value} Scan=0x{ScanCode:X2} Mouse={MouseButton} Req=P{RequiredPage}S{RequiredSlot}";
}

public sealed class GuitarCompilerOptions
{
    // v4.3 快速翻页参数 (方案 4.1)
    public long PageKeyHoldUs { get; set; } = 25_000;       // R/T 按键保持时间 25ms (20~35ms)
    public long PageInterTapGapUs { get; set; } = 12_000;   // 连续两个 R/T Tap 的最小间隔 12ms (8~20ms)
    public long PageInputSafetyUs { get; set; } = 8_000;    // 调度抖动安全余量 8ms (5~15ms)
    public long WheelMouseSettleUs { get; set; } = 55_000;  // 轮盘选槽稳定时间 55ms (标准提前量)
    public long StringTapDurationUs { get; set; } = 25_000; // 琴弦脉冲按下时长 25ms，防长按卡键
    public long MinRetriggerGapUs { get; set; } = 25_000;   // 同弦过密防抖 25ms
    public long ReleaseGapUs { get; set; } = 22_000;        // 跨帧抬键间隙 22ms
    public long MinHoldUs { get; set; } = 20_000;           // 游戏识别要求至少 20ms
    public long MaxSustainUs { get; set; } = 1_500_000;     // 最大长按延音 1.5s
    public long CloseNotesThresholdUs { get; set; } = 280_000;
    public bool EnableSustain { get; set; } = true;         // 默认启用智能动态延音 (两键过近点按防吞音，尾音/空隙充分延音)
    public GuitarPlayStyle PlayStyle { get; set; } = GuitarPlayStyle.Hybrid; // 演奏风格策略
    public AutoStrumMode AutoStrum { get; set; } = AutoStrumMode.Auto;
    public bool EnableInitialSync { get; set; } = false;    // 是否强制在开头排布 4xR / 3xR 同步动作
    public byte InitialPage { get; set; } = 1;              // 初始所在页码 (默认稳定为 P1)
    public ChordVoicingProfile VoicingProfile { get; set; } = new();

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
/// 吉他动作编译器: 负责翻页预加载时序、轮盘选品、发音按键与扫弦动作生成
/// </summary>
public static class GuitarCompiler
{
    /// <summary>
    /// 编译品格演奏事件为动作序列 (支持 0~24 品与 R/T 翻页预加载)
    /// </summary>
    public static List<GuitarAction> CompileFretEvents(
        IReadOnlyList<FretPlayEvent> fretEvents,
        WheelPreset preset,
        GuitarCompilerOptions? options = null)
    {
        if (fretEvents == null || fretEvents.Count == 0)
            return new List<GuitarAction>();

        options ??= new GuitarCompilerOptions();
        NormalizeMinimumTiming(options);
        var actions = new List<GuitarAction>();
        var barrierTimes = new HashSet<long>();
        // Keep MIDI/source timestamps intact. Control preparation may delay an
        // attack, but mutating FretPlayEvent.TimeUs made distinct notes collapse
        // onto one timestamp and the retrigger filter discarded them.
        var effectiveTimes = new Dictionary<FretPlayEvent, long>();
        var ignoredSameMomentStrings = new HashSet<FretPlayEvent>();
        var seenStringMoments = new HashSet<(byte StringNo, long TimeUs)>();
        foreach (var ev in fretEvents)
            if (!ev.IsSkipped && !seenStringMoments.Add((ev.StringNo, ev.TimeUs)))
                ignoredSameMomentStrings.Add(ev);

        // 1. 生成轮盘翻页与槽位选择准备动作 (提前调度，允许负时间戳用于 PreRoll 预热，消除 0ms 爆发拥堵)
        byte currentPage = options.InitialPage; // 起始页码 (默认稳定为 P1)
        byte currentSlot = 0;
        long lastNoteTimeUs = -1;
        long lastControlFinishedUs = long.MinValue / 2;
        long navHoldUs = Math.Max(25_000, options.PageKeyHoldUs);
        long navGapUs = Math.Max(15_000, options.PageInterTapGapUs);
        long stepDelayUs = navHoldUs + navGapUs;
        long wheelSettleUs = GetWheelSettleUs(options);
        long minHoldUs = Math.Max(20_000, options.MinHoldUs);

        foreach (var ev in fretEvents)
        {
            if (ev.IsSkipped || ignoredSameMomentStrings.Contains(ev)) continue;

            long targetAttackUs = ev.TimeUs;

            byte targetPage = ev.Page;
            byte targetSlot = ev.Slot > 0 ? ev.Slot : (byte)1;
            if (ev.Page == 0 || ev.Slot == 0)
            {
                (targetPage, targetSlot) = FretConverter.GetPageAndSlot(ev.Fret);
            }

            if (targetPage != currentPage || targetSlot != currentSlot)
            {
                var (steps, direction) = CircularPageNavigator.GetShortestPath(currentPage, targetPage, 5);
                long idealControlStartUs = targetAttackUs - wheelSettleUs - steps * stepDelayUs;
                long earliestSafeStartUs = lastNoteTimeUs >= 0
                    ? lastNoteTimeUs + Math.Max(minHoldUs, options.StringTapDurationUs) + 6_000
                    : long.MinValue / 2;
                if (lastControlFinishedUs > long.MinValue / 4)
                    earliestSafeStartUs = Math.Max(earliestSafeStartUs, lastControlFinishedUs + 6_000);
                long cursorUs = Math.Max(idealControlStartUs, earliestSafeStartUs);

                if (steps > 0)
                {
                    ushort navScan = direction > 0 ? preset.PageNextScanCode : preset.PagePrevScanCode; // T 或 R
                    for (int k = 0; k < steps; k++)
                    {
                        long navTime = cursorUs;
                        byte navPage = CircularPageNavigator.StepPage(currentPage, direction, 5);
                        actions.Add(new GuitarAction(
                            navTime,
                            GuitarActionType.PageNav,
                            navPage,
                            navScan,
                            MouseButton.Left,
                            deadlineUs: navTime + 500_000,
                            requiredPage: targetPage,
                            requiredSlot: targetSlot,
                            resource: GameActionResource.Nav));
                        actions.Add(new GuitarAction(
                            navTime + navHoldUs,
                            GuitarActionType.PageNavUp,
                            navPage,
                            navScan,
                            MouseButton.Left,
                            deadlineUs: navTime + navHoldUs + 500_000,
                            requiredPage: targetPage,
                            requiredSlot: targetSlot,
                            resource: GameActionResource.Nav));
                        barrierTimes.Add(navTime);
                        cursorUs += stepDelayUs;
                        currentPage = navPage;
                    }
                }

                long selectTimeUs = cursorUs;
                actions.Add(new GuitarAction(
                    selectTimeUs,
                    GuitarActionType.WheelSelect,
                    targetSlot,
                    0,
                    MouseButton.Left,
                    deadlineUs: selectTimeUs + 500_000,
                    requiredPage: targetPage,
                    requiredSlot: targetSlot,
                    resource: GameActionResource.Wheel));
                barrierTimes.Add(selectTimeUs);
                currentSlot = targetSlot;
                lastControlFinishedUs = selectTimeUs + wheelSettleUs;
            }
            long effectiveTimeUs = Math.Max(targetAttackUs, lastControlFinishedUs);
            effectiveTimes[ev] = effectiveTimeUs;
            lastNoteTimeUs = effectiveTimeUs;
        }

        // 2. 按琴弦分组生成弦按键发音动作 (1~6 弦，过滤 IsSkipped 音符)
        var stringTriggers = new Dictionary<byte, List<(long TimeUs, long SourceTimeUs, long DurationUs, byte Page, byte Slot, byte NoteNumber)>>();
        foreach (var ev in fretEvents)
        {
            if (ev.IsSkipped || ignoredSameMomentStrings.Contains(ev)) continue;
            if (ev.StringNo < 1 || ev.StringNo > 6) continue;

            if (!stringTriggers.TryGetValue(ev.StringNo, out var list))
            {
                list = new List<(long TimeUs, long SourceTimeUs, long DurationUs, byte Page, byte Slot, byte NoteNumber)>();
                stringTriggers[ev.StringNo] = list;
            }
            var (resolvedPage, resolvedSlot) = ev.Page == 0 || ev.Slot == 0
                ? FretConverter.GetPageAndSlot(ev.Fret)
                : (ev.Page, ev.Slot);
            list.Add((effectiveTimes[ev], ev.TimeUs, ev.DurationUs, resolvedPage, resolvedSlot, ev.NoteNumber));
        }

        // 收集未被跳过音符的全局不重复起音时刻，用于检测整曲相邻音符间隙
        var uniqueFretMoments = fretEvents.Where(e => !e.IsSkipped && !ignoredSameMomentStrings.Contains(e))
                                          .Select(e => effectiveTimes[e])
                                          .Distinct()
                                          .OrderBy(t => t)
                                          .ToList();

        foreach (var kvp in stringTriggers)
        {
            byte stringNo = kvp.Key;
            ushort scanCode = (preset.StringScanCodes != null && preset.StringScanCodes.Length >= stringNo)
                ? preset.StringScanCodes[stringNo - 1]
                : GameScanCodes.ForGuitarString(stringNo);

            var items = kvp.Value;
            items.Sort((a, b) =>
            {
                int byEffectiveTime = a.TimeUs.CompareTo(b.TimeUs);
                return byEffectiveTime != 0 ? byEffectiveTime : a.SourceTimeUs.CompareTo(b.SourceTimeUs);
            });

            var filtered = new List<(long TimeUs, long SourceTimeUs, long DurationUs, byte Page, byte Slot, byte NoteNumber)>(items.Count);
            foreach (var it in items)
            {
                bool isDuplicate = filtered.Count > 0 &&
                    it.SourceTimeUs - filtered[^1].SourceTimeUs < options.MinRetriggerGapUs;
                if (!isDuplicate)
                {
                    filtered.Add(it);
                }
            }

            long lastUpTime = long.MinValue / 2;
            for (int i = 0; i < filtered.Count; i++)
            {
                long sourceTime = filtered[i].TimeUs;
                long downTime = Math.Max(sourceTime, lastUpTime + options.ReleaseGapUs);
                long rawDuration = filtered[i].DurationUs;
                bool isLastOnString = (i + 1 == filtered.Count);
                long nextDownTime = isLastOnString ? long.MaxValue : filtered[i + 1].TimeUs;

                int momentIdx = uniqueFretMoments.BinarySearch(sourceTime);
                bool isLastMomentInSong = (momentIdx >= 0 && momentIdx == uniqueFretMoments.Count - 1);
                long nextGlobalTime = (!isLastMomentInSong && momentIdx >= 0 && momentIdx + 1 < uniqueFretMoments.Count)
                    ? uniqueFretMoments[momentIdx + 1]
                    : long.MaxValue;

                long holdUs = CalculateDynamicHoldUs(downTime, rawDuration, isLastOnString, nextDownTime, options, nextGlobalTime, stringNo);
                long upTime = downTime + holdUs;

                // 核心修复 1: 延音释放边界保证 (upTime 绝不可跨越或碰撞 nextDownTime - ReleaseGapUs)
                if (!isLastOnString)
                {
                    long maxAllowedUp = nextDownTime - options.ReleaseGapUs;
                    if (upTime > maxAllowedUp)
                    {
                        upTime = maxAllowedUp;
                        if (upTime - downTime < Math.Max(5_000, options.MinHoldUs))
                        {
                            upTime = downTime + Math.Max(5_000, options.MinHoldUs);
                        }
                    }
                }

                // 核心修复 2: 控制屏障 (Control Barrier)
                // 若在延音期间存在翻页或选槽，在动作发生前提前 6ms 释放琴弦。
                foreach (var bTime in barrierTimes)
                {
                    if (bTime > downTime && bTime < upTime)
                    {
                        long safeUp = bTime - 6_000;
                        if (safeUp >= downTime + Math.Max(5_000, options.MinHoldUs))
                        {
                            upTime = safeUp;
                        }
                    }
                }

                actions.Add(new GuitarAction(
                    downTime,
                    GuitarActionType.StringDown,
                    stringNo,
                    scanCode,
                    MouseButton.Left,
                    deadlineUs: downTime + 30_000,
                    requiredPage: filtered[i].Page,
                    requiredSlot: filtered[i].Slot,
                    resource: GameActionResource.StringKeys,
                    fallbackPolicy: FallbackPolicy.Skip,
                    noteNumber: filtered[i].NoteNumber));

                actions.Add(new GuitarAction(
                    upTime,
                    GuitarActionType.StringUp,
                    stringNo,
                    scanCode,
                    MouseButton.Left,
                    deadlineUs: upTime + 60_000,
                    requiredPage: filtered[i].Page,
                    requiredSlot: filtered[i].Slot,
                    resource: GameActionResource.StringKeys));
                lastUpTime = upTime;
            }
        }

        // 3. 排序 (相同时间优先 Up -> PageNav -> WheelSelect -> StrumClick -> Down)
        actions.Sort((a, b) =>
        {
            int cmp = a.TimeUs.CompareTo(b.TimeUs);
            if (cmp != 0) return cmp;
            return GetActionPriority(a.Type).CompareTo(GetActionPriority(b.Type));
        });

        return actions;
    }

    /// <summary>
    /// 编译和弦与扫弦事件为动作序列 (兼容旧版重载)
    /// </summary>
    public static List<GuitarAction> CompileChordEvents(
        IReadOnlyList<ChordChangeEvent> chordEvents,
        IReadOnlyList<StrumEvent> strumEvents,
        WheelPreset preset,
        GuitarCompilerOptions? options = null)
        => CompileChordEvents(chordEvents, strumEvents, pluckEvents: null, preset, options);

    /// <summary>
    /// 编译和弦、扫弦与按键拨弦事件为动作序列 (支持 4 页 24 和弦与串行 R/T 快速翻页)
    /// </summary>
    public static List<GuitarAction> CompileChordEvents(
        IReadOnlyList<ChordChangeEvent> chordEvents,
        IReadOnlyList<StrumEvent> strumEvents,
        IReadOnlyList<ChordPluckEvent>? pluckEvents,
        WheelPreset preset,
        GuitarCompilerOptions? options = null)
    {
        options ??= new GuitarCompilerOptions();
        NormalizeMinimumTiming(options);
        var actions = new List<GuitarAction>();
        // 1. 和弦轮盘翻页与切换动作 (串行提前准备)
        byte currentPage = options.InitialPage > 0 ? options.InitialPage : (byte)1; // 和弦初始在指定起始页 (默认 P1)
        ChordId currentChord = ChordId.Unknown;

        if (chordEvents != null)
        {
            foreach (var ch in chordEvents)
            {
                if (ch.Chord != currentChord && ch.Chord != ChordId.Unknown)
                {
                    byte targetPage = (ch.Page > 0) ? ch.Page : (ChordAnalyzer.GetDefinition(ch.Chord)?.Page ?? 1);
                    byte targetSlot = (ch.Slot > 0) ? ch.Slot : (ChordAnalyzer.GetDefinition(ch.Chord)?.Slot ?? 1);

                    long selectTimeUs = ch.TimeUs - GetWheelSettleUs(options);
                    var (steps, direction) = CircularPageNavigator.GetShortestPath(currentPage, targetPage, 4);

                    if (steps > 0)
                    {
                        ushort navScan = direction > 0 ? preset.PageNextScanCode : preset.PagePrevScanCode; // T 或 R
                        long stepDelayUs = options.PageSwitchDelayUs;
                        for (int k = 0; k < steps; k++)
                        {
                            long navTime = selectTimeUs - (steps - k) * stepDelayUs;
                            byte navPage = CircularPageNavigator.StepPage(currentPage, direction, 4);
                            actions.Add(new GuitarAction(
                                navTime,
                                GuitarActionType.PageNav,
                                navPage,
                                navScan,
                                MouseButton.Left,
                                deadlineUs: selectTimeUs,
                                requiredPage: targetPage,
                                requiredSlot: targetSlot,
                                resource: GameActionResource.Nav));
                            actions.Add(new GuitarAction(
                                navTime + options.PageKeyHoldUs,
                                GuitarActionType.PageNavUp,
                                navPage,
                                navScan,
                                MouseButton.Left,
                                deadlineUs: selectTimeUs,
                                requiredPage: targetPage,
                                requiredSlot: targetSlot,
                                resource: GameActionResource.Nav));
                            currentPage = navPage;
                        }
                    }

                    actions.Add(new GuitarAction(
                        selectTimeUs,
                        GuitarActionType.WheelSelect,
                        targetSlot,
                        0,
                        MouseButton.Left,
                        deadlineUs: ch.TimeUs,
                        requiredPage: targetPage,
                        requiredSlot: targetSlot,
                        resource: GameActionResource.Wheel));
                    currentChord = ch.Chord;
                }
            }
        }

        // 2. 扫弦动作
        var strumList = new List<StrumEvent>();
        if (options.PlayStyle != GuitarPlayStyle.PluckOnly &&
            options.AutoStrum != AutoStrumMode.Disabled &&
            strumEvents != null)
        {
            bool alternateToggle = false;

            foreach (var st in strumEvents)
            {
                MouseButton btn;

                switch (options.AutoStrum)
                {
                    case AutoStrumMode.AllDown:
                        btn = preset.DownStrumButton;
                        break;

                    case AutoStrumMode.Alternate:
                        btn = alternateToggle ? preset.UpStrumButton : preset.DownStrumButton;
                        alternateToggle = !alternateToggle;
                        break;

                    case AutoStrumMode.FollowMidi:
                        btn = (st.Hint == StrumDirectionHint.HighToLow)
                            ? preset.UpStrumButton
                            : preset.DownStrumButton;
                        break;

                    case AutoStrumMode.Auto:
                    default:
                        if (st.Hint == StrumDirectionHint.LowToHigh)
                            btn = preset.DownStrumButton;
                        else if (st.Hint == StrumDirectionHint.HighToLow)
                            btn = preset.UpStrumButton;
                        else
                        {
                            btn = alternateToggle ? preset.UpStrumButton : preset.DownStrumButton;
                            alternateToggle = !alternateToggle;
                        }
                        break;
                }

                actions.Add(new GuitarAction(st.TimeUs, GuitarActionType.StrumClick, 0, 0, btn));
                strumList.Add(st);
            }
        }

        // 3. 琴弦按键拨弦动作
        if (options.PlayStyle != GuitarPlayStyle.StrumOnly && pluckEvents != null && pluckEvents.Count > 0)
        {
            List<ChordPluckEvent> plucksToEmit;

            if (options.PlayStyle == GuitarPlayStyle.PluckOnly || strumList.Count == 0)
            {
                plucksToEmit = pluckEvents.ToList();
            }
            else
            {
                // 智能弹拨: 排除扫弦窗口内的音符，保留独立单音与主旋律
                plucksToEmit = pluckEvents.Where(p =>
                {
                    bool coveredByStrum = strumList.Any(st =>
                        p.TimeUs >= (st.TimeUs - 10_000) && p.TimeUs <= (st.TimeUs + 45_000));
                    return !coveredByStrum;
                }).ToList();
            }

            CompilePluckStringActions(plucksToEmit, preset, options, actions);
        }

        actions.Sort((a, b) =>
        {
            int cmp = a.TimeUs.CompareTo(b.TimeUs);
            if (cmp != 0) return cmp;
            return GetActionPriority(a.Type).CompareTo(GetActionPriority(b.Type));
        });

        return actions;
    }

    /// <summary>
    /// 纯和弦模式动作编译器：使用游戏真实和弦音与安全弦表生成无毒音动作。
    /// </summary>
    public static List<GuitarAction> CompileChordDetailedEvents(
        IReadOnlyList<ChordEvent> chordEvents,
        WheelPreset preset,
        GuitarCompilerOptions? options = null)
    {
        if (chordEvents == null || chordEvents.Count == 0)
            return new List<GuitarAction>();

        options ??= new GuitarCompilerOptions();
        NormalizeMinimumTiming(options);
        var actions = new List<GuitarAction>();
        var lastStringUp = new Dictionary<byte, long>();
        var lastStringDown = new Dictionary<byte, long>();
        long lastControlFinishedUs = long.MinValue / 2;
        long lastAttackFinishedUs = long.MinValue / 2;
        byte currentPage = options.InitialPage is >= 1 and <= 4 ? options.InitialPage : (byte)1;
        byte currentSlot = 0;
        ChordId currentChord = ChordId.Unknown;
        bool alternateStrum = false;

        // Chord string attacks need at least 20 ms for the game to register them.
        long minHoldUs = Math.Max(20_000, options.MinHoldUs);
        long tapUs = Math.Max(minHoldUs, options.StringTapDurationUs);
        long navHoldUs = Math.Max(25_000, options.PageKeyHoldUs);
        long navGapUs = Math.Max(15_000, options.PageInterTapGapUs);
        long navStepUs = navHoldUs + navGapUs;
        long wheelSettleUs = GetWheelSettleUs(options);
        const long controlGraceUs = 500_000;

        foreach (var chordEvent in chordEvents.OrderBy(e => e.StartUs))
        {
            if (chordEvent.Chord == ChordId.Unknown ||
                !GameChordGroundTruth.Table.TryGetValue(chordEvent.Chord, out var truth))
                continue;

            byte targetPage = truth.Page;
            byte targetSlot = truth.Slot;
            long attackTimeUs = chordEvent.StartUs;
            bool needsSelection = currentChord != truth.Id || currentPage != targetPage || currentSlot != targetSlot;

            if (needsSelection)
            {
                var (pageSteps, pageDirection) = CircularPageNavigator.GetShortestPath(currentPage, targetPage, 4);
                long navDurationUs = pageSteps * navStepUs;
                long idealControlStartUs = attackTimeUs - wheelSettleUs - navDurationUs;
                long earliestControlStartUs = Math.Max(
                    lastControlFinishedUs + 6_000,
                    lastAttackFinishedUs > long.MinValue / 4 ? lastAttackFinishedUs + 6_000 : long.MinValue / 2);
                long controlStartUs = Math.Max(idealControlStartUs, earliestControlStartUs);
                long selectUs = controlStartUs + navDurationUs;
                long readyUs = selectUs + wheelSettleUs;
                if (readyUs > attackTimeUs) attackTimeUs = readyUs;
                if (attackTimeUs - chordEvent.StartUs > controlGraceUs) continue;

                ushort navScan = pageDirection > 0 ? preset.PageNextScanCode : preset.PagePrevScanCode;
                long cursorUs = controlStartUs;
                for (int step = 0; step < pageSteps; step++)
                {
                    byte navPage = CircularPageNavigator.StepPage(currentPage, pageDirection, 4);
                    actions.Add(new GuitarAction(cursorUs, GuitarActionType.PageNav, navPage, navScan,
                        deadlineUs: cursorUs + controlGraceUs, requiredPage: targetPage, requiredSlot: targetSlot,
                        resource: GameActionResource.Nav));
                    actions.Add(new GuitarAction(cursorUs + navHoldUs, GuitarActionType.PageNavUp, navPage, navScan,
                        deadlineUs: cursorUs + navHoldUs + controlGraceUs, requiredPage: targetPage, requiredSlot: targetSlot,
                        resource: GameActionResource.Nav));
                    cursorUs += navStepUs;
                    currentPage = navPage;
                }
                actions.Add(new GuitarAction(selectUs, GuitarActionType.WheelSelect, targetSlot,
                    deadlineUs: selectUs + controlGraceUs, requiredPage: targetPage, requiredSlot: targetSlot,
                    resource: GameActionResource.Wheel));
                currentSlot = targetSlot;
                currentChord = truth.Id;
                lastControlFinishedUs = readyUs;
            }

            // A delayed chord selection must also delay later attacks on the same chord.
            if (lastControlFinishedUs > attackTimeUs)
                attackTimeUs = lastControlFinishedUs;
            if (attackTimeUs - chordEvent.StartUs > controlGraceUs) continue;

            ChordArticulation articulation = chordEvent.Articulation;
            if (options.PlayStyle == GuitarPlayStyle.BlockOnly) articulation = ChordArticulation.Block;
            else if (options.PlayStyle == GuitarPlayStyle.StrumOnly) articulation = ChordArticulation.Strum;
            else if (options.PlayStyle == GuitarPlayStyle.PluckOnly) articulation = ChordArticulation.Arpeggio;

            if (articulation == ChordArticulation.Strum && options.AutoStrum != AutoStrumMode.Disabled)
            {
                MouseButton button = ResolveChordStrumButton(chordEvent.Direction, options.AutoStrum,
                    preset, ref alternateStrum);
                actions.Add(new GuitarAction(attackTimeUs, GuitarActionType.StrumClick, 0, 0, button,
                    deadlineUs: attackTimeUs + 60_000, requiredPage: targetPage, requiredSlot: targetSlot,
                    resource: GameActionResource.Mouse));
                lastAttackFinishedUs = Math.Max(lastAttackFinishedUs, attackTimeUs + 30_000);
                continue;
            }

            var sourceNotes = chordEvent.AttackNotes is { Count: > 0 }
                ? chordEvent.AttackNotes : chordEvent.SourceNotes;
            var uniqueSourceNotes = DeduplicateChordSourceNotes(sourceNotes);
            var targets = new List<(byte StringNo, byte NoteNumber, long TimeUs)>();
            if (articulation is ChordArticulation.Block or ChordArticulation.Strum)
            {
                var occupied = new HashSet<byte>();
                if (uniqueSourceNotes.Count > 0)
                {
                    var orderedNotes = articulation == ChordArticulation.Strum
                        ? uniqueSourceNotes.OrderBy(note => note.StartUs)
                        : uniqueSourceNotes.OrderByDescending(note => note.NoteNumber);
                    foreach (var note in orderedNotes)
                    {
                        byte stringNo = note.OrigString is >= 1 and <= 6 &&
                            truth.SafeCleanStrings.Contains((byte)note.OrigString) &&
                            !occupied.Contains((byte)note.OrigString)
                                ? (byte)note.OrigString
                                : FindSafeChordString(truth.Id, note.NoteNumber, occupied);
                        if (stringNo == 0)
                            stringNo = options.VoicingProfile.MatchNoteToString(note.NoteNumber, truth.Id, occupied, out _);
                        if (occupied.Contains(stringNo)) continue;
                        if (stringNo == 0) continue;
                        occupied.Add(stringNo);
                        // Keep the generated note offsets for a strum. The accompaniment
                        // generator staggers safe strings (8 ms each); collapsing them to
                        // attackTimeUs turns every fallback strum back into a block chord.
                        long targetTimeUs = articulation == ChordArticulation.Strum
                            ? Math.Max(attackTimeUs, note.StartUs)
                            : attackTimeUs;
                        targets.Add((stringNo, truth.ActualMidi1To6[stringNo - 1], targetTimeUs));
                    }
                }
                int minTargets = articulation == ChordArticulation.Strum
                    ? Math.Min(3, truth.SafeCleanStrings.Length)
                    : 1;
                if (targets.Count < minTargets)
                {
                    targets.Clear();
                    var fallbackStrings = articulation == ChordArticulation.Strum
                        ? chordEvent.Direction == StrumDirectionHint.HighToLow
                            ? truth.SafeCleanStrings.Order()
                            : truth.SafeCleanStrings.OrderDescending()
                        : truth.SafeCleanStrings.Take(3);
                    int index = 0;
                    foreach (byte stringNo in fallbackStrings.Take(articulation == ChordArticulation.Strum ? 4 : 3))
                    {
                        long staggerUs = articulation == ChordArticulation.Strum ? index * 8_000L : 0;
                        targets.Add((stringNo, truth.ActualMidi1To6[stringNo - 1], attackTimeUs + staggerUs));
                        index++;
                    }
                }
            }
            else if (uniqueSourceNotes.Count > 0)
            {
                var orderedNotes = uniqueSourceNotes.OrderBy(n => n.StartUs).ThenByDescending(n => n.NoteNumber).ToList();
                var occupied = new HashSet<byte>();
                long previousNoteTimeUs = long.MinValue;
                foreach (var note in orderedNotes)
                {
                    if (previousNoteTimeUs != long.MinValue && note.StartUs - previousNoteTimeUs > 15_000)
                        occupied.Clear();
                    byte stringNo = note.OrigString is >= 1 and <= 6 &&
                        truth.SafeCleanStrings.Contains((byte)note.OrigString) &&
                        !occupied.Contains((byte)note.OrigString)
                            ? (byte)note.OrigString
                            : FindSafeChordString(truth.Id, note.NoteNumber, occupied);
                    if (stringNo == 0)
                        stringNo = options.VoicingProfile.MatchNoteToString(note.NoteNumber, truth.Id, occupied, out _);
                    if (occupied.Contains(stringNo)) continue;
                    if (stringNo == 0) continue;
                    occupied.Add(stringNo);
                    targets.Add((stringNo, truth.ActualMidi1To6[stringNo - 1], Math.Max(attackTimeUs, note.StartUs)));
                    previousNoteTimeUs = note.StartUs;
                }
            }

            foreach (var target in targets)
            {
                byte stringNo = target.StringNo;
                if (stringNo is < 1 or > 6) continue;
                long releaseGapUs = Math.Max(22_000, options.ReleaseGapUs);
                long previousDownUs = lastStringDown.GetValueOrDefault(stringNo, long.MinValue / 2);
                long downUs = Math.Max(target.TimeUs, previousDownUs + minHoldUs + releaseGapUs);
                long previousUpUs = lastStringUp.GetValueOrDefault(stringNo, long.MinValue / 2);
                if (previousUpUs > downUs - releaseGapUs)
                {
                    long safeUpUs = Math.Max(previousDownUs + minHoldUs, downUs - releaseGapUs);
                    int previousUpIndex = actions.FindLastIndex(action =>
                        action.Type == GuitarActionType.StringUp && action.Value == stringNo);
                    if (previousUpIndex >= 0 && safeUpUs < previousUpUs)
                    {
                        var previousUp = actions[previousUpIndex];
                        actions[previousUpIndex] = new GuitarAction(safeUpUs, previousUp.Type, previousUp.Value,
                            previousUp.ScanCode, previousUp.MouseButton, safeUpUs + controlGraceUs,
                            previousUp.RequiredPage, previousUp.RequiredSlot, previousUp.Resource,
                            previousUp.FallbackPolicy, previousUp.NoteNumber);
                        lastStringUp[stringNo] = safeUpUs;
                    }
                }
                downUs = Math.Max(downUs, lastStringUp.GetValueOrDefault(stringNo, long.MinValue / 2) + releaseGapUs);
                long upUs = downUs + tapUs;
                ushort scanCode = preset.StringScanCodes != null && preset.StringScanCodes.Length >= stringNo
                    ? preset.StringScanCodes[stringNo - 1] : GameScanCodes.ForGuitarString(stringNo);
                actions.Add(new GuitarAction(downUs, GuitarActionType.StringDown, stringNo, scanCode,
                    deadlineUs: downUs + 60_000, requiredPage: targetPage, requiredSlot: targetSlot,
                    resource: GameActionResource.StringKeys, noteNumber: target.NoteNumber));
                actions.Add(new GuitarAction(upUs, GuitarActionType.StringUp, stringNo, scanCode,
                    deadlineUs: upUs + controlGraceUs, requiredPage: targetPage, requiredSlot: targetSlot,
                    resource: GameActionResource.StringKeys));
                lastStringUp[stringNo] = upUs;
                lastStringDown[stringNo] = downUs;
                lastAttackFinishedUs = Math.Max(lastAttackFinishedUs, upUs);
            }
        }

        actions.Sort((a, b) =>
        {
            int compare = a.TimeUs.CompareTo(b.TimeUs);
            return compare != 0 ? compare : GetActionPriority(a.Type).CompareTo(GetActionPriority(b.Type));
        });
        return actions;
    }

    private static byte FindSafeChordString(ChordId chord, byte noteNumber, IReadOnlySet<byte> occupied)
    {
        byte exact = GameChordGroundTruth.FindExactCleanString(chord, noteNumber, occupied);
        return exact != 0 ? exact : GameChordGroundTruth.FindPitchClassCompatibleString(chord, noteNumber, occupied);
    }

    private static List<RawMidiNote> DeduplicateChordSourceNotes(IReadOnlyList<RawMidiNote> sourceNotes)
    {
        const long simultaneousToleranceUs = 22_000;
        var result = new List<RawMidiNote>(sourceNotes.Count);
        var latestByPitch = new Dictionary<byte, (long AnchorUs, int Index)>();
        foreach (var note in sourceNotes.OrderBy(note => note.StartUs))
        {
            if (latestByPitch.TryGetValue(note.NoteNumber, out var previous) &&
                note.StartUs - previous.AnchorUs < simultaneousToleranceUs)
            {
                var existing = result[previous.Index];
                if (note.Velocity > existing.Velocity ||
                    (note.Velocity == existing.Velocity && note.DurationUs > existing.DurationUs))
                    result[previous.Index] = note;
                continue;
            }
            latestByPitch[note.NoteNumber] = (note.StartUs, result.Count);
            result.Add(note);
        }
        return result;
    }

    private static MouseButton ResolveChordStrumButton(
        StrumDirectionHint direction, AutoStrumMode mode, WheelPreset preset, ref bool alternate)
    {
        if (mode == AutoStrumMode.AllDown) return preset.DownStrumButton;
        if (mode == AutoStrumMode.Alternate)
        {
            var button = alternate ? preset.UpStrumButton : preset.DownStrumButton;
            alternate = !alternate;
            return button;
        }
        if (mode == AutoStrumMode.FollowMidi)
            return direction == StrumDirectionHint.HighToLow ? preset.UpStrumButton : preset.DownStrumButton;
        if (direction == StrumDirectionHint.HighToLow) return preset.UpStrumButton;
        if (direction == StrumDirectionHint.LowToHigh) return preset.DownStrumButton;
        var next = alternate ? preset.UpStrumButton : preset.DownStrumButton;
        alternate = !alternate;
        return next;
    }
    private static void CompilePluckStringActions(
        IReadOnlyList<ChordPluckEvent> pluckEvents,
        WheelPreset preset,
        GuitarCompilerOptions options,
        List<GuitarAction> actions)
    {
        if (pluckEvents == null || pluckEvents.Count == 0) return;

        var stringTriggers = new Dictionary<byte, List<(long TimeUs, long DurationUs)>>();
        foreach (var ev in pluckEvents)
        {
            if (ev.StringNo < 1 || ev.StringNo > 6) continue;
            if (!stringTriggers.TryGetValue(ev.StringNo, out var list))
            {
                list = new List<(long TimeUs, long DurationUs)>();
                stringTriggers[ev.StringNo] = list;
            }
            list.Add((ev.TimeUs, ev.DurationUs));
        }

        // 收集未被跳过拨弦事件的全局不重复起音时刻
        var uniquePluckMoments = pluckEvents.Select(p => p.TimeUs).Distinct().OrderBy(t => t).ToList();

        foreach (var kvp in stringTriggers)
        {
            byte stringNo = kvp.Key;
            ushort scanCode = (preset.StringScanCodes != null && preset.StringScanCodes.Length >= stringNo)
                ? preset.StringScanCodes[stringNo - 1]
                : GameScanCodes.ForGuitarString(stringNo);

            var items = kvp.Value;
            items.Sort((a, b) => a.TimeUs.CompareTo(b.TimeUs));

            var filtered = new List<(long TimeUs, long DurationUs)>(items.Count);
            foreach (var it in items)
            {
                if (filtered.Count == 0 || (it.TimeUs - filtered[^1].TimeUs) >= options.MinRetriggerGapUs)
                {
                    filtered.Add(it);
                }
            }

            long lastUpTime = long.MinValue / 2;
            for (int i = 0; i < filtered.Count; i++)
            {
                long sourceTime = filtered[i].TimeUs;
                long downTime = Math.Max(sourceTime, lastUpTime + options.ReleaseGapUs);
                long rawDuration = filtered[i].DurationUs;
                bool isLastOnString = (i + 1 == filtered.Count);
                long nextDownTime = isLastOnString ? long.MaxValue : filtered[i + 1].TimeUs;

                int momentIdx = uniquePluckMoments.BinarySearch(sourceTime);
                bool isLastMomentInSong = (momentIdx >= 0 && momentIdx == uniquePluckMoments.Count - 1);
                long nextGlobalTime = (!isLastMomentInSong && momentIdx >= 0 && momentIdx + 1 < uniquePluckMoments.Count)
                    ? uniquePluckMoments[momentIdx + 1]
                    : long.MaxValue;

                long holdUs = CalculateDynamicHoldUs(downTime, rawDuration, isLastOnString, nextDownTime, options, nextGlobalTime, stringNo);
                long upTime = downTime + holdUs;

                if (!isLastOnString)
                {
                    long maxAllowedUp = nextDownTime - options.ReleaseGapUs;
                    if (upTime > maxAllowedUp)
                    {
                        upTime = maxAllowedUp;
                        if (upTime - downTime < options.MinHoldUs)
                        {
                            upTime = downTime + options.MinHoldUs;
                        }
                    }
                }

                actions.Add(new GuitarAction(downTime, GuitarActionType.StringDown, stringNo, scanCode));
                actions.Add(new GuitarAction(upTime, GuitarActionType.StringUp, stringNo, scanCode));
                lastUpTime = upTime;
            }
        }
    }

    /// <summary>
    /// 动态计算琴弦按键保持时长:
    /// 1. 最后一个音符且乐谱明确有延音 -> 保持饱满延音 (最高可达 MaxSustainUs)
    /// 2. 与下一个同弦或全局音符太近 (< CloseNotesThresholdUs 280ms) -> 严禁延音，立即使用点触 StringTapDurationUs 释放，留足间隔防吞音
    /// 3. 与下一个音符间隔充分 (>= 280ms) 且乐谱有延音 -> 保持延音，同时保证在下一个音符前至少留出 ReleaseGapUs
    /// </summary>
    public static long CalculateDynamicHoldUs(
        long downTime,
        long rawDurationUs,
        bool isLastOnString,
        long nextDownTime,
        GuitarCompilerOptions options,
        long nextGlobalTime = long.MaxValue,
        byte stringNo = 0)
    {
        NormalizeMinimumTiming(options);
        if (!options.EnableSustain || rawDurationUs <= options.StringTapDurationUs)
        {
            return options.StringTapDurationUs;
        }

        // 核心规则: 若整曲下一个音符在 280ms 内到来 (密集旋律/琶音进行)，严禁长按延音，必须极速点按释放
        if (nextGlobalTime != long.MaxValue && (nextGlobalTime - downTime) < options.CloseNotesThresholdUs)
        {
            return options.StringTapDurationUs;
        }

        if (isLastOnString)
        {
            // 规则 1: 最后一个音符且乐谱里明确他有延音，保持延音
            return Math.Clamp(rawDurationUs, options.StringTapDurationUs, options.MaxSustainUs);
        }

        long gapToNextDown = nextDownTime - downTime;
        if (gapToNextDown < options.CloseNotesThresholdUs)
        {
            // 规则 2: 同弦两音离得很近 (<280ms)，严禁长按，采用快速点触释放，避免连按碰撞吞音
            return options.StringTapDurationUs;
        }

        // 规则 3: 间隔充分且乐谱明确有延音，保持延音，并留足 ReleaseGap
        long maxHold = gapToNextDown - Math.Max(25_000, options.ReleaseGapUs);

        if (maxHold >= options.StringTapDurationUs)
        {
            return Math.Clamp(Math.Min(rawDurationUs, maxHold), options.StringTapDurationUs, options.MaxSustainUs);
        }

        return options.StringTapDurationUs;
    }

    private static void NormalizeMinimumTiming(GuitarCompilerOptions options)
    {
        options.MinHoldUs = Math.Max(20_000, options.MinHoldUs);
        options.StringTapDurationUs = Math.Max(options.MinHoldUs, options.StringTapDurationUs);
        options.PageKeyHoldUs = Math.Max(25_000, options.PageKeyHoldUs);
        options.ReleaseGapUs = Math.Max(22_000, options.ReleaseGapUs);
        options.MinRetriggerGapUs = Math.Max(25_000, options.MinRetriggerGapUs);
    }

    private static long GetWheelSettleUs(GuitarCompilerOptions options) =>
        Math.Max(55_000, options.WheelMouseSettleUs);

    private static int GetActionPriority(GuitarActionType type) => type switch
    {
        GuitarActionType.StringUp => 0,
        GuitarActionType.PageNavUp => 0,
        GuitarActionType.PageNav => 1,
        GuitarActionType.WheelSelect => 2,
        GuitarActionType.StrumClick => 3,
        GuitarActionType.StringDown => 4,
        _ => 5
    };
}
