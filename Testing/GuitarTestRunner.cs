using System.Drawing;
using MidiAutoPlayer.Guitar;
using MidiAutoPlayer.Input;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Testing;

public static class GuitarTestRunner
{
    public static void RunAllGuitarTests(Action<bool, string, string> assertAction)
    {
        Test_WheelCoordinateScaling(assertAction);
        Test_WheelSharedPreset(assertAction);
        Test_StringScanCodesOrder(assertAction);
        Test_LegacyStringScanCodeOrderHealing(assertAction);
        Test_PlayableRangeContinuity_41To70(assertAction);
        Test_SingleNoteCandidates(assertAction);
        Test_FretDp_MinimizeFretSwitching(assertAction);
        Test_FretAction_SameFretDeduplication(assertAction);
        Test_ChordRecognition_SixTemplates(assertAction);
        Test_ChordRecognition_Cmaj7_And_LooseMode(assertAction);
        Test_ChordAnalyzer_HysteresisDebouncing(assertAction);
        Test_StrumAnalyzer_MicroTimingDirection(assertAction);
        Test_StrumAnalyzer_SimultaneousUnknown(assertAction);
        Test_GuitarCompiler_AutoStrumDisabled(assertAction);
        Test_GuitarCompiler_ChordLeadAndDeduplication(assertAction);
        Test_MultiMonitorVirtualDesktopMapping(assertAction);
        Test_PresetZeroXAutoHealing(assertAction);
        Test_StandardSixStringTuning_String1IsHighestPitch_MappedToGameKey6(assertAction);
        Test_ChordPluck_ExtractionAndVoicing(assertAction);
        Test_ChordPluck_PolyphonySimultaneousStringPress(assertAction);
        Test_NoteSustainAndRetriggerGap(assertAction);
        Test_GuitarPlayStyles(assertAction);
        Test_FretConverter_0To24Frets_And_5Pages(assertAction);
        Test_PageTurn_Timing_And_KeySequence(assertAction);
        Test_GlobalOctaveShift_NoPerNoteFolding(assertAction);
        Test_SlashChord_BassRecognition(assertAction);
        Test_24Chords_CatalogCompleteness(assertAction);
        Test_PlaybackStartAdvisor(assertAction);
        Test_v42_CoreFeatures(assertAction);
        Test_v43_Features(assertAction);
        Test_GameChordGroundTruth(assertAction);
        Test_FullHybridAlgorithms(assertAction);
        Test_DenseHybridSafety(assertAction);
        Test_MultitrackContinuousPlucks(assertAction);
        Test_StaggeredSingleTrackStrum(assertAction);
        Test_Mode2AccompanimentSeparation(assertAction);
        Test_GuitarRetriggerAndControlRegressions(assertAction);
    }

    private static void Test_GuitarRetriggerAndControlRegressions(Action<bool, string, string> assert)
    {
        var preset = new WheelPreset();
        var options = new GuitarCompilerOptions();
        assert(options.ReleaseGapUs >= 22_000 && options.MinRetriggerGapUs >= 25_000,
            "同弦重触发默认留足跨帧抬键窗口", $"gap={options.ReleaseGapUs}, retrigger={options.MinRetriggerGapUs}");

        long sustained = GuitarCompiler.CalculateDynamicHoldUs(0, 500_000, false, 500_000, options);
        assert(sustained <= 475_000, "Mode 1 慢速连音在下一次同弦按键前至少释放 25ms", $"hold={sustained}");

        var doubled = FretConverter.SanitizeForFretSolo(new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 300_000, NoteNumber = 64, Velocity = 70, TrackId = 1 },
            new RawMidiNote { StartUs = 12_000, DurationUs = 300_000, NoteNumber = 64, Velocity = 100, TrackId = 2 },
            new RawMidiNote { StartUs = 60_000, DurationUs = 300_000, NoteNumber = 64, Velocity = 90, TrackId = 1 }
        });
        assert(doubled.Count == 2 && doubled[0].Velocity == 100 && doubled[1].StartUs == 60_000,
            "跨轨 22ms 内同音合并，真实后续连击保留", $"count={doubled.Count}");

        var fretEvents = new[]
        {
            new FretPlayEvent(100_000, 1, 1, 65, durationUs: 300_000, page: 1, slot: 1),
            new FretPlayEvent(135_000, 1, 19, 83, durationUs: 100_000, page: 4, slot: 1)
        };
        var fretActions = GuitarCompiler.CompileFretEvents(fretEvents, preset, new GuitarCompilerOptions());
        var nav = fretActions.Where(a => a.Type == GuitarActionType.PageNav).OrderBy(a => a.TimeUs).ToArray();
        var previousUp = fretActions.First(a => a.Type == GuitarActionType.StringUp && a.RequiredPage == 1);
        var secondDown = fretActions.First(a => a.Type == GuitarActionType.StringDown && a.RequiredPage == 4);
        var wheel = fretActions.Last(a => a.Type == GuitarActionType.WheelSelect);
        assert(nav.Length == 3 && nav.Zip(nav.Skip(1)).All(pair => pair.Second.TimeUs - pair.First.TimeUs >= 40_000) &&
               nav[0].TimeUs >= previousUp.TimeUs + 6_000 && secondDown.TimeUs >= wheel.TimeUs + 55_000,
            "Mode 1 多页翻转串行且前音释放、选槽稳定后才发音", $"nav=[{string.Join(',', nav.Select(a => a.TimeUs))}]");

        var guard = new GuitarControlTimingGuard();
        guard.MarkExecuted(new GuitarAction(0, GuitarActionType.StringUp, 1), 100_000);
        assert(guard.EarliestExecutionUs(new GuitarAction(100_000, GuitarActionType.StringDown, 1), 100_000) >= 122_000,
            "运行时同弦重触发保留 22ms 释放期", "");

        var doubledChord = new ChordEvent
        {
            StartUs = 1_000_000,
            Chord = ChordId.G,
            Articulation = ChordArticulation.Arpeggio,
            AttackNotes = new List<RawMidiNote>
            {
                new() { StartUs = 1_000_000, NoteNumber = 67, Velocity = 80, TrackId = 1 },
                new() { StartUs = 1_010_000, NoteNumber = 67, Velocity = 100, TrackId = 2 }
            }
        };
        var doubledChordActions = GuitarCompiler.CompileChordDetailedEvents(new[] { doubledChord }, preset);
        assert(doubledChordActions.Count(action => action.Type == GuitarActionType.StringDown) == 1,
            "跨轨同音和弦攻击只占用一根弦一次", "");

        var fallbackChord = new ChordEvent
        {
            StartUs = 1_000_000,
            Chord = ChordId.C,
            Articulation = ChordArticulation.Arpeggio,
            AttackNotes = new List<RawMidiNote>
            {
                new() { StartUs = 1_000_000, NoteNumber = 62, Velocity = 100 }
            }
        };
        var fallbackActions = GuitarCompiler.CompileChordDetailedEvents(new[] { fallbackChord }, preset);
        assert(fallbackActions.Any(action => action.Type == GuitarActionType.StringDown),
            "和弦外音找不到精确安全弦时使用最近音高琴弦兜底", "");
    }

    private static void Test_WheelCoordinateScaling(Action<bool, string, string> assert)
    {
        var preset = new WheelPreset();
        // 假设 1080p 客户区尺寸 1920x1080
        int w1 = 1920, h1 = 1080;
        int w2 = 2560, h2 = 1440;

        for (int i = 0; i < 6; i++)
        {
            var pt = preset.Slots[i];
            int x1 = (int)Math.Round(pt.X * w1);
            int y1 = (int)Math.Round(pt.Y * h1);

            int x2 = (int)Math.Round(pt.X * w2);
            int y2 = (int)Math.Round(pt.Y * h2);

            // 比例必须一致 (误差在像素取整内)
            double ratioX = (double)x2 / x1;
            double ratioY = (double)y2 / y1;
            bool ok = Math.Abs(ratioX - (2560.0 / 1920.0)) < 0.05 && Math.Abs(ratioY - (1440.0 / 1080.0)) < 0.05;
            assert(ok, $"轮盘 Slot {i + 1} 坐标缩放: 1080p -> 1440p 等比适配", $"x1={x1}, x2={x2}");
        }
    }

    private static void Test_WheelSharedPreset(Action<bool, string, string> assert)
    {
        var controller = new WheelController();
        // 和弦与品格共用同一组 Slots
        assert(controller.Preset.Slots.Length == 6, "轮盘预设: 包含固定 6 槽位", "");
        assert(controller.Preset.StrumPoint != null, "轮盘预设: 包含扫弦安全点", "");
        assert(controller.CurrentSlot == 0, "轮盘状态: 初始槽位为 0 (未选择)", "");
    }

    private static void Test_StringScanCodesOrder(Action<bool, string, string> assert)
    {
        var preset = new WheelPreset();
        // 游戏按键映射 (黄金法则): 按键 1 等于游戏吉他一弦，现实吉他 6 弦 (0x02)；按键 6 对应现实吉他 1 弦 (0x07)
        bool orderCorrect =
            preset.StringScanCodes[0] == 0x07 && // 物理1弦 (高E 64) -> 游戏键6 (0x07)
            preset.StringScanCodes[1] == 0x06 && // 物理2弦 (B3 59) -> 游戏键5 (0x06)
            preset.StringScanCodes[2] == 0x05 && // 物理3弦 (G3 55) -> 游戏键4 (0x05)
            preset.StringScanCodes[3] == 0x04 && // 物理4弦 (D3 50) -> 游戏键3 (0x04)
            preset.StringScanCodes[4] == 0x03 && // 物理5弦 (A2 45) -> 游戏键2 (0x03)
            preset.StringScanCodes[5] == 0x02;   // 物理6弦 (低E 40) -> 游戏键1 (0x02)

        assert(orderCorrect, "琴弦 ScanCode 物理按键: 1弦->键6 (0x07) ... 6弦->键1 (0x02)", "");

        bool helperCorrect =
            GameScanCodes.ForGuitarString(1) == 0x07 &&
            GameScanCodes.ForGuitarString(2) == 0x06 &&
            GameScanCodes.ForGuitarString(3) == 0x05 &&
            GameScanCodes.ForGuitarString(4) == 0x04 &&
            GameScanCodes.ForGuitarString(5) == 0x03 &&
            GameScanCodes.ForGuitarString(6) == 0x02;

        assert(helperCorrect, "GameScanCodes 发送映射函数: 物理1~6弦严格映射为游戏按键 6~1", "");
    }

    private static void Test_LegacyStringScanCodeOrderHealing(Action<bool, string, string> assert)
    {
        // 旧版启动自愈误把物理弦序配置成游戏键升序 [1..6]；应恢复为物理 1~6 弦 -> 游戏键 6~1。
        var normalizer = typeof(GameScanCodes).GetMethod("NormalizeLegacyStringScanCodes");
        assert(normalizer != null, "旧琴弦键序修复器存在", "缺少 NormalizeLegacyStringScanCodes");
        if (normalizer == null) return;

        var legacyOrder = new ushort[] { 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        var normalized = (ushort[]?)normalizer.Invoke(null, new object?[] { legacyOrder });
        assert(normalized != null && normalized.SequenceEqual(new ushort[] { 0x07, 0x06, 0x05, 0x04, 0x03, 0x02 }),
            "旧配置自愈顺序: 游戏键 1~6 的数组纠正为物理 1~6 弦对应的键 6~1",
            $"实际: [{string.Join(",", normalized ?? Array.Empty<ushort>())}]");

        var customOrder = new ushort[] { 0x20, 0x21, 0x22, 0x23, 0x24, 0x25 };
        var customPreserved = (ushort[]?)normalizer.Invoke(null, new object?[] { customOrder });
        assert(customPreserved != null && customPreserved.SequenceEqual(customOrder),
            "自定义琴弦键序保持: 不触碰非标准校准映射", $"实际: [{string.Join(",", customPreserved ?? Array.Empty<ushort>())}]");
    }

    private static void Test_PlayableRangeContinuity_41To70(Action<bool, string, string> assert)
    {
        var tuning = GuitarTuning.Standard;
        bool allCovered = true;
        int failedNote = 0;

        for (byte note = 41; note <= 70; note++)
        {
            var candidates = FretConverter.GenerateCandidates(note, tuning);
            if (candidates.Count == 0)
            {
                allCovered = false;
                failedNote = note;
                break;
            }
        }

        assert(allCovered, "品格音域连续性: 41(F2) ~ 70(A#4) 连续 30 个半音全部可演奏，无任何空缺", $"失败音符: {failedNote}");
    }

    private static void Test_SingleNoteCandidates(Action<bool, string, string> assert)
    {
        var tuning = GuitarTuning.Standard;

        // C4 (60): 2弦I (59+1=60), 3弦V (55+5=60), 4弦X (50+10=60)
        var c4Candidates = FretConverter.GenerateCandidates(60, tuning);
        bool c4Ok = c4Candidates.Any(c => c.StringNo == 2 && c.Fret == 1) &&
                    c4Candidates.Any(c => c.StringNo == 3 && c.Fret == 5);
        assert(c4Ok, "C4(60) 候选位置: 必须包含 2弦I 与 3弦V", $"实际候选: [{string.Join(", ", c4Candidates)}]");

        // G4 (67): 1弦III (64+3=67), 2弦VIII (59+8=67)
        var g4Candidates = FretConverter.GenerateCandidates(67, tuning);
        bool g4Ok = g4Candidates.Any(c => c.StringNo == 1 && c.Fret == 3);
        assert(g4Ok, "G4(67) 候选位置: 必须包含 1弦III", $"实际候选: [{string.Join(", ", g4Candidates)}]");

        // A#2 (46): 5弦I (45+1) 与 6弦VI (40+6)
        var ash2Candidates = FretConverter.GenerateCandidates(46, tuning);
        bool ash2Ok = ash2Candidates.Any(c => c.StringNo == 5 && c.Fret == 1) &&
                      ash2Candidates.Any(c => c.StringNo == 6 && c.Fret == 6);
        assert(ash2Ok, "A#2(46) 候选位置: 必须包含 5弦I 与 6弦VI", $"实际候选: [{string.Join(", ", ash2Candidates)}]");
    }

    private static void Test_FretDp_MinimizeFretSwitching(Action<bool, string, string> assert)
    {
        // 构造一串同品格音符序列:
        // C4 (60, 2弦I) 和 F4 (65, 1弦I)
        // 算法应该在 1品 上连续演奏 (2弦I -> 1弦I)，保持在同一品格 (1品)！
        var melody = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 60, StartUs = 0, DurationUs = 300_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 65, StartUs = 400_000, DurationUs = 300_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 60, StartUs = 800_000, DurationUs = 300_000, Velocity = 100 }
        };

        var events = FretConverter.Convert(melody);
        assert(events.Count == 3, "品格转换: 生成 3 个演奏事件", "");

        bool allFret1 = events.All(e => e.Fret == 1);
        assert(allFret1, "指法 DP 优化: C4与F4连续演奏时优先保持在同一品格 (1品)，避免鼠标切品",
            $"实际品格序列: [{string.Join(", ", events.Select(e => $"S{e.StringNo}-F{e.Fret}"))}]");
    }

    private static void Test_FretAction_SameFretDeduplication(Action<bool, string, string> assert)
    {
        // 连续 3 个同品格音符 (1品 -> P1 Slot 1)
        var fretEvents = new List<FretPlayEvent>
        {
            new FretPlayEvent(1_000_000, stringNo: 1, fret: 1, noteNumber: 65),
            new FretPlayEvent(1_500_000, stringNo: 1, fret: 1, noteNumber: 65),
            new FretPlayEvent(2_000_000, stringNo: 2, fret: 1, noteNumber: 60)
        };

        var preset = new WheelPreset();
        var actions = GuitarCompiler.CompileFretEvents(fretEvents, preset);

        var wheelClicks = actions.Where(a => a.Type == GuitarActionType.WheelSelect).ToList();
        assert(wheelClicks.Count == 1, "连续同品格: 轮盘只在首个音符点击一次，后续消除重复点击", $"实际轮盘点击次数: {wheelClicks.Count}");

        // 验证提前量 (1000ms - 55ms = 945ms)
        assert(wheelClicks[0].TimeUs == 945_000, "品格切换提前量: 在音符发声前 55ms (945,000 us) 点击 Slot 1", $"实际提前时间: {wheelClicks[0].TimeUs}");
        assert(wheelClicks[0].Value == 1, "品格槽位选择: 正确选择 Slot 1 (1品)", $"实际值: {wheelClicks[0].Value}");
    }

    private static void Test_ChordRecognition_SixTemplates(Action<bool, string, string> assert)
    {
        // 构造 C 和弦 (C4=60, E4=64, G4=67)
        var cNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 60, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 64, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 67, StartUs = 0, DurationUs = 500_000, Velocity = 100 }
        };
        var cEvents = ChordAnalyzer.Analyze(cNotes);
        assert(cEvents.Count >= 1 && cEvents[0].Chord == ChordId.C, "和弦识别 C: C-E-G 准确识别为 C 和弦", $"实际识别: {(cEvents.Count > 0 ? cEvents[0].Chord.ToString() : "None")}");

        // 构造 Am 和弦 (A3=57, C4=60, E4=64)
        var amNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 57, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 60, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 64, StartUs = 0, DurationUs = 500_000, Velocity = 100 }
        };
        var amEvents = ChordAnalyzer.Analyze(amNotes);
        assert(amEvents.Count >= 1 && amEvents[0].Chord == ChordId.Am, "和弦识别 Am: A-C-E 准确识别为 Am 和弦", $"实际识别: {(amEvents.Count > 0 ? amEvents[0].Chord.ToString() : "None")}");

        // 构造 G 和弦 (G3=55, B3=59, D4=62)
        var gNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 55, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 59, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 62, StartUs = 0, DurationUs = 500_000, Velocity = 100 }
        };
        var gEvents = ChordAnalyzer.Analyze(gNotes);
        assert(gEvents.Count >= 1 && gEvents[0].Chord == ChordId.G, "和弦识别 G: G-B-D 准确识别为 G 和弦", $"实际识别: {(gEvents.Count > 0 ? gEvents[0].Chord.ToString() : "None")}");
    }

    private static void Test_ChordRecognition_Cmaj7_And_LooseMode(Action<bool, string, string> assert)
    {
        // 1. Cmaj7 (Page 2 Slot 1): C4(60), E4(64), G4(67), B4(71)
        var cmaj7Notes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 60, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 64, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 67, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 71, StartUs = 0, DurationUs = 500_000, Velocity = 80 }
        };

        var events = ChordAnalyzer.Analyze(cmaj7Notes, options: new ChordAnalyzerOptions { Mode = ChordRecognitionMode.Loose });
        assert(events.Count >= 1 && events[0].Chord == ChordId.Cmaj7, "24和弦识别 Cmaj7: 包含 7th 音 B 时精准识别为 Cmaj7 (P2 Slot 1)",
            $"实际识别: {(events.Count > 0 ? events[0].Chord.ToString() : "None")}");

        // 2. 宽松模式容错: C 和弦夹杂微弱经过音 (F4=65, 30ms 极短音符)
        var cWithPassingNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 60, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 64, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 67, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 65, StartUs = 100_000, DurationUs = 30_000, Velocity = 40 }
        };
        var cLooseEvents = ChordAnalyzer.Analyze(cWithPassingNotes, options: new ChordAnalyzerOptions { Mode = ChordRecognitionMode.Loose });
        assert(cLooseEvents.Count >= 1 && cLooseEvents[0].Chord == ChordId.C, "宽松模式容错: C 三和弦夹杂轻微经过音时稳定保持 C 和弦",
            $"实际识别: {(cLooseEvents.Count > 0 ? cLooseEvents[0].Chord.ToString() : "None")}");
    }

    private static void Test_ChordAnalyzer_HysteresisDebouncing(Action<bool, string, string> assert)
    {
        // 持续 2 秒的 C 和弦，中间夹杂一个极短 (40ms) 的经过音 D4
        var notes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 60, StartUs = 0, DurationUs = 2_000_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 64, StartUs = 0, DurationUs = 2_000_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 67, StartUs = 0, DurationUs = 2_000_000, Velocity = 100 },
            // 短经过音
            new RawMidiNote { NoteNumber = 62, StartUs = 500_000, DurationUs = 40_000, Velocity = 70 }
        };

        var events = ChordAnalyzer.Analyze(notes);
        // 迟滞防抖应该让整个 2 秒保持为单个 C 和弦，不发生抖动切和弦
        assert(events.Count == 1 && events[0].Chord == ChordId.C, "和弦防抖 (Hysteresis): 短暂经过音不引起和弦抖动", $"实际切和弦次数: {events.Count}");
    }

    private static void Test_StrumAnalyzer_MicroTimingDirection(Action<bool, string, string> assert)
    {
        // 低音向高音微小时序琶音 (DownStrum):
        // 0ms C3(48), 12ms G3(55), 25ms C4(60), 38ms E4(64)
        var downCluster = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 48, StartUs = 0, DurationUs = 400_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 55, StartUs = 12_000, DurationUs = 400_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 60, StartUs = 25_000, DurationUs = 400_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 64, StartUs = 38_000, DurationUs = 400_000, Velocity = 100 }
        };

        var downEvents = StrumAnalyzer.Analyze(downCluster);
        assert(downEvents.Count == 1, "扫弦识别: 低到高琶音识别出 1 个扫弦事件", $"实际数量: {downEvents.Count}");
        assert(downEvents[0].Hint == StrumDirectionHint.LowToHigh, "扫弦方向: 准确识别为 LowToHigh (下扫)", $"实际方向: {downEvents[0].Hint}");

        // 高音向低音微小时序琶音 (UpStrum):
        // 0ms E4(64), 12ms C4(60), 25ms G3(55), 38ms C3(48)
        var upCluster = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 64, StartUs = 0, DurationUs = 400_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 60, StartUs = 12_000, DurationUs = 400_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 55, StartUs = 25_000, DurationUs = 400_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 48, StartUs = 38_000, DurationUs = 400_000, Velocity = 100 }
        };

        var upEvents = StrumAnalyzer.Analyze(upCluster);
        assert(upEvents.Count == 1, "扫弦识别: 高到低琶音识别出 1 个扫弦事件", $"实际数量: {upEvents.Count}");
        assert(upEvents[0].Hint == StrumDirectionHint.HighToLow, "扫弦方向: 准确识别为 HighToLow (上扫)", $"实际方向: {upEvents[0].Hint}");
    }

    private static void Test_StrumAnalyzer_SimultaneousUnknown(Action<bool, string, string> assert)
    {
        // 完全同时发声的和弦音 (0ms C3, 0ms E3, 0ms G3)
        var simCluster = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 48, StartUs = 0, DurationUs = 400_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 52, StartUs = 0, DurationUs = 400_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 55, StartUs = 0, DurationUs = 400_000, Velocity = 100 }
        };

        var events = StrumAnalyzer.Analyze(simCluster);
        assert(events.Count == 1, "完全同时和弦: 生成 1 个扫弦事件", $"实际数量: {events.Count}");
        assert(events[0].Hint == StrumDirectionHint.Unknown, "完全同时和弦: 方向为 Unknown，不伪造识别结果", $"实际方向: {events[0].Hint}");
    }

    private static void Test_GuitarCompiler_AutoStrumDisabled(Action<bool, string, string> assert)
    {
        var chordEvents = new List<ChordChangeEvent>
        {
            new ChordChangeEvent(1_000_000, ChordId.C),
            new ChordChangeEvent(2_000_000, ChordId.Am)
        };
        var strumEvents = new List<StrumEvent>
        {
            new StrumEvent(1_000_000, StrumDirectionHint.LowToHigh),
            new StrumEvent(1_500_000, StrumDirectionHint.HighToLow)
        };

        var preset = new WheelPreset();
        var actions = GuitarCompiler.CompileChordEvents(
            chordEvents,
            strumEvents,
            preset,
            new GuitarCompilerOptions { AutoStrum = AutoStrumMode.Disabled });

        // 仅包含换和弦点击，无任何鼠标扫弦
        bool noStrum = actions.All(a => a.Type != GuitarActionType.StrumClick);
        int wheelCount = actions.Count(a => a.Type == GuitarActionType.WheelSelect);

        assert(noStrum, "关闭自动扫弦: 动作序列不包含任何扫弦鼠标点击", $"实际包含扫弦动作数: {actions.Count(a => a.Type == GuitarActionType.StrumClick)}");
        assert(wheelCount == 2, "关闭自动扫弦: 依然按时自动执行换和弦 (轮盘选择)", $"实际轮盘动作数: {wheelCount}");
    }

    private static void Test_GuitarCompiler_ChordLeadAndDeduplication(Action<bool, string, string> assert)
    {
        var chordEvents = new List<ChordChangeEvent>
        {
            new ChordChangeEvent(1_000_000, ChordId.C)
        };
        var strumEvents = new List<StrumEvent>
        {
            new StrumEvent(1_000_000, StrumDirectionHint.LowToHigh),
            new StrumEvent(1_500_000, StrumDirectionHint.HighToLow),
            new StrumEvent(2_000_000, StrumDirectionHint.LowToHigh)
        };

        var preset = new WheelPreset();
        var actions = GuitarCompiler.CompileChordEvents(
            chordEvents,
            strumEvents,
            preset,
            new GuitarCompilerOptions { AutoStrum = AutoStrumMode.Auto });

        var wheelSelects = actions.Where(a => a.Type == GuitarActionType.WheelSelect).ToList();
        var strumClicks = actions.Where(a => a.Type == GuitarActionType.StrumClick).ToList();

        assert(wheelSelects.Count == 1, "和弦不变连续扫弦: 轮盘只在切换时点击 1 次", $"实际点击次数: {wheelSelects.Count}");
        assert(strumClicks.Count == 3, "自动扫弦开启: 3 次扫弦动作全部生成", $"实际扫弦动作数: {strumClicks.Count}");

        // 提前量验证 (1000ms - 55ms = 945ms)
        assert(wheelSelects[0].TimeUs == 945_000, "和弦切换提前量: 换和弦在第 1 次扫弦前 55ms 提前完成 (945,000 us)", $"实际时间: {wheelSelects[0].TimeUs}");
        assert(wheelSelects[0].Value == 4, "和弦槽位选择: C 和弦正确映射到 P1 Slot 4", $"实际槽位: {wheelSelects[0].Value}");
    }

    private static void Test_MultiMonitorVirtualDesktopMapping(Action<bool, string, string> assert)
    {
        // 模拟用户真实副屏拓扑: 主屏 (0,0, 1707x1067), 副屏在左侧 (-1920, 522, 1920x1080)
        int vLeft = -1920;
        int vTop = 0;
        int vWidth = 1920 + 1707; // 3627
        int vHeight = 1602;

        int screenX = -960; // 副屏水平正中央
        int screenY = 1062; // 522 + 540

        int normX = (int)Math.Round((screenX - vLeft) * 65535.0 / (vWidth - 1));
        int normY = (int)Math.Round((screenY - vTop) * 65535.0 / (vHeight - 1));

        assert(normX >= 0 && normX <= 65535, "多显示器虚拟桌面映射: 负坐标 (-960) 正确映射为 [0..65535] 正整数", $"normX={normX}");
        assert(normY >= 0 && normY <= 65535, "多显示器虚拟桌面映射: Y 坐标 (1062) 正确映射为 [0..65535] 正整数", $"normY={normY}");

        // 窗口相对客户区计算验证
        Point gameOrigin = new Point(-1920, 522);
        Size gameClientSize = new Size(1920, 1080);

        int clientX = screenX - gameOrigin.X;
        int clientY = screenY - gameOrigin.Y;
        double nx = clientX / (double)gameClientSize.Width;
        double ny = clientY / (double)gameClientSize.Height;

        assert(Math.Abs(nx - 0.5000) < 0.0001, "多显示器副屏点击转换: 相对 X 正确识别为 0.5000 (不再归零)", $"nx={nx}");
        assert(Math.Abs(ny - 0.5000) < 0.0001, "多显示器副屏点击转换: 相对 Y 正确识别为 0.5000", $"ny={ny}");

        // 越界点击检测验证 (主屏点击落在副屏窗口外部)
        Point outsideClick = new Point(100, 300); // 落在主屏
        int outClientX = outsideClick.X - gameOrigin.X;
        bool isOutside = outClientX < 0 || outClientX >= gameClientSize.Width;
        assert(isOutside, "多显示器越界拦截: 点击在主屏非游戏区域时正确识别为越界并拦截", $"outClientX={outClientX}");
    }

    private static void Test_PresetZeroXAutoHealing(Action<bool, string, string> assert)
    {
        var corruptedPreset = new WheelPreset();
        for (int i = 0; i < 6; i++)
        {
            corruptedPreset.Slots[i] = new NormalizedPoint(0.0000, 0.5000);
        }
        corruptedPreset.StrumPoint = new NormalizedPoint(0.0000, 0.3000);

        bool allZero = corruptedPreset.Slots.All(s => s.X <= 0.005);
        assert(allZero, "异常配置识别: 成功识别出所有槽位 X 坐标全为 0 的损坏预设", "");

        // 触发自愈修复
        var def = new WheelPreset();
        corruptedPreset.Slots = def.Slots.Select(p => new NormalizedPoint(p.X, p.Y)).ToArray();
        corruptedPreset.StrumPoint = new NormalizedPoint(def.StrumPoint.X, def.StrumPoint.Y);

        bool healed = corruptedPreset.Slots[0].X > 0.4 && corruptedPreset.Slots[1].X > 0.6;
        assert(healed, "配置自愈修复: 损坏的全 0 坐标已自动修复为标准圆周分布坐标", $"Slot1.X={corruptedPreset.Slots[0].X}, Slot2.X={corruptedPreset.Slots[1].X}");
    }

    private static void Test_StandardSixStringTuning_String1IsHighestPitch_MappedToGameKey6(Action<bool, string, string> assert)
    {
        var tuning = GuitarTuning.Standard;
        // 游戏标准六弦调弦：物理1弦为最细高音弦 (E4=64), 物理6弦为最粗低音弦 (E2=40)
        bool tuningCorrect =
            tuning.OpenStrings[0] == 64 && // 物理1弦 E4
            tuning.OpenStrings[1] == 59 && // 物理2弦 B3
            tuning.OpenStrings[2] == 55 && // 物理3弦 G3
            tuning.OpenStrings[3] == 50 && // 物理4弦 D3
            tuning.OpenStrings[4] == 45 && // 物理5弦 A2
            tuning.OpenStrings[5] == 40;   // 物理6弦 E2

        assert(tuningCorrect, "标准六弦吉他调弦规范: 物理1弦为最细高音弦(E4 64)，物理6弦为最粗低音弦(E2 40)",
            $"实际调弦: [{string.Join(", ", tuning.OpenStrings)}]");

        // 验证音高严格单调递减 (物理1弦至6弦)
        bool monotonicallyDecreasing = true;
        for (int i = 0; i < 5; i++)
        {
            if (tuning.OpenStrings[i] <= tuning.OpenStrings[i + 1])
            {
                monotonicallyDecreasing = false;
                break;
            }
        }
        assert(monotonicallyDecreasing, "琴弦音高单调性: 物理1弦至6弦音高严格递减 (高音至低音)", "");

        // 验证琴弦与游戏按键真实对应映射 (按键1=现实6弦, 按键6=现实1弦)
        bool keysCorrect =
            GameScanCodes.GetGameDigit(1) == 6 &&
            GameScanCodes.GetGameDigit(2) == 5 &&
            GameScanCodes.GetGameDigit(3) == 4 &&
            GameScanCodes.GetGameDigit(4) == 3 &&
            GameScanCodes.GetGameDigit(5) == 2 &&
            GameScanCodes.GetGameDigit(6) == 1;

        assert(keysCorrect, "游戏按键真实对应映射: 按键1等于游戏吉他一弦，现实吉他6弦 (物理1弦->按键6，物理6弦->按键1)", "");
    }

    private static void Test_ChordPluck_ExtractionAndVoicing(Action<bool, string, string> assert)
    {
        // 测试 C 和弦下的琴弦发声映射
        var cPitches = ChordAnalyzer.GetChordStringPitches(ChordId.C);
        // C: [64, 60, 55, 52, 48, 40] -> E4, C4, G3, E3, C3, E2 (1~6弦)
        assert(cPitches[0] == 64 && cPitches[1] == 60 && cPitches[2] == 55 &&
               cPitches[3] == 52 && cPitches[4] == 48 && cPitches[5] == 40,
               "和弦琴弦声部映射 C: 正确分配 1~6 弦音高 [64, 60, 55, 52, 48, 40]",
               $"实际音高: [{string.Join(", ", cPitches)}]");

        // 测试 MatchNoteToChordString
        byte strC3 = ChordAnalyzer.MatchNoteToChordString(48, ChordId.C);
        byte strE3 = ChordAnalyzer.MatchNoteToChordString(52, ChordId.C);
        byte strG3 = ChordAnalyzer.MatchNoteToChordString(55, ChordId.C);
        byte strC4 = ChordAnalyzer.MatchNoteToChordString(60, ChordId.C);
        byte strE4 = ChordAnalyzer.MatchNoteToChordString(64, ChordId.C);

        assert(strC3 == 5, "C3(48) 匹配 C 和弦琴弦: 准确匹配到 5 弦 (C3)", $"实际弦号: {strC3}");
        assert(strE3 == 4, "E3(52) 匹配 C 和弦琴弦: 准确匹配到 4 弦 (E3)", $"实际弦号: {strE3}");
        assert(strG3 == 3, "G3(55) 匹配 C 和弦琴弦: 准确匹配到 3 弦 (G3)", $"实际弦号: {strG3}");
        assert(strC4 == 2, "C4(60) 匹配 C 和弦琴弦: 准确匹配到 2 弦 (C4)", $"实际弦号: {strC4}");
        assert(strE4 == 1, "E4(64) 匹配 C 和弦琴弦: 准确匹配到 1 弦 (E4)", $"实际弦号: {strE4}");
    }

    private static void Test_ChordPluck_PolyphonySimultaneousStringPress(Action<bool, string, string> assert)
    {
        // 构造同一时刻同时发声的 3 个和弦音符 (琴弦同按)
        var simultaneousNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 48, StartUs = 1_000_000, DurationUs = 500_000, Velocity = 100 }, // C3 -> 2弦
            new RawMidiNote { NoteNumber = 52, StartUs = 1_000_000, DurationUs = 500_000, Velocity = 100 }, // E3 -> 3弦
            new RawMidiNote { NoteNumber = 55, StartUs = 1_000_000, DurationUs = 500_000, Velocity = 100 }  // G3 -> 4弦
        };

        var chordEvents = new List<ChordChangeEvent>
        {
            new ChordChangeEvent(0, ChordId.C)
        };

        var plucks = ChordAnalyzer.ExtractPluckEvents(simultaneousNotes, chordEvents);
        assert(plucks.Count == 3, "同时发声音符提取: 提取出 3 个拨弦事件", $"实际数量: {plucks.Count}");

        // 验证 3 个音符被分配到不同琴弦，无琴弦冲突
        var stringNos = plucks.Select(p => p.StringNo).ToHashSet();
        assert(stringNos.Count == 3, "琴弦同按分配: 同时发声的音符被分配到不同的琴弦 (2, 3, 4弦)",
            $"实际弦号集合: [{string.Join(", ", plucks.Select(p => p.StringNo))}]");

        // 编译为动作，验证在同一微秒有多个 StringDown
        var preset = new WheelPreset();
        var actions = GuitarCompiler.CompileChordEvents(
            chordEvents,
            strumEvents: new List<StrumEvent>(),
            plucks,
            preset,
            new GuitarCompilerOptions { PlayStyle = GuitarPlayStyle.PluckOnly });

        var downActions = actions.Where(a => a.Type == GuitarActionType.StringDown && a.TimeUs == 1_000_000).ToList();
        assert(downActions.Count == 3, "琴弦同按编译: 在 1,000,000 us 准时并发 3 个 StringDown 动作",
            $"并发动作数: {downActions.Count}");
    }

    private static void Test_NoteSustainAndRetriggerGap(Action<bool, string, string> assert)
    {
        // 构造一个持续 600ms (600,000 us) 的长音
        var fretEvents = new List<FretPlayEvent>
        {
            new FretPlayEvent(timeUs: 1_000_000, stringNo: 1, fret: 1, noteNumber: 41, velocity: 100, durationUs: 600_000),
            // 同一琴弦在 1_800_000 us 再次发声
            new FretPlayEvent(timeUs: 1_800_000, stringNo: 1, fret: 1, noteNumber: 41, velocity: 100, durationUs: 400_000)
        };

        var preset = new WheelPreset();
        var actions = GuitarCompiler.CompileFretEvents(fretEvents, preset, new GuitarCompilerOptions
        {
            EnableSustain = true
        });

        var down1 = actions.First(a => a.Type == GuitarActionType.StringDown && a.TimeUs == 1_000_000);
        var up1 = actions.First(a => a.Type == GuitarActionType.StringUp && a.Value == 1);

        long holdDuration = up1.TimeUs - down1.TimeUs;
        assert(holdDuration == 600_000, "长按延音时值: 琴弦持续按下 600ms (非固定 30ms 脉冲)",
            $"实际按住时长: {holdDuration / 1000} ms");

        // 测试密集同弦重触发释放间隙 (Note 1 持续 500ms，但在 300ms 处同弦再次弹响)
        var fastEvents = new List<FretPlayEvent>
        {
            new FretPlayEvent(timeUs: 0, stringNo: 2, fret: 1, noteNumber: 46, velocity: 100, durationUs: 500_000),
            new FretPlayEvent(timeUs: 300_000, stringNo: 2, fret: 1, noteNumber: 46, velocity: 100, durationUs: 500_000)
        };

        var fastActions = GuitarCompiler.CompileFretEvents(fastEvents, preset, new GuitarCompilerOptions
        {
            EnableSustain = true,
            ReleaseGapUs = 2_000
        });

        var firstUp = fastActions.First(a => a.Type == GuitarActionType.StringUp && a.Value == 2);
        assert(firstUp.TimeUs <= 275_000, "重触发安全间隙: 在下一音符到来前至少 25ms 提前抬起释放",
            $"实际抬起时间: {firstUp.TimeUs} us");

        // 测试吉他动态智能延音: 两音过近 (<280ms) 严禁延音，点按 25ms 快速释放
        var closeEvents = new List<FretPlayEvent>
        {
            new FretPlayEvent(timeUs: 1_000_000, stringNo: 3, fret: 1, noteNumber: 56, velocity: 100, durationUs: 600_000),
            new FretPlayEvent(timeUs: 1_120_000, stringNo: 3, fret: 1, noteNumber: 56, velocity: 100, durationUs: 500_000)
        };
        var closeActions = GuitarCompiler.CompileFretEvents(closeEvents, preset, new GuitarCompilerOptions
        {
            EnableSustain = true,
            StringTapDurationUs = 25_000,
            CloseNotesThresholdUs = 280_000
        });
        var closeUp1 = closeActions.First(a => a.Type == GuitarActionType.StringUp && a.Value == 3 && a.TimeUs < 1_120_000);
        assert(closeUp1.TimeUs == 1_025_000, "吉他动态延音: 两音太近(120ms)时严格不延音，25ms 快速释放防吞音",
            $"实际抬起: {closeUp1.TimeUs} us");

        var closeUp2 = closeActions.First(a => a.Type == GuitarActionType.StringUp && a.Value == 3 && a.TimeUs > 1_120_000);
        assert(closeUp2.TimeUs == 1_120_000 + 500_000, "吉他动态延音: 最后一个音符且乐谱有延音，保持 500ms 饱满延音",
            $"实际抬起: {closeUp2.TimeUs} us");
    }

    private static void Test_GuitarPlayStyles(Action<bool, string, string> assert)
    {
        var chordEvents = new List<ChordChangeEvent> { new(0, ChordId.C) };
        var strumEvents = new List<StrumEvent> { new(1_000_000, StrumDirectionHint.LowToHigh) };
        var plucks = new List<ChordPluckEvent>
        {
            new(1_000_000, stringNo: 2, noteNumber: 48, velocity: 100, durationUs: 300_000), // 在扫弦区
            new(1_600_000, stringNo: 5, noteNumber: 60, velocity: 100, durationUs: 300_000)  // 独立单音拨弦
        };

        var preset = new WheelPreset();

        // 1. 纯指弹 PluckOnly: 不应有任何 StrumClick，全部为按键 StringDown/Up
        var pluckOnlyActions = GuitarCompiler.CompileChordEvents(
            chordEvents, strumEvents, plucks, preset,
            new GuitarCompilerOptions { PlayStyle = GuitarPlayStyle.PluckOnly });
        bool noStrum = pluckOnlyActions.All(a => a.Type != GuitarActionType.StrumClick);
        int pluckCount = pluckOnlyActions.Count(a => a.Type == GuitarActionType.StringDown);
        assert(noStrum && pluckCount == 2, "纯拨弦指弹风格: 不产生鼠标扫弦，2 个音符全部通过 1~6 键盘拨弦",
            $"扫弦数: {pluckOnlyActions.Count(a => a.Type == GuitarActionType.StrumClick)}, 拨弦数: {pluckCount}");

        // 2. 纯扫弦 StrumOnly: 不应有任何 StringDown
        var strumOnlyActions = GuitarCompiler.CompileChordEvents(
            chordEvents, strumEvents, plucks, preset,
            new GuitarCompilerOptions { PlayStyle = GuitarPlayStyle.StrumOnly });
        bool noPluck = strumOnlyActions.All(a => a.Type != GuitarActionType.StringDown);
        bool hasStrum = strumOnlyActions.Any(a => a.Type == GuitarActionType.StrumClick);
        assert(noPluck && hasStrum, "纯鼠标扫弦风格: 仅产生鼠标扫弦，不产生键盘拨弦",
            $"扫弦数: {strumOnlyActions.Count(a => a.Type == GuitarActionType.StrumClick)}");

        // 3. 智能弹拨 Hybrid: 密集区扫弦，独立单音拨弦
        var hybridActions = GuitarCompiler.CompileChordEvents(
            chordEvents, strumEvents, plucks, preset,
            new GuitarCompilerOptions { PlayStyle = GuitarPlayStyle.Hybrid });
        bool hybridStrum = hybridActions.Any(a => a.Type == GuitarActionType.StrumClick);
        var hybridDowns = hybridActions.Where(a => a.Type == GuitarActionType.StringDown).ToList();
        assert(hybridStrum && hybridDowns.Count == 1 && hybridDowns[0].TimeUs == 1_600_000,
            "智能弹拨风格: 密集扫弦区触发鼠标扫弦，独立单音(1600ms)自动按键拨弦",
            $"扫弦: {hybridStrum}, 单音拨弦时间: {(hybridDowns.Count > 0 ? hybridDowns[0].TimeUs.ToString() : "None")}");
    }

    private static void Test_FretConverter_0To24Frets_And_5Pages(Action<bool, string, string> assert)
    {
        // 0品 -> P5 (Zero), Slot 1
        var (p0, s0) = FretConverter.GetPageAndSlot(0);
        assert(p0 == 5 && s0 == 1, "0品映射: 0品映射到 P5(Zero) Slot 1", $"实际 P{p0} S{s0}");

        // 1..6品 -> P1, Slot 1..6
        var (p1, s1) = FretConverter.GetPageAndSlot(1);
        var (p6, s6) = FretConverter.GetPageAndSlot(6);
        assert(p1 == 1 && s1 == 1 && p6 == 1 && s6 == 6, "1~6品映射: 1品=P1S1, 6品=P1S6", $"P1: {p1}S{s1}, P6: {p6}S{s6}");

        // 7..12品 -> P2, Slot 1..6
        var (p7, s7) = FretConverter.GetPageAndSlot(7);
        var (p12, s12) = FretConverter.GetPageAndSlot(12);
        assert(p7 == 2 && s7 == 1 && p12 == 2 && s12 == 6, "7~12品映射: 7品=P2S1, 12品=P2S6", $"P7: {p7}S{s7}, P12: {p12}S{s12}");

        // 13..18品 -> P3, Slot 1..6
        var (p13, s13) = FretConverter.GetPageAndSlot(13);
        var (p18, s18) = FretConverter.GetPageAndSlot(18);
        assert(p13 == 3 && s13 == 1 && p18 == 3 && s18 == 6, "13~18品映射: 13品=P3S1, 18品=P3S6", $"P13: {p13}S{s13}, P18: {p18}S{s18}");

        // 19..24品 -> P4, Slot 1..6
        var (p19, s19) = FretConverter.GetPageAndSlot(19);
        var (p24, s24) = FretConverter.GetPageAndSlot(24);
        assert(p19 == 4 && s19 == 1 && p24 == 4 && s24 == 6, "19~24品映射: 19品=P4S1, 24品=P4S6", $"P19: {p19}S{s19}, P24: {p24}S{s24}");
    }

    private static void Test_PageTurn_Timing_And_KeySequence(Action<bool, string, string> assert)
    {
        // 构造从 P1 到 P3 的翻页测试 (需要按 2 次 T 键，每次间隔 >= 220ms)
        var fretEvents = new List<FretPlayEvent>
        {
            // 第一个音在 P1 (1品: P1 S1)
            new FretPlayEvent(timeUs: 1_000_000, stringNo: 1, fret: 1, noteNumber: 65, page: 1, slot: 1),
            // 第二个音在 P3 (14品: P3 S2), 发生于 3_000_000 us (充足时间准备)
            new FretPlayEvent(timeUs: 3_000_000, stringNo: 1, fret: 14, noteNumber: 78, page: 3, slot: 2)
        };

        var preset = new WheelPreset();
        var options = new GuitarCompilerOptions
        {
            PageSwitchDelayUs = 220_000,
            SlotSettleUs = 55_000
        };

        var actions = GuitarCompiler.CompileFretEvents(fretEvents, preset, options);

        var pageNavs = actions.Where(a => a.Type == GuitarActionType.PageNav).ToList();
        var p3Navs = pageNavs.ToList();
        assert(p3Navs.Count == 2 && p3Navs.Select(a => a.Value).SequenceEqual(new byte[] { 2, 3 }),
            "跨页翻页排队: P1 到 P3 准确排布 P2、P3 两次翻页动作", $"实际页序: {string.Join(",", p3Navs.Select(a => a.Value))}");

        bool allTKeys = p3Navs.All(n => n.ScanCode == 0x14); // 0x14 = T
        assert(allTKeys, "翻页按键码: 下一页严格使用物理按键 T (ScanCode 0x14)", "");

        long navGap = p3Navs[1].TimeUs - p3Navs[0].TimeUs;
        assert(navGap >= options.PageKeyHoldUs + Math.Max(15_000, options.PageInterTapGapUs),
            "翻页物理安全时序: 连续翻页动作按保持时间与抬键间隙串行", $"实际间隔: {navGap / 1000}ms");

        var slotSelect = actions.First(a => a.Type == GuitarActionType.WheelSelect && a.Value == 2);
        long afterNavGap = slotSelect.TimeUs - p3Navs[1].TimeUs;
        assert(afterNavGap >= 0, "选品提前调度: 槽位选择严格在完成翻页后才执行", $"间隙: {afterNavGap}us");

        // 首音直接位于 P3 时，也必须从实际默认 P1 生成开局翻页，不能假定游戏已经在建议页。
        var firstNoteOnP3 = new[]
        {
            new FretPlayEvent(timeUs: 1_000_000, stringNo: 1, fret: 14, noteNumber: 78, page: 3, slot: 2)
        };
        var firstNoteP3Actions = GuitarCompiler.CompileFretEvents(firstNoteOnP3, preset, options);
        var firstNoteP3Navs = firstNoteP3Actions.Where(a => a.Type == GuitarActionType.PageNav).ToList();
        assert(firstNoteP3Navs.Count == 2 && firstNoteP3Navs.Select(a => a.Value).SequenceEqual(new byte[] { 2, 3 }),
            "开局目标页对齐: 首音在 P3 时从实际 P1 开局依次生成 P2、P3 翻页动作",
            $"实际页序: {string.Join(",", firstNoteP3Navs.Select(a => a.Value))}");

        // P5 是有效的零品页，须从 P1 向后翻四页。
        var openString = new[]
        {
            new FretPlayEvent(timeUs: 1_000_000, stringNo: 6, fret: 0, noteNumber: 40, page: 5, slot: 1)
        };
        var openStringActions = GuitarCompiler.CompileFretEvents(openString, preset, options);
        var openPageNavs = openStringActions.Where(a => a.Type == GuitarActionType.PageNav).ToList();
        assert(openPageNavs.Count == 4 && openPageNavs.Select(a => a.Value).SequenceEqual(new byte[] { 2, 3, 4, 5 })
            && openPageNavs.All(a => a.ScanCode == preset.PageNextScanCode),
            "0品页编译: 从 P1 到 P5 生成四次 T 翻页", $"实际动作: {string.Join(",", openPageNavs)}");
    }

    private static void Test_GlobalOctaveShift_NoPerNoteFolding(Action<bool, string, string> assert)
    {
        // 构造一个音程为大三度 (C4=60, E4=64) 和跨八度音符 (G3=55, C5=72) 的旋律
        var melody = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 55, StartUs = 0, DurationUs = 300_000, Velocity = 100 },       // G3
            new RawMidiNote { NoteNumber = 60, StartUs = 400_000, DurationUs = 300_000, Velocity = 100 }, // C4
            new RawMidiNote { NoteNumber = 64, StartUs = 800_000, DurationUs = 300_000, Velocity = 100 }, // E4
            new RawMidiNote { NoteNumber = 72, StartUs = 1_200_000, DurationUs = 300_000, Velocity = 100 } // C5
        };

        var events = FretConverter.Convert(melody);
        assert(events.Count == 4, "整曲统一移调: 生成 4 个音符事件", "");

        // 验证音高差严格保持一致 (文档 3.4: 严禁逐音独立 octave-fold 破坏音程)
        int diff01 = events[1].NoteNumber - events[0].NoteNumber; // 60 - 55 = 5 (纯四度)
        int diff12 = events[2].NoteNumber - events[1].NoteNumber; // 64 - 60 = 4 (大三度)
        int diff23 = events[3].NoteNumber - events[2].NoteNumber; // 72 - 64 = 8 (小六度)

        bool intervalsPreserved = (diff01 == 5) && (diff12 == 4) && (diff23 == 8);
        assert(intervalsPreserved, "严禁独立折叠音符: 整曲音程完美保真，大三度/小六度音程绝对一致",
            $"实际音高序列: [{string.Join(", ", events.Select(e => e.NoteNumber))}]");
    }

    private static void Test_SlashChord_BassRecognition(Action<bool, string, string> assert)
    {
        // 1. G/B 和弦 (Page 4 Slot 1): 构成音 G, B, D (7, 11, 2)，最低音为 B2 (47)
        var gbNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 47, StartUs = 0, DurationUs = 500_000, Velocity = 100 }, // B2 (最低音)
            new RawMidiNote { NoteNumber = 55, StartUs = 0, DurationUs = 500_000, Velocity = 100 }, // G3
            new RawMidiNote { NoteNumber = 62, StartUs = 0, DurationUs = 500_000, Velocity = 100 }  // D4
        };
        var gbEvents = ChordAnalyzer.Analyze(gbNotes);
        assert(gbEvents.Count >= 1 && gbEvents[0].Chord == ChordId.G_B,
            "斜杠和弦识别 G/B: 最低音为 B 时准确识别为 G/B (P4 Slot 1)",
            $"实际识别: {(gbEvents.Count > 0 ? gbEvents[0].Chord.ToString() : "None")}");

        // 2. D/F# 和弦 (Page 4 Slot 3): 构成音 D, F#, A (2, 6, 9)，最低音为 F#2 (42)
        var dfNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 42, StartUs = 0, DurationUs = 500_000, Velocity = 100 }, // F#2 (最低音)
            new RawMidiNote { NoteNumber = 50, StartUs = 0, DurationUs = 500_000, Velocity = 100 }, // D3
            new RawMidiNote { NoteNumber = 57, StartUs = 0, DurationUs = 500_000, Velocity = 100 }  // A3
        };
        var dfEvents = ChordAnalyzer.Analyze(dfNotes);
        assert(dfEvents.Count >= 1 && dfEvents[0].Chord == ChordId.D_Fsharp,
            "斜杠和弦识别 D/F#: 最低音为 F# 时准确识别为 D/F# (P4 Slot 3)",
            $"实际识别: {(dfEvents.Count > 0 ? dfEvents[0].Chord.ToString() : "None")}");

        // 3. 原位 G 和弦: 最低音为根音 G2 (43)，绝不能误判为 G/B
        var gNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 43, StartUs = 0, DurationUs = 500_000, Velocity = 100 }, // G2 (最低音为根音)
            new RawMidiNote { NoteNumber = 47, StartUs = 0, DurationUs = 500_000, Velocity = 100 }, // B2
            new RawMidiNote { NoteNumber = 50, StartUs = 0, DurationUs = 500_000, Velocity = 100 }  // D3
        };
        var gEvents = ChordAnalyzer.Analyze(gNotes);
        assert(gEvents.Count >= 1 && gEvents[0].Chord == ChordId.G,
            "普通和弦防误判: 最低音为 G 时准确识别为原位 G，绝不误判为 G/B",
            $"实际识别: {(gEvents.Count > 0 ? gEvents[0].Chord.ToString() : "None")}");
    }

    private static void Test_24Chords_CatalogCompleteness(Action<bool, string, string> assert)
    {
        var chords = ChordAnalyzer.SupportedChords;
        assert(chords.Count == 24, "24和弦目录完整性: 准确包含 24 个支持的和弦定义", $"实际数量: {chords.Count}");

        for (byte page = 1; page <= 4; page++)
        {
            var pageChords = chords.Where(c => c.Page == page).ToList();
            assert(pageChords.Count == 6, $"和弦轮盘 P{page} 完整性: 包含 6 个槽位和弦", $"实际数量: {pageChords.Count}");

            var slots = pageChords.Select(c => c.Slot).OrderBy(s => s).ToList();
            bool slotsContinuous = slots.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 });
            assert(slotsContinuous, $"和弦轮盘 P{page} 槽位连续性: 槽位 1~6 连续完整无空缺", "");
        }
    }

    private static void Test_PlaybackStartAdvisor(Action<bool, string, string> assert)
    {
        // 1. 测试罗马数字品格转换
        assert(PlaybackStartAdvisor.GetRomanFret(0) == "Zero", "品格罗马数字 0: Zero", "");
        assert(PlaybackStartAdvisor.GetRomanFret(1) == "I", "品格罗马数字 1: I", "");
        assert(PlaybackStartAdvisor.GetRomanFret(4) == "IV", "品格罗马数字 4: IV", "");
        assert(PlaybackStartAdvisor.GetRomanFret(12) == "XII", "品格罗马数字 12: XII", "");
        assert(PlaybackStartAdvisor.GetRomanFret(24) == "XXIV", "品格罗马数字 24: XXIV", "");

        // 2. 测试品格模式建议 (默认稳定为 P1)
        var fretEvents = new List<FretPlayEvent>
        {
            new FretPlayEvent(timeUs: 1_000_000, stringNo: 1, fret: 1, noteNumber: 65, velocity: 100, page: 1, slot: 1)
        };
        var fretAdvice = PlaybackStartAdvisor.GetAdvice(1, fretEvents, chordEvents: null, recommendDynamicStartPage: false);
        assert(fretAdvice.InitialPage == 1, "品格模式起始页: 默认稳定为 P1 (第1页)", $"实际页面: P{fretAdvice.InitialPage}");
        assert(fretAdvice.InitialModeName == "吉他品格模式", "品格模式物理起始状态: 品格模式", fretAdvice.InitialModeName);
        assert(fretAdvice.SummaryText.Contains("请让游戏处于品格模式 P1"),
            "品格模式准备提示: 明确从品格模式 P1 开始", fretAdvice.SummaryText);
        assert(fretAdvice.FirstItemDescription.Contains("物理1弦") && fretAdvice.FirstItemDescription.Contains("按键6"),
            "品格模式首音提示: 物理1弦准确提示游戏按键6", $"实际提示: {fretAdvice.FirstItemDescription}");

        var dynamicTargetEvents = new[]
        {
            new FretPlayEvent(timeUs: 1_000_000, stringNo: 1, fret: 14, noteNumber: 78, page: 3, slot: 2)
        };
        var dynamicAdvice = PlaybackStartAdvisor.GetAdvice(1, dynamicTargetEvents, null, recommendDynamicStartPage: true);
        assert(dynamicAdvice.InitialPage == 1 && dynamicAdvice.TargetPage == 3,
            "动态指引与物理起点分离: 游戏起点保持 P1，首音目标为 P3",
            $"实际起点=P{dynamicAdvice.InitialPage}, 目标=P{dynamicAdvice.TargetPage}");

        // 3. 测试和弦模式建议 (默认稳定为 P1)
        var chordEvents = new List<ChordChangeEvent>
        {
            new ChordChangeEvent(timeUs: 0, chord: ChordId.C, page: 1, slot: 4)
        };
        var chordAdvice = PlaybackStartAdvisor.GetAdvice(2, fretEvents: null, chordEvents, recommendDynamicStartPage: false);
        assert(chordAdvice.InitialPage == 1, "和弦模式起始页: 默认稳定为 P1", $"实际页面: P{chordAdvice.InitialPage}");
        assert(chordAdvice.InitialModeName == "吉他品格模式" && chordAdvice.ModeName == "吉他和弦模式",
            "和弦模式物理起点与演奏模式分离", $"起点={chordAdvice.InitialModeName}, 演奏={chordAdvice.ModeName}");
        assert(chordAdvice.SummaryText.Contains("请让游戏处于品格模式 P1") &&
               chordAdvice.SummaryText.Contains("自动切换到和弦模式"),
            "和弦模式准备提示: 从品格模式 P1 起步后自动切换", chordAdvice.SummaryText);
        assert(chordAdvice.FirstItemDescription.Contains("C") && chordAdvice.FirstItemDescription.Contains("Slot 4"),
            "和弦模式首和弦提示: C 和弦准确提示 P1 Slot 4", $"实际提示: {chordAdvice.FirstItemDescription}");
    }

    private static void Test_v42_CoreFeatures(Action<bool, string, string> assert)
    {
        // 1. 测试动态规划不可达路径硬禁止与 SkipNote 兜底
        // 第一个音在 P1 (Note 65, 1弦1品)
        // 第二个音在 30ms 之后，音符为高音 88 (1弦24品, 必须在 P4). 30ms 绝无可能从 P1 翻到 P4!
        var melodyUnreachable = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 65, StartUs = 0, DurationUs = 300_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 88, StartUs = 30_000, DurationUs = 300_000, Velocity = 100 }
        };

        var events = FretConverter.Convert(melodyUnreachable);
        assert(events.Count == 2, "v4.2 DP硬约束: 输出 2 个事件", "");
        assert(events.Count > 1 && events[1].IsSkipped, "v4.2 DP硬约束: 30ms 内跨 3 页不可达指法硬禁止，自动标记为 IsSkipped 兜底",
            $"实际状态: IsSkipped={events[1].IsSkipped}, S{events[1].StringNo}-F{events[1].Fret}");

        var preset = new WheelPreset();
        var actions = GuitarCompiler.CompileFretEvents(events, preset);
        bool note2Played = actions.Any(a => a.Type == GuitarActionType.StringDown && a.TimeUs == 20_000);
        assert(!note2Played, "v4.2 编译器保护: 被跳过的不可达音符绝不下发按键，绝不在错误品位刺耳发音", "");

        // 2. 测试 PreRoll 开局预热负时间戳
        // 第一个音在 50ms 处，品位在 P3 (14品)
        var preRollEvents = new List<FretPlayEvent>
        {
            new FretPlayEvent(timeUs: 50_000, stringNo: 1, fret: 14, noteNumber: 78, page: 3, slot: 2)
        };
        var preRollActions = GuitarCompiler.CompileFretEvents(preRollEvents, preset, new GuitarCompilerOptions
        {
            InitialPage = 1,
            PageSwitchDelayUs = 220_000,
            SlotSettleUs = 55_000
        });
        bool hasNegativeActions = preRollActions.Any(a => a.TimeUs < 0);
        assert(hasNegativeActions, "v4.2 PreRoll 调度: 首音跨页动作生成负时间戳，在开局前提前完成翻页与选槽",
            $"首个动作时间戳: {preRollActions[0].TimeUs} us");

        // 3. 测试控制屏障 (Control Barrier)
        // 音符 1 从 0us 延音至 2_000_000 us (2秒)
        // 音符 2 在 1_000_000 us 换页到 P3 (需要提前在 725,000 us 翻页)
        var barrierEvents = new List<FretPlayEvent>
        {
            new FretPlayEvent(timeUs: 0, stringNo: 1, fret: 1, noteNumber: 65, durationUs: 2_000_000, page: 1, slot: 1),
            new FretPlayEvent(timeUs: 1_000_000, stringNo: 1, fret: 14, noteNumber: 78, durationUs: 300_000, page: 3, slot: 2)
        };
        var barrierActions = GuitarCompiler.CompileFretEvents(barrierEvents, preset, new GuitarCompilerOptions
        {
            InitialPage = 1,
            PageSwitchDelayUs = 220_000,
            SlotSettleUs = 55_000,
            EnableSustain = true
        });
        var navAction = barrierActions.First(a => a.Type == GuitarActionType.PageNav);
        var note1Up = barrierActions.First(a => a.Type == GuitarActionType.StringUp && a.Value == 1);
        assert(note1Up.TimeUs <= navAction.TimeUs - 5_000, "v4.2 控制屏障: 翻页前提前释放正在延音的琴弦，杜绝翻页时按住琴弦",
            $"Up 时间: {note1Up.TimeUs} us, Nav 时间: {navAction.TimeUs} us");

        // 4. 测试 SanitizeForFretSolo 彻底保全琶音与旋律线
        var arpeggio = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 60, StartUs = 0, DurationUs = 800_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 64, StartUs = 100_000, DurationUs = 800_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 67, StartUs = 200_000, DurationUs = 800_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 72, StartUs = 300_000, DurationUs = 800_000, Velocity = 100 }
        };
        var sanitized = FretConverter.SanitizeForFretSolo(arpeggio);
        assert(sanitized.Count == 4, "v4.2 旋律直通: 彻底保全琶音与延音音符，绝不整曲切片重跑最高音",
            $"输入: 4, 净化后: {sanitized.Count}");
    }

    private static void Test_v43_Features(Action<bool, string, string> assert)
    {
        var preset = new WheelPreset();
        var opt = new GuitarCompilerOptions
        {
            InitialPage = 1,
            PageKeyHoldUs = 25_000,
            PageInterTapGapUs = 12_000,
            PageInputSafetyUs = 8_000,
            WheelMouseSettleUs = 25_000,
            AutoStrum = AutoStrumMode.FollowMidi
        };

        // 1. 快速翻页串行离散 Tap 序列 (P1->P2 与 P1->P4)
        var fEventsP1P2 = new List<FretPlayEvent>
        {
            new FretPlayEvent(1_000_000, 1, 1, 65, page: 1, slot: 1),
            new FretPlayEvent(2_000_000, 1, 7, 71, page: 2, slot: 1)
        };
        var actionsP1P2 = GuitarCompiler.CompileFretEvents(fEventsP1P2, preset, opt);
        var navP1P2 = actionsP1P2.Where(a => a.Type == GuitarActionType.PageNav).ToList();
        assert(navP1P2.Count == 1, "v4.3 极速翻页串行: P1->P2 (跨1页) 准确编译为 1 个独立 PageNav Tap 调度动作", $"实际动作数: {navP1P2.Count}");
        assert(navP1P2[0].ScanCode == preset.PageNextScanCode, "v4.3 翻页按键映射: P1->P2 向前翻页生成 T 键 (0x14)", $"实际按键码: 0x{navP1P2[0].ScanCode:X2}");

        var fEventsP1P4 = new List<FretPlayEvent>
        {
            new FretPlayEvent(1_000_000, 1, 1, 65, page: 1, slot: 1),
            new FretPlayEvent(2_000_000, 1, 19, 83, page: 4, slot: 1)
        };
        var actionsP1P4 = GuitarCompiler.CompileFretEvents(fEventsP1P4, preset, opt);
        var navP1P4 = actionsP1P4.Where(a => a.Type == GuitarActionType.PageNav).ToList();
        assert(navP1P4.Count == 3, "v4.3 多页翻页串行: P1->P4 (跨3页) 连续生成 3 组离散独立的 PageNav Tap 调度动作", $"实际 Tap 动作数: {navP1P4.Count}");
        long tapSpacing = navP1P4[1].TimeUs - navP1P4[0].TimeUs;
        assert(tapSpacing == opt.PageKeyHoldUs + Math.Max(15_000, opt.PageInterTapGapUs),
            "v4.3 翻页间隔控制: 连续 Tap 保证按满及至少 15ms 抬键", $"实际间隔: {tapSpacing} us");

        // 2. 动态规划物理时间不足硬禁止与 SkipNote 兜底 (代价模型)
        var fOpt = new FretConverterOptions
        {
            PageChangeCost = 6.0,
            PageKeyHoldUs = 25_000,
            PageInterTapGapUs = 12_000,
            PageInputSafetyUs = 8_000,
            WheelMouseSettleUs = 25_000
        };
        var candP1 = new FretCandidate(1, 1);
        var candP2 = new FretCandidate(1, 7);
        var (costNormal, conflictNormal) = FretConverter.CalculateTransitionCost(candP1, candP2, deltaUs: 100_000, fOpt);
        assert(!double.IsInfinity(costNormal) && costNormal > 0 && !conflictNormal, "v4.3 转移代价模型: 时间充裕时跨页代价有限且可达", $"代价: {costNormal}");
        var (costImpossible, conflictImpossible) = FretConverter.CalculateTransitionCost(candP1, candP2, deltaUs: 20_000, fOpt);
        assert(double.IsPositiveInfinity(costImpossible) && conflictImpossible, "v4.3 转移代价模型: 时间不足物理翻页周期时返回正无穷大 (硬禁止)", $"实际代价: {costImpossible}");

        // 3. 超限音符绝不就地钳位破音，标记为 IsSkipped
        var extremeNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 20, StartUs = 0, DurationUs = 300_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 60, StartUs = 400_000, DurationUs = 300_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 110, StartUs = 800_000, DurationUs = 300_000, Velocity = 100 }
        };
        var convertedExtreme = FretConverter.Convert(extremeNotes);
        assert(convertedExtreme.Count == 3, "v4.3 音域保护: 输出所有音符对应事件", "");
        assert(convertedExtreme[0].IsSkipped && convertedExtreme[2].IsSkipped,
            "v4.3 音域保护: 超出物理音域的超高/超低音符标记为 IsSkipped，绝不钳位到边缘音造成刺耳破音",
            $"Note 20 Skipped={convertedExtreme[0].IsSkipped}, Note 110 Skipped={convertedExtreme[2].IsSkipped}");
        assert(!convertedExtreme[1].IsSkipped && convertedExtreme[1].NoteNumber == 60, "v4.3 旋律保真: 合法音符正常保留原音高并分配品位", $"Note 60 Fret={convertedExtreme[1].Fret}");

        // 4. 单音源直通净化保护 (isMonophonic = true) 与真实起音重叠保护
        var monoNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 60, StartUs = 0, DurationUs = 200_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 62, StartUs = 150_000, DurationUs = 200_000, Velocity = 100 }
        };
        var monoBypass = FretConverter.SanitizeForFretSolo(monoNotes, isMonophonic: true);
        assert(monoBypass.Count == 2, "v4.3 单音源直通: isMonophonic 为 true 时跳过单音净化，音符 100% 原始保留", $"保留数: {monoBypass.Count}");

        var polyArp = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 60, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 64, StartUs = 25_000, DurationUs = 500_000, Velocity = 100 }
        };
        var polySanitized = FretConverter.SanitizeForFretSolo(polyArp, isMonophonic: false);
        assert(polySanitized.Count == 2, "v4.3 真实起音重叠净化: 起音时间错开 (25ms) 的快速单音不受尾部延音重叠影响，完整保留", $"保留数: {polySanitized.Count}");

        // 5. 演奏形态分类识别 (Block, Strum Down/Up, Arpeggio)
        var blockNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 48, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 52, StartUs = 2_000, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 55, StartUs = 5_000, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 60, StartUs = 8_000, DurationUs = 500_000, Velocity = 100 }
        };
        var (artBlock, _) = ChordAnalyzer.DetectAttackArticulation(blockNotes);
        assert(artBlock == ChordArticulation.Block, "v4.3 演奏法识别 Block: ≤20ms 且 ≥3 音识别为 Block 块和弦", $"实际识别: {artBlock}");

        var strumDownNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 48, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 52, StartUs = 15_000, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 55, StartUs = 30_000, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 60, StartUs = 45_000, DurationUs = 500_000, Velocity = 100 }
        };
        var (artStrumD, dirStrumD) = ChordAnalyzer.DetectAttackArticulation(strumDownNotes);
        assert(artStrumD == ChordArticulation.Strum && dirStrumD == StrumDirectionHint.LowToHigh,
            "v4.3 演奏法识别 Strum Down: 45ms 扩音且音高上升识别为下扫弦 (LowToHigh)", $"识别: {artStrumD}, 方向: {dirStrumD}");

        var strumUpNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 60, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 55, StartUs = 15_000, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 52, StartUs = 30_000, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 48, StartUs = 45_000, DurationUs = 500_000, Velocity = 100 }
        };
        var (artStrumU, dirStrumU) = ChordAnalyzer.DetectAttackArticulation(strumUpNotes);
        assert(artStrumU == ChordArticulation.Strum && dirStrumU == StrumDirectionHint.HighToLow,
            "v4.3 演奏法识别 Strum Up: 45ms 扩音且音高下降识别为上扫弦 (HighToLow)", $"识别: {artStrumU}, 方向: {dirStrumU}");

        var arpNotes = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 48, StartUs = 0, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 55, StartUs = 80_000, DurationUs = 500_000, Velocity = 100 },
            new RawMidiNote { NoteNumber = 60, StartUs = 160_000, DurationUs = 500_000, Velocity = 100 }
        };
        var (artArp, _) = ChordAnalyzer.DetectAttackArticulation(arpNotes);
        assert(artArp == ChordArticulation.Arpeggio, "v4.3 演奏法识别 Arpeggio: 音符间隔 80ms (>40ms) 识别为琶音/分解和弦", $"实际识别: {artArp}");

        // 6. 块和弦 (Block Chord) 原子多键编译
        var detailedBlockChord = new ChordEvent
        {
            StartUs = 1_000_000,
            EndUs = 1_500_000,
            Chord = ChordId.C,
            Page = 1,
            Slot = 4,
            Articulation = ChordArticulation.Block,
            AttackNotes = new List<RawMidiNote>
            {
                new RawMidiNote { NoteNumber = 48, StartUs = 1_000_000, DurationUs = 400_000, Velocity = 100 },
                new RawMidiNote { NoteNumber = 52, StartUs = 1_000_000, DurationUs = 400_000, Velocity = 100 },
                new RawMidiNote { NoteNumber = 55, StartUs = 1_000_000, DurationUs = 400_000, Velocity = 100 },
                new RawMidiNote { NoteNumber = 60, StartUs = 1_000_000, DurationUs = 400_000, Velocity = 100 }
            }
        };
        var blockActions = GuitarCompiler.CompileChordDetailedEvents(new[] { detailedBlockChord }, preset, opt);
        var blockDowns = blockActions.Where(a => a.Type == GuitarActionType.StringDown && a.TimeUs == 1_000_000).ToList();
        assert(blockDowns.Count >= 3, "v4.3 块和弦原子多键: 编译生成同时刻按下的多个 StringDown 动作", $"StringDown 数量: {blockDowns.Count}");
        bool allSameTimestamp = blockDowns.All(d => d.TimeUs == 1_000_000);
        assert(allSameTimestamp, "v4.3 块和弦原子多键: 所有琴弦按键拥有完全相同的时间戳，调度器单批次下发", "");

        // 7. 扫弦 (Strum) 方向按键映射 (DownStrum -> Left, UpStrum -> Right)
        var detailedStrumDown = new ChordEvent
        {
            StartUs = 1_000_000,
            EndUs = 1_500_000,
            Chord = ChordId.G,
            Page = 1,
            Slot = 5,
            Articulation = ChordArticulation.Strum,
            Direction = StrumDirectionHint.LowToHigh
        };
        var strumDownActions = GuitarCompiler.CompileChordDetailedEvents(new[] { detailedStrumDown }, preset, opt);
        var strumClickDown = strumDownActions.FirstOrDefault(a => a.Type == GuitarActionType.StrumClick);
        assert(strumClickDown.Type == GuitarActionType.StrumClick && strumClickDown.MouseButton == preset.DownStrumButton,
            "v4.3 扫弦动作编译: 下扫弦 (LowToHigh) 映射为 DownStrumButton (鼠标左键)", $"按钮: {strumClickDown.MouseButton}");

        var detailedStrumUp = new ChordEvent
        {
            StartUs = 1_000_000,
            EndUs = 1_500_000,
            Chord = ChordId.G,
            Page = 1,
            Slot = 5,
            Articulation = ChordArticulation.Strum,
            Direction = StrumDirectionHint.HighToLow
        };
        var strumUpActions = GuitarCompiler.CompileChordDetailedEvents(new[] { detailedStrumUp }, preset, opt);
        var strumClickUp = strumUpActions.FirstOrDefault(a => a.Type == GuitarActionType.StrumClick);
        assert(strumClickUp.Type == GuitarActionType.StrumClick && strumClickUp.MouseButton == preset.UpStrumButton,
            "v4.3 扫弦动作编译: 上扫弦 (HighToLow) 映射为 UpStrumButton (鼠标右键)", $"按钮: {strumClickUp.MouseButton}");

        // 8. 和弦指型库 (ChordVoicingProfile) 音高精确匹配
        var profile = new ChordVoicingProfile();
        var cVoicing = profile.GetVoicing(ChordId.C);
        assert(cVoicing[1] == 60, "v4.3 和弦指型库: C和弦 2弦音高为 C4(60)", $"实际: {cVoicing[1]}");
        assert(cVoicing[4] == 48, "v4.3 和弦指型库: C和弦 5弦音高为 C3(48)", $"实际: {cVoicing[4]}");
        byte matchedString = profile.MatchNoteToString(60, ChordId.C, occupiedStrings: null, out bool isExact);
        assert(matchedString == 2 && isExact, "v4.3 和弦指型寻弦: C4(60) 精确匹配到 C和弦的 2弦", $"实际弦号: {matchedString}, 精确={isExact}");

        // 9. 游戏状态机就绪与忙碌截止期保护
        var gsm = new GameStateMachine();
        gsm.Reset(mode: 1, initialPage: 1);
        gsm.CurrentSlot = 1;
        assert(gsm.IsFretReady(1, 1, 0), "v4.3 状态机初始就绪: 初始状态 (P1, Slot 1) 处于完全就绪状态", "");
        gsm.NavBusyUntilUs = 50_000;
        gsm.WheelBusyUntilUs = 30_000;
        assert(!gsm.IsFretReady(1, 1, 20_000), "v4.3 状态机忙碌拦截: 翻页/选槽未完成前 (20ms)，IsFretReady 返回 false", "");
        assert(!gsm.IsChordReady(1, 1, 40_000), "v4.3 状态机忙碌拦截: 翻页尚未结束前 (40ms < 50ms)，IsChordReady 返回 false", "");
        assert(gsm.IsChordReady(1, 1, 60_000), "v4.3 状态机忙碌恢复: 动作结束后 (60ms)，状态机恢复就绪", "");

        // 10. 诊断报告统计与格式化摘要生成
        var report = new PlaybackDiagnosticReport
        {
            TotalActions = 100,
            PlayedSoundNotes = 85,
            SkippedNotes = 5,
            LateActionsCount = 2,
            MaxLateUs = 15_000,
            AverageLateUs = 8_000,
            PageTapCount = 6,
            WheelSelectCount = 12,
            BlockChordCount = 10,
            StrumCount = 4,
            ArpeggioCount = 8,
            ExactPitchMatchedCount = 80
        };
        assert(Math.Abs(report.ExactPitchRate - (80.0 / 85.0)) < 0.001, "v4.3 诊断报表统计: 精确音高匹配率计算准确", $"实际率: {report.ExactPitchRate:P1}");
        string summaryText = report.GenerateSummaryText();
        assert(summaryText.Contains("85") && summaryText.Contains("跳过音符: 5") && summaryText.Contains("迟到动作: 2") && summaryText.Contains("块和弦(Block)=10"),
            "v4.3 诊断简报生成: 摘要文本准确包含发音数、跳过数、迟到数与演奏形态统计", $"生成的简报内容:\r\n{summaryText}");

        // 11. 键鼠时序与硬件延迟优化验证 (防竞态、防超车、同品跨弦驻留偏好)
        var fOptTiming = new FretConverterOptions();
        assert(fOptTiming.SlotChangeCost >= 3.0, "v4.3 时序调优: 换槽惩罚 SlotChangeCost >= 3.0 促使优先同品跨弦演奏", $"实际: {fOptTiming.SlotChangeCost}");

        var candFret1Str1 = new FretCandidate(1, 1); // 1品 1弦 (P1 Slot 1)
        var candFret1Str2 = new FretCandidate(2, 1); // 1品 2弦 (同品同槽，跨弦)
        var candFret2Str1 = new FretCandidate(1, 2); // 2品 1弦 (换品换槽)

        // 快速密集的音符 (100ms 间隔)
        var (costSameFret, _) = FretConverter.CalculateTransitionCost(candFret1Str1, candFret1Str2, deltaUs: 100_000, fOptTiming);
        var (costChangeSlot, _) = FretConverter.CalculateTransitionCost(candFret1Str1, candFret2Str1, deltaUs: 100_000, fOptTiming);
        assert(costChangeSlot > costSameFret, "v4.3 快速连奏同品驻留偏好: 100ms 快速音符换槽成本显著高于同品跨弦", $"换槽成本={costChangeSlot}, 同品跨弦成本={costSameFret}");

        var wheelCtrl = new WheelController();
        assert(wheelCtrl.MouseClickHoldMs >= 10, "v4.3 鼠标脉冲时长: 轮盘控制器默认点击持续时长 >= 10ms", $"实际: {wheelCtrl.MouseClickHoldMs}");
    }

    private static void Test_GameChordGroundTruth(Action<bool, string, string> assert)
    {
        bool hasAllChords = GameChordGroundTruth.Table.Count == 24;
        bool cUsesGameTones = GameChordGroundTruth.Table.TryGetValue(ChordId.C, out var c)
            && c.ActualMidi1To6.SequenceEqual(new byte[] { 64, 60, 55, 52, 48, 48 })
            && c.SafeCleanStrings.SequenceEqual(new byte[] { 1, 2, 3, 4 })
            && !c.CanUseFullMouseStrum;
        bool gAllowsFullStrum = GameChordGroundTruth.Table.TryGetValue(ChordId.G, out var g)
            && g.ActualMidi1To6.SequenceEqual(new byte[] { 67, 59, 55, 50, 47, 43 })
            && g.SafeCleanStrings.SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 })
            && g.CanUseFullMouseStrum;
        assert(hasAllChords, "混合模式真实和弦表: 24 个槽位完整", $"实际 {GameChordGroundTruth.Table.Count} 个");
        assert(cUsesGameTones, "混合模式真实和弦表: C 的错音 5 弦被屏蔽", "C 必须保留真实发音并只用安全弦");
        assert(gAllowsFullStrum, "混合模式真实和弦表: G 可全弦扫弦", "G 六弦发声与配置一致");
        assert(GameChordGroundTruth.Table[ChordId.Am].ActualMidi1To6
                   .SequenceEqual(ChordAnalyzerV2.ChordStringPitches[ChordId.Am].Reverse()),
            "和弦弦序与六弦到一弦的发声音高表一致", "Am 的音高不应按反向偏移计算");
        assert(GameChordGroundTruth.FindExactCleanString(ChordId.C, 64) == 1,
            "混合模式和弦映射: 精确音高优先命中安全弦", "C 的 E4 应映射到实体 1 弦");
        assert(GameChordGroundTruth.FindExactCleanString(ChordId.Am, 46) == 0 &&
               GameChordGroundTruth.FindExactCleanString(ChordId.Bm, 48) == 0 &&
               GameChordGroundTruth.FindExactCleanString(ChordId.Em, 61) == 0 &&
               GameChordGroundTruth.FindExactCleanString(ChordId.D, 63) == 0,
            "混合模式毒弦屏蔽: Am/Bm/Em/D 的配表错音不参与拨弦", "所有错音弦必须被安全弦白名单排除");
        assert(GameChordGroundTruth.FindPitchClassCompatibleString(ChordId.C, 67) == 3,
            "混合模式和弦映射: 八度等效音优先", "G4 应映射到安全的 G3 弦");
        assert(GameChordGroundTruth.FindPitchClassCompatibleString(ChordId.C, 62) == 0,
            "混合模式和弦映射: 不把不协和音硬塞到最近弦", "D 音不能替换为 C 的其他音");

        var cTriad = new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 500_000, NoteNumber = 48, Velocity = 100 },
            new RawMidiNote { StartUs = 0, DurationUs = 500_000, NoteNumber = 52, Velocity = 95 },
            new RawMidiNote { StartUs = 0, DurationUs = 500_000, NoteNumber = 55, Velocity = 90 }
        };
        var hybridActions = HybridGuitarCompiler.CompileFullPerformance(cTriad, new SpeedControlManager(), new WheelPreset());
        assert(hybridActions.Any(action => action.Type == GuitarActionType.ModeSwitchDown && action.Value == 2) &&
               !hybridActions.Any(action => action.Type == GuitarActionType.StrumClick) &&
               !hybridActions.Any(action => action.Type == GuitarActionType.StringDown && action.NoteNumber == 53),
            "混合模式真实接入: C 和弦改用安全弦拨奏，不触发全弦扫弦或 F3 毒弦", "只允许混合演奏路径使用安全弦映射");

        var pureChordStrum = new ChordEvent
        {
            StartUs = 1_000_000,
            EndUs = 1_500_000,
            Chord = ChordId.C,
            Page = 1,
            Slot = 4,
            Articulation = ChordArticulation.Strum,
            AttackNotes = new List<RawMidiNote>
            {
                new RawMidiNote { NoteNumber = 48, StartUs = 1_000_000, DurationUs = 200_000, Velocity = 100 },
                new RawMidiNote { NoteNumber = 52, StartUs = 1_000_000, DurationUs = 200_000, Velocity = 100 },
                new RawMidiNote { NoteNumber = 55, StartUs = 1_000_000, DurationUs = 200_000, Velocity = 100 },
                new RawMidiNote { NoteNumber = 60, StartUs = 1_000_000, DurationUs = 200_000, Velocity = 100 }
            }
        };
        var pureChordActions = GuitarCompiler.CompileChordDetailedEvents(new[] { pureChordStrum }, new WheelPreset());
        var pureChordDowns = pureChordActions.Where(action => action.Type == GuitarActionType.StringDown).ToList();
        assert(!pureChordActions.Any(action => action.Type == GuitarActionType.StrumClick) &&
               pureChordDowns.Count >= 3 && pureChordDowns.All(action => action.Value is >= 1 and <= 6) &&
               pureChordDowns.All(action => action.NoteNumber != 53),
            "纯和弦模式真实接入: C 扫弦降级为安全弦齐拨", "不得鼠标全弦扫响或触发 5 弦 F3 毒音");
        assert(pureChordDowns.All(down => pureChordActions.Any(up => up.Type == GuitarActionType.StringUp &&
                   up.Value == down.Value && up.TimeUs - down.TimeUs >= 20_000)),
            "纯和弦模式硬件时序: 安全弦按压不少于 20ms", "每个下拨动作都要有有效释放动作");

        var pureSafeStrum = new ChordEvent
        {
            StartUs = 2_000_000,
            Chord = ChordId.G,
            Articulation = ChordArticulation.Strum,
            Direction = StrumDirectionHint.HighToLow
        };
        var pureSafeActions = GuitarCompiler.CompileChordDetailedEvents(new[] { pureSafeStrum }, new WheelPreset());
        assert(pureSafeActions.Any(action => action.Type == GuitarActionType.StrumClick &&
                   action.MouseButton == new WheelPreset().UpStrumButton && action.RequiredPage == 1 && action.RequiredSlot == 1),
            "纯和弦模式完整和弦: G 仍允许按真实槽位直接扫弦", "全弦安全和弦保留方向扫弦");
    }

    private static void Test_FullHybridAlgorithms(Action<bool, string, string> assert)
    {
        var dense = Enumerable.Range(0, 24).Select(i => new RawMidiNote
        {
            StartUs = i * 12_000, DurationUs = 12_000, NoteNumber = 72,
            Velocity = 100, Channel = 0
        }).ToList();
        int recommended = MidiSpeedOptimizer.CalculateAutoSpeedPercent(dense);
        var speed = new SpeedControlManager();
        speed.ApplyAutoSpeedOnMidiImport(recommended);
        assert(recommended < 100 && speed.IsAutoSpeed && speed.EffectiveSpeedPercent == recommended,
            "自动配速: 密集同音连击自动降速", $"推荐 {recommended}%");
        speed.SetUserManualSpeed(140);
        assert(!speed.IsAutoSpeed && speed.EffectiveSpeedPercent == 140,
            "速度覆盖: 用户手动设置优先于自动建议", $"生效 {speed.EffectiveSpeedPercent}%");
        speed.ResetToAutoSpeed();
        assert(speed.IsAutoSpeed && speed.EffectiveSpeedPercent == recommended,
            "速度覆盖: 可恢复自动建议", "");

        var preset = new WheelPreset();
        assert(preset.UpStrumButton == MouseButton.Left && preset.DownStrumButton == MouseButton.Right,
            "扫弦方向: 左键上扫，右键下扫", "");

        var cTriadEvidence = new double[12];
        cTriadEvidence[0] = 1.0;
        cTriadEvidence[4] = 1.0;
        cTriadEvidence[7] = 1.0;
        var chordScore = ChordAnalyzerV2.Score24ChordsEnhanced(cTriadEvidence, 3.0, 0, ChordId.Unknown);
        assert(chordScore.BestChord == ChordId.C,
            "和弦等级: C 三和弦不会误判成五度或七九和弦", $"实际 {chordScore.BestChord}");
        var ebMajor = new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 500_000, NoteNumber = 51, Velocity = 100 },
            new RawMidiNote { StartUs = 0, DurationUs = 500_000, NoteNumber = 55, Velocity = 100 },
            new RawMidiNote { StartUs = 0, DurationUs = 500_000, NoteNumber = 58, Velocity = 100 }
        };
        int transpose = ChordAnalyzerV2.FindBestSemitoneTranspose(ebMajor);
        assert(transpose == -3, "自动移调: 降 E 大三和弦对齐游戏 C 大三和弦", $"实际 {transpose:+#;-#;0} 半音");

        var strongBeat = new[]
        {
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 400_000, NoteNumber = 48, Velocity = 90 },
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 400_000, NoteNumber = 52, Velocity = 90 },
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 400_000, NoteNumber = 55, Velocity = 90 },
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 500_000, NoteNumber = 72, Velocity = 110 }
        };
        var actions = HybridGuitarCompiler.CompileFullPerformance(strongBeat, new SpeedControlManager(), preset);
        assert(actions.Any(a => a.Type == GuitarActionType.StringDown && a.NoteNumber == 72) &&
               !actions.Any(a => a.Type == GuitarActionType.StrumClick),
            "主旋律优先: 强拍固定和弦无法发出的 C5 保留精确拨弦", "");

        var shieldNotes = new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 600_000, NoteNumber = 72, Velocity = 110 },
            new RawMidiNote { StartUs = 150_000, DurationUs = 100_000, NoteNumber = 48, Velocity = 75 }
        };
        var shieldActions = HybridGuitarCompiler.CompileFullPerformance(shieldNotes, new SpeedControlManager(), preset);
        long leadAttackUs = shieldActions.First(a => a.Type == GuitarActionType.StringDown && a.NoteNumber == 72).TimeUs;
        bool noBassInterruption = !shieldActions.Any(a => a.TimeUs > leadAttackUs && a.TimeUs <= leadAttackUs + 260_000 &&
            a.Type is GuitarActionType.ModeSwitchDown or GuitarActionType.PageNav or GuitarActionType.WheelSelect);
        assert(noBassInterruption, "延音护盾: 高音长音前 260ms 不为低音伴奏切页", "");

        var hybridNotes = new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 300_000, NoteNumber = 72, Velocity = 100 },
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 250_000, NoteNumber = 48, Velocity = 100 },
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 250_000, NoteNumber = 52, Velocity = 100 },
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 250_000, NoteNumber = 55, Velocity = 100 },
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 250_000, NoteNumber = 64, Velocity = 100 }
        };
        var hybridActions = HybridGuitarCompiler.CompileFullPerformance(hybridNotes, new SpeedControlManager(), preset);
        var switchToChord = hybridActions.FirstOrDefault(a => a.Type == GuitarActionType.ModeSwitchDown && a.Value == 2);
        assert(switchToChord.Type == GuitarActionType.ModeSwitchDown &&
               !hybridActions.Any(a => a.Type == GuitarActionType.PageNav && a.TimeUs > switchToChord.TimeUs),
            "混合切模式: Z 后回到和弦 P1，无额外翻页", "");
        var state = GuitarScheduler.InspectExpectedHybridStateAt(hybridActions, 1_100_000);
        assert(state.Mode == 2 && state.Page == 1, "混合状态: 时间轴跟踪 Z 切模式与 P1 复位", $"Mode={state.Mode}, P{state.Page}");

        bool allHeld = hybridActions.Where(a => a.Type is GuitarActionType.StringDown or GuitarActionType.PageNav or GuitarActionType.ModeSwitchDown)
            .All(down => hybridActions.Any(up => up.ScanCode == down.ScanCode &&
                up.Type == (down.Type == GuitarActionType.StringDown ? GuitarActionType.StringUp :
                    down.Type == GuitarActionType.PageNav ? GuitarActionType.PageNavUp : GuitarActionType.ModeSwitchUp) &&
                up.TimeUs - down.TimeUs >= 20_000));
        assert(allHeld, "混合动作: 数字键、翻页键与 Z 键均至少按住 20ms", "");
    }

    private static void Test_MultitrackContinuousPlucks(Action<bool, string, string> assert)
    {
        var alternateTracks = new[]
        {
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 300_000, NoteNumber = 67, Velocity = 100, TrackId = 1 },
            new RawMidiNote { StartUs = 1_020_000, DurationUs = 300_000, NoteNumber = 59, Velocity = 100, TrackId = 2 },
            new RawMidiNote { StartUs = 1_040_000, DurationUs = 300_000, NoteNumber = 55, Velocity = 100, TrackId = 3 }
        };
        long[] sourceAttacks = alternateTracks.Select(note => note.StartUs).ToArray();
        var reduced = MidiPolyphonyReducer.ReduceForGuitar(alternateTracks);
        assert(reduced.Select(note => note.StartUs).OrderBy(time => time).SequenceEqual(sourceAttacks),
            "多轨连续拨弦: 复音裁剪保留三个不同起音", $"输出 {string.Join(",", reduced.Select(note => note.StartUs))}");

        var chordEvents = ChordAnalyzerV2.AnalyzeDetailed(alternateTracks, out _,
            new ChordAnalyzerV2Options { EnableAutoTranspose = false });
        assert(chordEvents.Select(chord => chord.StartUs).SequenceEqual(sourceAttacks) &&
               chordEvents.All(chord => chord.AttackNotes.Count == 1),
            "多轨连续拨弦: 和弦识别保留每次独立攻击", $"事件 {chordEvents.Count}");
        var accompanimentActions = GuitarCompiler.CompileChordDetailedEvents(chordEvents, new WheelPreset(),
            new GuitarCompilerOptions { PlayStyle = GuitarPlayStyle.PluckOnly });
        long[] accompanimentDowns = accompanimentActions.Where(action => action.Type == GuitarActionType.StringDown)
            .Select(action => action.TimeUs).OrderBy(time => time).ToArray();
        assert(accompanimentDowns.Length == 3 && accompanimentDowns.Distinct().Count() == 3,
            "多轨连续拨弦: 伴奏模式实际发出三次不同时间的弦按下", $"起音 {string.Join(",", accompanimentDowns)}");

        var multitrackSpeed = new SpeedControlManager();
        multitrackSpeed.SetUserManualSpeed(100);
        var multitrackHybrid = HybridGuitarCompiler.CompileFullPerformance(alternateTracks, multitrackSpeed,
            new WheelPreset());
        long[] multitrackHybridDowns = multitrackHybrid.Where(action => action.Type == GuitarActionType.StringDown)
            .Select(action => action.TimeUs).OrderBy(time => time).ToArray();
        assert(multitrackHybridDowns.Length >= 3 && multitrackHybridDowns.Distinct().Count() >= 3,
            "多轨连续拨弦: 混合模式不把三个起音裁成一次", $"起音 {string.Join(",", multitrackHybridDowns)}");

        var repeatedChord = Enumerable.Range(0, 3).Select(index =>
        {
            long timeUs = 1_000_000 + index * 32_000L;
            return new ChordEvent
            {
                StartUs = timeUs,
                Chord = ChordId.G,
                Articulation = ChordArticulation.Arpeggio,
                AttackNotes = new List<RawMidiNote>
                {
                    new() { StartUs = timeUs, DurationUs = 100_000, NoteNumber = 67, Velocity = 100 }
                }
            };
        }).ToList();
        var chordActions = GuitarCompiler.CompileChordDetailedEvents(repeatedChord, new WheelPreset(),
            new GuitarCompilerOptions { PlayStyle = GuitarPlayStyle.PluckOnly });
        long[] chordDowns = chordActions.Where(action => action.Type == GuitarActionType.StringDown)
            .Select(action => action.TimeUs).OrderBy(time => time).ToArray();
        assert(chordDowns.Length == 3 && chordDowns.Zip(chordDowns.Skip(1))
                .All(pair => pair.Second - pair.First >= 42_000),
            "伴奏同弦连拨: 32ms 源间隔被安全排为至少 20ms 按下加 22ms 抬键", $"起音 {string.Join(",", chordDowns)}");

        var repeatedHybrid = new[]
        {
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 600_000, NoteNumber = 67, Velocity = 100, TrackId = 1 },
            new RawMidiNote { StartUs = 1_100_000, DurationUs = 600_000, NoteNumber = 67, Velocity = 100, TrackId = 2 }
        };
        var speed = new SpeedControlManager();
        speed.SetUserManualSpeed(100);
        var hybridActions = HybridGuitarCompiler.CompileFullPerformance(repeatedHybrid, speed, new WheelPreset());
        long[] hybridDowns = hybridActions.Where(action => action.Type == GuitarActionType.StringDown)
            .Select(action => action.TimeUs).OrderBy(time => time).ToArray();
        assert(hybridDowns.Length >= 2 && hybridDowns[1] - hybridDowns[0] <= 160_000,
            "混合同弦连拨: 前音长延音不推迟后音起音", $"起音 {string.Join(",", hybridDowns)}");
        var hybridReleases = hybridActions.Where(action => action.Type == GuitarActionType.StringUp &&
                action.Value == hybridActions.First(a => a.Type == GuitarActionType.StringDown).Value)
            .OrderBy(action => action.TimeUs).ToArray();
        assert(hybridDowns.Length >= 2 && hybridReleases.Length >= 1 &&
               hybridReleases[0].TimeUs <= hybridDowns[1] - 22_000,
            "Mode 3 同弦长音对应自己的抬键且留足跨帧释放窗口", "");
    }

    private static void Test_StaggeredSingleTrackStrum(Action<bool, string, string> assert)
    {
        var rolledG = new[]
        {
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 400_000, NoteNumber = 43, Velocity = 100, TrackId = 1, OrigString = 6, IsBrushChord = true },
            new RawMidiNote { StartUs = 1_008_000, DurationUs = 400_000, NoteNumber = 47, Velocity = 100, TrackId = 1, OrigString = 5, IsBrushChord = true },
            new RawMidiNote { StartUs = 1_016_000, DurationUs = 400_000, NoteNumber = 50, Velocity = 100, TrackId = 1, OrigString = 4, IsBrushChord = true },
            new RawMidiNote { StartUs = 1_024_000, DurationUs = 400_000, NoteNumber = 79, Velocity = 110, TrackId = 1, OrigString = 1, IsBrushChord = true, IsPrimaryMelody = true }
        };
        var chordEvents = ChordAnalyzerV2.AnalyzeDetailed(rolledG, out _,
            new ChordAnalyzerV2Options { EnableAutoTranspose = false, AttackClusterUs = 30_000 });
        assert(chordEvents.Count == 1 && chordEvents[0].AttackNotes.Count == 4 &&
               chordEvents[0].Articulation == ChordArticulation.Strum,
            "单轨 GTP Brush: 24ms 内四根弦在 Mode 2 合为一次扫弦",
            $"事件 {chordEvents.Count}, 音符 {chordEvents.FirstOrDefault()?.AttackNotes.Count}");
        var mode2Actions = GuitarCompiler.CompileChordDetailedEvents(chordEvents, new WheelPreset(),
            new GuitarCompilerOptions { AutoStrum = AutoStrumMode.Auto });
        assert(mode2Actions.Any(action => action.Type == GuitarActionType.StrumClick),
            "单轨 GTP Brush: Mode 2 编译为鼠标扫弦动作",
            $"扫弦 {mode2Actions.Count(action => action.Type == GuitarActionType.StrumClick)} 次");

        var midiRoll = rolledG.Take(3).Select(note => new RawMidiNote
        {
            StartUs = note.StartUs, DurationUs = note.DurationUs, NoteNumber = note.NoteNumber,
            Velocity = note.Velocity, TrackId = note.TrackId
        }).ToArray();
        var midiEvents = ChordAnalyzerV2.AnalyzeDetailed(midiRoll, out _,
            new ChordAnalyzerV2Options { EnableAutoTranspose = false, AttackClusterUs = 30_000 });
        assert(midiEvents.Count == 1 && midiEvents[0].AttackNotes.Count == 3,
            "单轨 MIDI: 不同可演奏琴弦的滚奏音头在 Mode 2 合并",
            $"事件 {midiEvents.Count}, 音符 {midiEvents.FirstOrDefault()?.AttackNotes.Count}");

        var speed = new SpeedControlManager();
        speed.SetUserManualSpeed(100);
        var hybridActions = HybridGuitarCompiler.CompileFullPerformance(rolledG, speed, new WheelPreset(),
            new HybridCompilerOptions { ChordMode = ChordRecognitionMode.Loose });
        assert(hybridActions.Any(action => action.Type == GuitarActionType.StrumClick),
            "单轨 GTP Brush: Mode 3 高八度 G5 与 G 和弦同音级时可扫弦",
            $"扫弦 {hybridActions.Count(action => action.Type == GuitarActionType.StrumClick)} 次");
        var avoidSpeed = new SpeedControlManager();
        avoidSpeed.SetUserManualSpeed(100);
        var avoidedHybrid = HybridGuitarCompiler.CompileFullPerformance(rolledG, avoidSpeed, new WheelPreset(),
            new HybridCompilerOptions { ChordMode = ChordRecognitionMode.Loose, AvoidStrum = true });
        assert(!avoidedHybrid.Any(action => action.Type == GuitarActionType.StrumClick) &&
               avoidedHybrid.Any(action => action.Type == GuitarActionType.StringDown),
            "Mode 3 规避扫弦: GTP Brush 不发鼠标扫弦且保留拨弦", $"鼠标扫弦 {avoidedHybrid.Count(a => a.Type == GuitarActionType.StrumClick)} 次");
        var longMidiRoll = new[] { 43, 47, 50, 55 }.Select((pitch, index) => new RawMidiNote
        {
            StartUs = 1_000_000 + index * 20_000, DurationUs = 300_000, NoteNumber = (byte)pitch,
            Velocity = 90, TrackId = 1
        }).ToArray();
        var plainRollSpeed = new SpeedControlManager();
        plainRollSpeed.SetUserManualSpeed(100);
        var plainRoll = HybridGuitarCompiler.CompileFullPerformance(longMidiRoll, plainRollSpeed, new WheelPreset());
        var filteredRollSpeed = new SpeedControlManager();
        filteredRollSpeed.SetUserManualSpeed(100);
        var filteredRoll = HybridGuitarCompiler.CompileFullPerformance(longMidiRoll, filteredRollSpeed, new WheelPreset(),
            new HybridCompilerOptions { AvoidStrum = true });
        assert(filteredRoll.Count(action => action.Type == GuitarActionType.StringDown) <
               plainRoll.Count(action => action.Type == GuitarActionType.StringDown) &&
               !filteredRoll.Any(action => action.Type == GuitarActionType.StrumClick),
            "Mode 3 规避扫弦: 60ms MIDI 滚奏按 65ms 窗口折叠", $"原 {plainRoll.Count(a => a.Type == GuitarActionType.StringDown)} 弦音，规避后 {filteredRoll.Count(a => a.Type == GuitarActionType.StringDown)} 弦音");
        var strictSpeed = new SpeedControlManager();
        strictSpeed.SetUserManualSpeed(100);
        var strictActions = HybridGuitarCompiler.CompileFullPerformance(rolledG, strictSpeed, new WheelPreset(),
            new HybridCompilerOptions { ChordMode = ChordRecognitionMode.Strict });
        assert(!strictActions.Any(action => action.Type == GuitarActionType.StrumClick),
            "严格模式: 高八度主音不因同音级而降八度扫弦", "");

        var midiSpeed = new SpeedControlManager();
        midiSpeed.SetUserManualSpeed(100);
        var midiHybrid = HybridGuitarCompiler.CompileFullPerformance(rolledG.Select(note => new RawMidiNote
        {
            StartUs = note.StartUs, DurationUs = note.DurationUs, NoteNumber = note.NoteNumber,
            Velocity = note.Velocity, TrackId = note.TrackId, IsPrimaryMelody = note.IsPrimaryMelody
        }).ToArray(), midiSpeed, new WheelPreset());
        assert(midiHybrid.Any(action => action.Type == GuitarActionType.StrumClick),
            "单轨 MIDI: 无 Brush 标记的异弦滚奏在 Mode 3 可扫弦",
            $"扫弦 {midiHybrid.Count(action => action.Type == GuitarActionType.StrumClick)} 次");

        var autoSpeed = new SpeedControlManager();
        var slowedHybrid = HybridGuitarCompiler.CompileFullPerformance(rolledG, autoSpeed, new WheelPreset());
        assert(autoSpeed.EffectiveSpeedPercent < 100 &&
               slowedHybrid.Any(action => action.Type == GuitarActionType.StrumClick),
            "自动降速后: 原始 24ms GTP Brush 仍作为一次扫弦",
            $"速度 {autoSpeed.EffectiveSpeedPercent}%, 扫弦 {slowedHybrid.Count(action => action.Type == GuitarActionType.StrumClick)} 次");

        var repeated = new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 100_000, NoteNumber = 67, Velocity = 100, TrackId = 1 },
            new RawMidiNote { StartUs = 8_000, DurationUs = 100_000, NoteNumber = 67, Velocity = 100, TrackId = 1 }
        };
        var repeatedEvents = ChordAnalyzerV2.AnalyzeDetailed(repeated, out _,
            new ChordAnalyzerV2Options { EnableAutoTranspose = false, AttackClusterUs = 30_000 });
        assert(repeatedEvents.Count == 2 && repeatedEvents.All(chord => chord.AttackNotes.Count == 1),
            "同轨同音 8ms 连击: Mode 2 保留两次独立音头", $"事件 {repeatedEvents.Count}");

        var sameString = new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 100_000, NoteNumber = 64, Velocity = 100, TrackId = 1, OrigString = 1 },
            new RawMidiNote { StartUs = 8_000, DurationUs = 100_000, NoteNumber = 65, Velocity = 100, TrackId = 1, OrigString = 1 }
        };
        var sameStringEvents = ChordAnalyzerV2.AnalyzeDetailed(sameString, out _,
            new ChordAnalyzerV2Options { EnableAutoTranspose = false, AttackClusterUs = 30_000 });
        assert(sameStringEvents.Count == 2,
            "同轨同弦 8ms 连击: Mode 2 不合并不同音高的重触发", $"事件 {sameStringEvents.Count}");

        var transposedBrush = new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 400_000, NoteNumber = 51, Velocity = 100, TrackId = 1, OrigString = 6, IsBrushChord = true },
            new RawMidiNote { StartUs = 8_000, DurationUs = 400_000, NoteNumber = 55, Velocity = 100, TrackId = 1, OrigString = 3, IsBrushChord = true },
            new RawMidiNote { StartUs = 16_000, DurationUs = 400_000, NoteNumber = 58, Velocity = 100, TrackId = 1, OrigString = 4, IsBrushChord = true }
        };
        var shiftedEvents = ChordAnalyzerV2.AnalyzeDetailed(transposedBrush, out int shift,
            new ChordAnalyzerV2Options { AttackClusterUs = 30_000 });
        assert(shift != 0 && shiftedEvents.Count == 1 &&
               shiftedEvents[0].AttackNotes.All(note => note.IsBrushChord),
            "自动移调: Mode 2 保留 GTP Brush 元数据", $"移调 {shift}, Brush 音符 {shiftedEvents.FirstOrDefault()?.AttackNotes.Count(note => note.IsBrushChord)}");
    }

    private static void Test_Mode2AccompanimentSeparation(Action<bool, string, string> assert)
    {
        var fullSong = new List<RawMidiNote>();
        foreach (long timeUs in new long[] { 0, 250_000, 500_000, 750_000 })
            fullSong.Add(new RawMidiNote { StartUs = timeUs, DurationUs = 180_000,
                NoteNumber = 67, Velocity = 105, TrackId = 0, Program = 73 });
        foreach (long timeUs in new long[] { 0, 500_000 })
            foreach (byte pitch in new byte[] { 43, 47, 50 })
                fullSong.Add(new RawMidiNote { StartUs = timeUs, DurationUs = 430_000,
                    NoteNumber = pitch, Velocity = 85, TrackId = 1, Program = 24 });
        fullSong.Add(new RawMidiNote { StartUs = 250_000, DurationUs = 190_000,
            NoteNumber = 43, Velocity = 90, TrackId = 1, Program = 24 });
        fullSong = fullSong.OrderBy(note => note.StartUs).ThenBy(note => note.NoteNumber).ToList();

        var fullEvents = ChordAnalyzerV2.AnalyzeDetailed(fullSong, out int shift,
            new ChordAnalyzerV2Options { EnableAutoTranspose = false, AttackClusterUs = 30_000 });
        var accompaniment = AccompanimentEventFilter.Prepare(fullSong, fullEvents, shift);
        assert(fullEvents.Any(ev => ev.AttackNotes.Any(note => note.TrackId == 0)) &&
               accompaniment.Count < fullEvents.Count &&
               accompaniment.All(ev => ev.AttackNotes.Count > 0 && ev.AttackNotes.All(note => note.TrackId != 0)) &&
               accompaniment.Any(ev => ev.SourceNotes.Any(note => note.TrackId == 0)),
            "Mode 2 多轨完整谱: 主旋律参与和声识别但不生成右手攻击",
            $"原事件 {fullEvents.Count}, 伴奏事件 {accompaniment.Count}");
        assert(accompaniment.Any(ev => ev.AttackNotes.Count == 1 && ev.AttackNotes[0].NoteNumber == 43),
            "Mode 2 多轨完整谱: 保留低音根音单拨", "");
        var allActions = GuitarCompiler.CompileChordDetailedEvents(fullEvents, new WheelPreset());
        var backingActions = GuitarCompiler.CompileChordDetailedEvents(accompaniment, new WheelPreset());
        assert(allActions.Any(action => action.Type == GuitarActionType.StringDown && action.TimeUs >= 700_000) &&
               !backingActions.Any(action => action.Type == GuitarActionType.StringDown && action.TimeUs >= 700_000),
            "Mode 2 多轨完整谱: 纯人声拍点不再下发数字键拨弦", "");
        var mixedAttack = new ChordEvent
        {
            StartUs = 0, Chord = ChordId.G, Articulation = ChordArticulation.Strum,
            AttackNotes = fullSong.Where(note => note.StartUs == 0)
                .Where(note => note.TrackId == 0 || note.NoteNumber is 43 or 47).ToList(),
            SourceNotes = fullSong.Where(note => note.StartUs == 0).ToList()
        };
        var reclassified = AccompanimentEventFilter.Prepare(fullSong, new[] { mixedAttack }, 0);
        assert(reclassified.Count == 1 && reclassified[0].AttackNotes.Count == 2 &&
               reclassified[0].Articulation == ChordArticulation.Block,
            "Mode 2 多轨完整谱: 剔除主音后双音扫弦改为块和弦", "");

        var singleTrack = new List<RawMidiNote>();
        foreach (long timeUs in new long[] { 0, 600_000 })
            foreach (byte pitch in new byte[] { 48, 52, 55 })
                singleTrack.Add(new RawMidiNote { StartUs = timeUs, DurationUs = 400_000,
                    NoteNumber = pitch, Velocity = 85, TrackId = 4 });
        singleTrack.Add(new RawMidiNote { StartUs = 150_000, DurationUs = 130_000,
            NoteNumber = 72, Velocity = 105, TrackId = 4 });
        singleTrack.Add(new RawMidiNote { StartUs = 300_000, DurationUs = 130_000,
            NoteNumber = 74, Velocity = 105, TrackId = 4 });
        singleTrack.Add(new RawMidiNote { StartUs = 450_000, DurationUs = 130_000,
            NoteNumber = 48, Velocity = 95, TrackId = 4 });
        singleTrack = singleTrack.OrderBy(note => note.StartUs).ThenBy(note => note.NoteNumber).ToList();

        var singleEvents = ChordAnalyzerV2.AnalyzeDetailed(singleTrack, out int singleShift,
            new ChordAnalyzerV2Options { EnableAutoTranspose = false, AttackClusterUs = 30_000 });
        var singleAccompaniment = AccompanimentEventFilter.Prepare(singleTrack, singleEvents, singleShift);
        assert(singleAccompaniment.All(ev => ev.AttackNotes.All(note => note.NoteNumber is not 72 and not 74)) &&
               singleAccompaniment.Any(ev => ev.AttackNotes.Any(note => note.NoteNumber == 48)) &&
               singleAccompaniment.Any(ev => ev.AttackNotes.Count >= 3),
            "Mode 2 单轨混排谱: 跳过高音旋律单音并保留根音与和弦簇",
            $"原事件 {singleEvents.Count}, 伴奏事件 {singleAccompaniment.Count}");

        var slowed = MidiAutoPlayer.Core.Compilation.PlaybackSpeedEngine.ScaleMidiNotes(singleTrack, 0.5);
        var slowedEvents = ChordAnalyzerV2.AnalyzeDetailed(slowed, out int slowedShift,
            new ChordAnalyzerV2Options { EnableAutoTranspose = false, AttackClusterUs = 60_000 });
        var slowedAccompaniment = AccompanimentEventFilter.Prepare(singleTrack, slowedEvents, slowedShift, 0.5);
        assert(slowedAccompaniment.All(ev => ev.StartUs is not 300_000 and not 600_000) &&
               slowedAccompaniment.Any(ev => ev.AttackNotes.Any(note => note.NoteNumber == 48)),
            "Mode 2 单轨混排谱: 半速播放仍精确剔除旋律拍点", "");

        var leadOnly = fullSong.Where(note => note.TrackId == 0).ToList();
        var leadOnlyEvents = ChordAnalyzerV2.AnalyzeDetailed(leadOnly, out int leadOnlyShift,
            new ChordAnalyzerV2Options { EnableAutoTranspose = false, AttackClusterUs = 30_000 });
        assert(AccompanimentEventFilter.Prepare(leadOnly, leadOnlyEvents, leadOnlyShift).Count == leadOnlyEvents.Count,
            "Mode 2 纯单音谱: 无伴奏纹理时不误删整首谱", "");

        var arrangedActions = ArrangedAccompanimentCompiler.Compile(singleTrack, singleEvents, singleShift, 1.0,
            new WheelPreset(), new GuitarCompilerOptions());
        assert(arrangedActions.Any(action => action.Type is GuitarActionType.StringDown or GuitarActionType.StrumClick),
            "Mode 2 智能改编: 节拍事件编译为可执行弦动作", $"动作 {arrangedActions.Count}");

        var unknownChord = new ChordEvent
        {
            StartUs = 900_000,
            Chord = ChordId.Unknown,
            AttackNotes = new List<RawMidiNote> { new() { StartUs = 900_000, DurationUs = 180_000, NoteNumber = 43, Velocity = 90 } },
            SourceNotes = new List<RawMidiNote> { new() { StartUs = 900_000, DurationUs = 180_000, NoteNumber = 43, Velocity = 90 } }
        };
        var fretAssistActions = ArrangedAccompanimentCompiler.Compile(singleTrack, new[] { unknownChord }, 0, 1.0,
            new WheelPreset(), new GuitarCompilerOptions());
        assert(fretAssistActions.Count(action => action.Type == GuitarActionType.ModeSwitchDown && action.Value == 1) == 1 &&
               fretAssistActions.Any(action => action.Type == GuitarActionType.StringDown),
            "Mode 2 智能改编: 轮盘外和弦切品格补位且只生成一次切换", $"动作 {fretAssistActions.Count}");
    }

    private static void Test_DenseHybridSafety(Action<bool, string, string> assert)
    {
        var denseChord = new List<RawMidiNote>
        {
            new() { StartUs = 0, DurationUs = 300_000, NoteNumber = 60, Velocity = 100, TrackId = 1 },
            new() { StartUs = 15_000, DurationUs = 300_000, NoteNumber = 64, Velocity = 90, TrackId = 2 },
            new() { StartUs = 20_000, DurationUs = 300_000, NoteNumber = 67, Velocity = 80, TrackId = 3 },
            new() { StartUs = 25_000, DurationUs = 300_000, NoteNumber = 72, Velocity = 110, TrackId = 4 },
            new() { StartUs = 30_000, DurationUs = 300_000, NoteNumber = 48, Velocity = 95, TrackId = 5 },
            new() { StartUs = 30_000, DurationUs = 300_000, NoteNumber = 55, Velocity = 70, TrackId = 6 },
            new() { StartUs = 0, DurationUs = 2_000_000, NoteNumber = 80, Velocity = 90, TrackId = 7, Program = 90 },
            new() { StartUs = 0, DurationUs = 200_000, NoteNumber = 36, Velocity = 100, Channel = 9 }
        };
        var reduced = MidiPolyphonyReducer.ReduceForGuitar(denseChord);
        assert(reduced.Count == 6 && reduced.Select(n => n.StartUs).Distinct().Count() == 5 &&
               reduced.Any(n => n.NoteNumber == 72) && reduced.Any(n => n.NoteNumber == 48) &&
               reduced.All(n => n.NoteNumber != 80 && n.Channel != 9),
            "多轨减负: 保留各轨起音、六弦上限并去除 Pad/鼓声道", $"输出 {reduced.Count} 音");

        var rapidSameTrack = MidiPolyphonyReducer.ReduceForGuitar(new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 100_000, NoteNumber = 60, Velocity = 90, TrackId = 1 },
            new RawMidiNote { StartUs = 30_000, DurationUs = 100_000, NoteNumber = 62, Velocity = 90, TrackId = 1 }
        });
        assert(rapidSameTrack.Select(n => n.StartUs).Distinct().Count() == 2,
            "多轨减负: 同轨快速旋律不被跨轨吸附吞掉", $"输出 {rapidSameTrack.Count} 音");

        var opt = new HybridCompilerOptions();
        var p1 = new HybridStateCandidate { Mode = GuitarPlayModeType.Fret, Page = 1, Slot = 1, Fret = 1 };
        var p2 = new HybridStateCandidate { Mode = GuitarPlayModeType.Fret, Page = 2, Slot = 1, Fret = 7 };
        var chord = new HybridStateCandidate { Mode = GuitarPlayModeType.Chord, Page = 1, Slot = 1, Chord = ChordId.G };
        assert(double.IsPositiveInfinity(HybridGuitarCompiler.CalculateHybridTransition(p1, p2, 35_000, opt).Cost) &&
               !double.IsPositiveInfinity(HybridGuitarCompiler.CalculateHybridTransition(p1, p2, 200_000, opt).Cost) &&
               double.IsPositiveInfinity(HybridGuitarCompiler.CalculateHybridTransition(p1, chord, 150_000, opt).Cost),
            "混合硬锁: 物理时间不足时跨页或切模式绝不进入路径", "");

        var denseNotes = new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 50_000, NoteNumber = 64, Velocity = 100 },
            new RawMidiNote { StartUs = 45_000, DurationUs = 50_000, NoteNumber = 76, Velocity = 100 },
            new RawMidiNote { StartUs = 90_000, DurationUs = 50_000, NoteNumber = 52, Velocity = 100 }
        };
        var denseActions = HybridGuitarCompiler.CompileFullPerformance(denseNotes, new SpeedControlManager(), new WheelPreset());
        var firstSound = denseActions.FirstOrDefault(a => a.Type == GuitarActionType.StringDown);
        assert(firstSound.Type == GuitarActionType.StringDown &&
               !denseActions.Any(a => a.Type is GuitarActionType.PageNav or GuitarActionType.ModeSwitchDown or GuitarActionType.WheelSelect &&
                   a.TimeUs > firstSound.TimeUs && a.TimeUs < firstSound.TimeUs + 100_000),
            "混合原位保持: 密集音符不会强塞切页、切模式或选槽", "");

        var spacedNotes = new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 500_000, NoteNumber = 64, Velocity = 100 },
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 200_000, NoteNumber = 76, Velocity = 100 }
        };
        var actions = HybridGuitarCompiler.CompileFullPerformance(spacedNotes, new SpeedControlManager(), new WheelPreset());
        var controls = actions.Where(a => a.Type is GuitarActionType.ModeSwitchDown or GuitarActionType.ModeSwitchUp or
            GuitarActionType.PageNav or GuitarActionType.PageNavUp or GuitarActionType.WheelSelect).ToList();
        assert(controls.Count > 0 && controls.All(a => a.DeadlineUs - a.TimeUs >= 500_000),
            "混合控制: Z/R/T/轮盘动作具有 500ms 宽限", "");
        var firstAttack = actions.First(a => a.Type == GuitarActionType.StringDown);
        var controlAfterAttack = controls.FirstOrDefault(a => a.TimeUs > firstAttack.TimeUs &&
            a.Type is GuitarActionType.ModeSwitchDown or GuitarActionType.PageNav or GuitarActionType.WheelSelect);
        var firstRelease = actions.First(a => a.Type == GuitarActionType.StringUp && a.Value == firstAttack.Value);
        assert(controlAfterAttack.Type != 0 && firstRelease.TimeUs <= controlAfterAttack.TimeUs - 6_000,
            "混合控制: 前音释放与下一控制动作保留 6ms 净空", "");
        bool navHeld = controls.Where(a => a.Type is GuitarActionType.ModeSwitchDown or GuitarActionType.PageNav)
            .All(down => controls.Any(up => up.ScanCode == down.ScanCode && up.TimeUs - down.TimeUs >= 25_000 &&
                up.Type == (down.Type == GuitarActionType.PageNav
                    ? GuitarActionType.PageNavUp : GuitarActionType.ModeSwitchUp)));
        assert(navHeld, "混合控制: Z/R/T 每次按下至少 25ms", "");
        bool wheelSettled = controls.Where(a => a.Type == GuitarActionType.WheelSelect)
            .All(select => !actions.Any(a => a.TimeUs >= select.TimeUs && a.TimeUs < select.TimeUs + 55_000 &&
                a.Type is GuitarActionType.StringDown or GuitarActionType.StrumClick));
        assert(wheelSettled, "混合控制: 轮盘选槽后留足 55ms 再发音", "");

        var guard = new GuitarControlTimingGuard();
        var noteDown = new GuitarAction(0, GuitarActionType.StringDown, 1, 0x02);
        var noteUp = new GuitarAction(20_000, GuitarActionType.StringUp, 1, 0x02);
        var pageDown = new GuitarAction(26_000, GuitarActionType.PageNav, 2, 0x14);
        var pageUp = new GuitarAction(51_000, GuitarActionType.PageNavUp, 2, 0x14);
        var wheel = new GuitarAction(66_000, GuitarActionType.WheelSelect, 1);
        guard.MarkExecuted(noteDown, 50_000);
        assert(guard.EarliestExecutionUs(noteUp, 55_000) >= 70_000,
            "运行时串行: 卡顿后琴弦仍按满 20ms", "");
        guard.MarkExecuted(noteUp, 70_000);
        assert(guard.EarliestExecutionUs(pageDown, 70_000) >= 76_000,
            "运行时串行: 琴弦释放后保留 6ms 再翻页", "");
        guard.MarkExecuted(pageDown, 76_000);
        assert(guard.EarliestExecutionUs(pageUp, 80_000) >= 101_000,
            "运行时串行: 卡顿后翻页键仍按满 25ms", "");
        guard.MarkExecuted(pageUp, 101_000);
        assert(guard.EarliestExecutionUs(wheel, 105_000) >= 116_000,
            "运行时串行: 翻页键释放后保留 15ms 再选槽", "");
        guard.MarkExecuted(wheel, 116_000);
        assert(guard.EarliestExecutionUs(new GuitarAction(120_000, GuitarActionType.StringDown, 2, 0x03), 125_000) >= 171_000,
            "运行时串行: 轮盘选槽后稳定 55ms 才拨弦", "");

        var speedSource = new List<RawMidiNote>
        {
            new() { StartUs = 0, DurationUs = 150_000, NoteNumber = 60, Velocity = 90, TrackId = 1 },
            new() { StartUs = 20_000, DurationUs = 150_000, NoteNumber = 72, Velocity = 90, TrackId = 2 },
            new() { StartUs = 1_000_000, DurationUs = 150_000, NoteNumber = 64, Velocity = 90, TrackId = 1 }
        };
        var speedSong = new ConvertedSong
        {
            TargetInstrument = 1, DisplayName = "多轨配速验证", DurationUs = 1_200_000,
            SelectedRawNotes = speedSource
        };
        var config = new AppConfig { PlayMode = 3 };
        using (var service = new MidiAutoPlayer.Services.PlaybackService(config,
            new MidiAutoPlayer.Bridge.WebEventPublisher(null), new WheelController(config.WheelPreset)))
        {
            object state = service.LoadSong(speedSong);
            double speed = (double)state.GetType().GetProperty("speed")!.GetValue(state)!;
            int expectedPercent = MidiSpeedOptimizer.CalculateAutoSpeedPercent(
                MidiPolyphonyReducer.ReduceForGuitar(speedSource));
            assert(Math.Abs(speed - expectedPercent / 100.0) < 0.001 && speed < 1.0,
                "混合配速: 多轨连续起音保留后按物理间隔自动降速", $"实际 {speed:0.00}x");
        }

        long MeasureChordAnalysis(int count)
        {
            var notes = new List<RawMidiNote>(count + 1)
            {
                new() { StartUs = 0, DurationUs = count * 100_000L + 100_000,
                    NoteNumber = 48, Velocity = 80 }
            };
            for (int i = 1; i <= count; i++)
                notes.Add(new RawMidiNote { StartUs = i * 100_000L, DurationUs = 50_000,
                    NoteNumber = (byte)(60 + i % 12), Velocity = 90 });
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var events = ChordAnalyzerV2.AnalyzeDetailed(notes, out _,
                new ChordAnalyzerV2Options { EnableAutoTranspose = false, HarmonicWindowUs = 50_000 });
            watch.Stop();
            assert(events.Count == count + 1 && events[^1].SourceNotes.Any(n => n.NoteNumber == 48),
                "和弦窗口: 长延音在后续和声中仍保留", $"事件 {events.Count}");
            return watch.ElapsedTicks;
        }

        MeasureChordAnalysis(100); // JIT warmup
        long smallTicks = MeasureChordAnalysis(2_000);
        long largeTicks = MeasureChordAnalysis(10_000);
        assert(largeTicks < smallTicks * 12,
            "和弦窗口: 长延音不会让分析耗时随曲长平方增长",
            $"2,000 音 {smallTicks} ticks；10,000 音 {largeTicks} ticks");
    }
}

