using Nte.GuitarChart.Core;
using Nte.GuitarChart.WinForms;
using MidiAutoPlayer.SemiAuto.Input;

namespace MidiAutoPlayer.Testing;

public static class SemiAutoAcceptanceTests
{
    public static void RunAll(Action<bool, string, string> assert)
    {
        Test_ImmersionHotkeyBinding_Validation(assert);
        Test_ImmersionSessionState_Lifecycle(assert);
        Test_PositionSwitchKeyBinding_RejectsReservedKeys(assert);
        Test_InputObserver_GameKeyMapping(assert);
        Test_GameLaneKeyBinding_DefaultsAndPhysicalMapping(assert);
        Test_LaneKeyReset_PersistsDefaults(assert);
        Test_PracticeTimeline_HoldPauseResumeAndSpeed(assert);
        Test_PracticeSession_TailRecovery(assert);
        // 1. GuitarChartCompiler Tests
        Test_Compiler_EmptyMidi(assert);
        Test_Compiler_FingeringCandidates_E4_E2(assert);
        Test_Compiler_FretToPageSlotMapping(assert);
        Test_Compiler_ConcurrentNotesConstraint_And_NoCommonFret(assert);
        Test_Compiler_SimultaneousNotesShareOnePosition(assert);
        Test_Compiler_NoSilentDeletion_OnTimingRisk(assert);
        Test_Compiler_OpeningPrePosition(assert);
        Test_Compiler_NoteTraceability(assert);

        // 2. Timing Model Tests (WheelController calibrated + P0 handling)
        Test_TimingModel_CalibratedWheelRules_And_P0(assert);


        // 4. Mode Isolation Tests
        Test_ModeIsolation_PracticeUsesChartTiming(assert);
        Test_ModeIsolation_PracticeFeedback(assert);
        Test_HitJudgement_FeedbackGrades(assert);
        Test_AutoShowcaseInputPlan(assert);

        // 5. Overlay and Bridge Tests
        Test_OverlayWindow_Properties_And_ClickThrough(assert);
        Test_Bridge_MessageContract_And_Validation(assert);
    }

    private static void Test_ImmersionHotkeyBinding_Validation(Action<bool, string, string> assert)
    {
        var lanes = GameLaneKeyBinding.CreateDefaultGameLanes();
        var position = new PhysicalKeyBinding(0x4A, 0x24);
        assert(MidiAutoPlayer.SemiAuto.Modes.ImmersionHotkeyBinding.DefaultVirtualKey == 0x49,
            "全沉浸快捷键", "默认全沉浸切换键为 I");
        assert(MidiAutoPlayer.SemiAuto.Modes.ImmersionHotkeyBinding.IsAllowed(0x49, 0x77, lanes, position),
            "全沉浸快捷键", "未占用的字母键可设置为全局快捷键");
        assert(!MidiAutoPlayer.SemiAuto.Modes.ImmersionHotkeyBinding.IsAllowed(0x1B, 0x77, lanes, position),
            "全沉浸快捷键", "Escape 不能设为快捷键");
        assert(!MidiAutoPlayer.SemiAuto.Modes.ImmersionHotkeyBinding.IsAllowed(0x31, 0x77, lanes, position),
            "全沉浸快捷键", "已分配给轨道的按键不能设为快捷键");
        assert(!MidiAutoPlayer.SemiAuto.Modes.ImmersionHotkeyBinding.IsAllowed(0x4A, 0x77, lanes, position),
            "全沉浸快捷键", "换位键不能设为快捷键");
        assert(!MidiAutoPlayer.SemiAuto.Modes.ImmersionHotkeyBinding.IsAllowed(0x77, 0x77, lanes, position),
            "全沉浸快捷键", "主程序播放快捷键不能重复绑定");
        assert(!MidiAutoPlayer.SemiAuto.Modes.ImmersionHotkeyBinding.IsAllowed(0x78, 0x77, lanes, position),
            "全沉浸快捷键", "F9 悬浮窗快捷键不能重复绑定");
    }

    private static void Test_ImmersionSessionState_Lifecycle(Action<bool, string, string> assert)
    {
        var state = new MidiAutoPlayer.SemiAuto.Modes.ImmersionSessionState();
        state.ToggleManually();
        assert(state.IsEnabled && !state.IsOwnedByAutomaticPlayback && !state.EndAutomatic(),
            "全沉浸生命周期", "自动进入关闭时，手动沉浸不会在停止播放时自动退出");

        state.ToggleManually();
        assert(!state.IsEnabled, "全沉浸生命周期", "快捷键可手动退出全沉浸");
        assert(state.StartAutomatic(true) && state.IsEnabled && state.IsOwnedByAutomaticPlayback,
            "全沉浸生命周期", "自动进入开启时可将实际播放会话标记为自动沉浸");
        state.ToggleManually();
        assert(!state.IsEnabled && state.IsOwnedByAutomaticPlayback,
            "全沉浸生命周期", "播放中手动退出不会丢失会话自动清理标记");
        assert(state.EndAutomatic() && !state.IsEnabled && !state.IsOwnedByAutomaticPlayback,
            "全沉浸生命周期", "暂停/结束清除自动沉浸并复位会话标记");

        assert(!state.StartAutomatic(false) && !state.EndAutomatic(),
            "全沉浸生命周期", "自动进入关闭时播放启动和结束不改写沉浸状态");
    }

    private static void Test_AutoShowcaseInputPlan(Action<bool, string, string> assert)
    {
        ChartNote Note(int key, int stringNo, long start, long duration, FeasibilityStatus status = FeasibilityStatus.Playable) =>
            new(Guid.NewGuid(), start, duration, 60, 100, 0, 0, 0, null, stringNo, key,
                new GuitarPosition(1, 1, 1), status);
        var first = Note(6, 1, 100_000, 10_000);
        var second = Note(1, 6, 125_000, 500_000);
        var skipped = Note(2, 5, 200_000, 100_000, FeasibilityStatus.Unplayable);
        var chart = new GuitarChart
        {
            ChartId = "test", SourceName = "test", Notes = new[] { first, second, skipped }, PositionEvents = Array.Empty<PositionEvent>(),
            Issues = Array.Empty<FeasibilityIssue>(), DurationUs = 1_000_000, SourceNoteCount = 3, PlayableNoteCount = 2,
            AtRiskNoteCount = 0, UnplayableNoteCount = 1
        };
        var events = AutoShowcaseInputPlan.Create(chart);
        assert(events.Count == 4, "锁屏可打", "计划只为具备有效游戏键和位置的可演奏音符创建按下与释放事件");
        assert(events[0].ScanCode == MidiAutoPlayer.Guitar.GameScanCodes.Digit6 && events[0].IsDown,
            "锁屏可打", "物理一弦映射到游戏六键扫描码");
        assert(events[1].TimeUs == 125_000 && !events[1].IsDown && events[2].TimeUs == 125_000 && events[2].IsDown,
            "锁屏可打", "短音符至少按下 25ms，且同一时刻先释放再按下");
        assert(events[3].TimeUs == 375_000 && !events[3].IsDown,
            "锁屏可打", "长音符按住时长限制为 250ms 并保证生成释放事件");
        var customEvents = AutoShowcaseInputPlan.Create(chart, new ushort[] { 0x21, 0x22, 0x23, 0x24, 0x25, 0x26 });
        assert(customEvents[0].ScanCode == 0x21 && customEvents[2].ScanCode == 0x26,
            "锁屏可打", "自动演奏遵循用户为高低音弦配置的实际扫描码，而不是强制使用默认键位");
        var customLaneKeys = new[] { new PhysicalKeyBinding(0x41, 0x1E), new PhysicalKeyBinding(0x32, 0x03),
            new PhysicalKeyBinding(0x33, 0x04), new PhysicalKeyBinding(0x34, 0x05), new PhysicalKeyBinding(0x35, 0x06),
            new PhysicalKeyBinding(0x4A, 0x24) };
        var independentEvents = AutoShowcaseInputPlan.Create(chart, new ushort[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36 }, customLaneKeys);
        assert(independentEvents[0].ScanCode == 0x31 && independentEvents[2].ScanCode == 0x36,
            "锁屏可打", "自动演奏输出使用游戏琴弦校准键位，不受悬浮窗监听键绑定影响");
    }

    private static void Test_PositionSwitchKeyBinding_RejectsReservedKeys(Action<bool, string, string> assert)
    {
        var type = typeof(SemiAutoAcceptanceTests).Assembly.GetType("Nte.GuitarChart.Core.PositionSwitchKeyBinding");
        assert(type != null, "换位键设置", "存在可复用的按键绑定验证服务");
        if (type == null) return;
        var validate = type.GetMethod("IsAllowedVirtualKey");
        assert(validate != null, "换位键设置", "验证服务公开虚拟键码安全校验");
        if (validate == null) return;
        bool Allowed(int key) => (bool)(validate.Invoke(null, new object[] { key }) ?? false);
        assert(new MidiAutoPlayer.Models.AppConfig().SemiAutoPositionSwitchVk == 0x4A,
            "七轨键位", "新配置默认使用 1～6 + J 七轨键位");
        assert(Allowed(0x4A), "七轨键位", "J 键允许作为第七轨默认换位键");
        assert(!Allowed(0x1B) && !Allowed(0x5B) && !Allowed(0x5C) && !Allowed(0xA2) && !Allowed(0xA5),
            "七轨键位", "Escape、左右 Windows 与 Ctrl/Alt 修饰键不能作为轨道键位");
        assert(!Allowed(0x31) && !Allowed(0x36) && !Allowed(0x61) && !Allowed(0x66), "换位键设置", "主键盘与小键盘数字 1～6 禁止作为换位键");
        assert(!Allowed(0x52) && !Allowed(0x54), "换位键设置", "R/T 翻页键禁止作为换位键");
        assert(!Allowed(0x77) && !Allowed(0x78), "换位键设置", "F8 急停键与 F9 悬浮窗快捷键禁止作为换位键");
        assert(!Allowed(0x00) && !Allowed(0xFF), "换位键设置", "无效虚拟键码禁止绑定");
        assert(!Nte.GuitarChart.Core.PositionSwitchKeyBinding.IsAllowedPhysicalKey(0x23, 0x4F), "换位键设置", "NumLock 关闭时仍识别并禁止小键盘数字 1 的扫描码冲突");
    }

    private static void Test_InputObserver_GameKeyMapping(Action<bool, string, string> assert)
    {
        assert(NativeInputObserver.MapVirtualKeyToGameLane(0x31, 0x02) == 1
            && NativeInputObserver.MapVirtualKeyToGameLane(0x36, 0x07) == 6,
            "半自动按键高亮", "主键盘数字 1～6 映射到对应游戏轨道");
        assert(NativeInputObserver.MapVirtualKeyToGameLane(0x61, 0x4F) == 1
            && NativeInputObserver.MapVirtualKeyToGameLane(0x66, 0x4D) == 6,
            "半自动按键高亮", "小键盘数字 1～6 映射到对应游戏轨道");
        assert(NativeInputObserver.MapVirtualKeyToGameLane(0x00, 0x4F) == 1
            && NativeInputObserver.MapVirtualKeyToGameLane(0x00, 0x4D) == 6,
            "半自动按键高亮", "扫描码映射在键盘布局不提供虚拟键码时仍可高亮");
        assert(NativeInputObserver.MapVirtualKeyToGameLane(0x52, 0x13) == 0,
            "半自动按键高亮", "非游戏按键不会点亮游戏轨道");
    }

    private static void Test_GameLaneKeyBinding_DefaultsAndPhysicalMapping(Action<bool, string, string> assert)
    {
        var defaults = GameLaneKeyBinding.CreateDefaultGameLanes();
        assert(defaults.Count == 6 && defaults.Select(k => k.VirtualKey).SequenceEqual(new[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36 }),
            "七轨键位", "前六轨默认键位为主键盘数字 1～6");
        assert(defaults.Select((key, i) => GameLaneKeyBinding.MapPhysicalKeyToGameLane(key.VirtualKey, key.ScanCode, defaults) == i + 1).All(x => x),
            "七轨键位", "默认实体键映射按轨道顺序返回 1～6");
        assert(GameLaneKeyBinding.MapPhysicalKeyToGameLane(0, defaults[3].ScanCode, defaults) == 4
            && GameLaneKeyBinding.MapPhysicalKeyToGameLane(defaults[4].VirtualKey, 0, defaults) == 5,
            "七轨键位", "实体扫描码优先匹配，扫描码缺失时回退虚拟键码");
        assert(GameLaneKeyBinding.IsAllowedPhysicalKey(0x31, 0x02)
            && GameLaneKeyBinding.IsAllowedPhysicalKey(0x4A, 0x24)
            && !GameLaneKeyBinding.IsAllowedPhysicalKey(0x77, 0x41),
            "七轨键位", "允许默认数字键和 J，但保留 F8 全局快捷键");
        assert(GameLaneKeyBinding.AreUnique(defaults)
            && !GameLaneKeyBinding.AreUnique(new[] { defaults[0], new PhysicalKeyBinding(0x41, 0x02) }),
            "七轨键位", "不同实体按键允许绑定，同扫描码的重复绑定拒绝");
        assert(GameLaneKeyBinding.FormatLabel(0x4A, 0x24) == "J",
            "七轨键位", "键位标签可格式化为大写字母");
        var custom = defaults.Select(k => new PhysicalKeyBinding(k.VirtualKey, k.ScanCode)).ToList();
        custom[0] = new PhysicalKeyBinding(0x41, 0x1E);
        using (var observer = new NativeInputObserver())
        {
            observer.SetGameLaneBindings(custom);
            assert(NativeInputObserver.MapConfiguredKeyToGameLane(0x41, 0x1E, observer.GameLaneBindings) == 1
                && NativeInputObserver.MapConfiguredKeyToGameLane(0x31, 0x02, observer.GameLaneBindings) == 0,
                "七轨键位", "输入监听只识别当前自定义实体键，不保留旧默认键别名");

            int captureLane = 0;
            int normalInputs = 0;
            using var capturedEvent = new ManualResetEventSlim();
            observer.LaneKeyCaptured += (lane, _, _) => { captureLane = lane; capturedEvent.Set(); };
            observer.InputObserved += (_, _) => Interlocked.Increment(ref normalInputs);
            observer.BeginLaneKeyCapture(1);
            var hookType = typeof(NativeInputObserver).GetNestedType("KBDLLHOOKSTRUCT", System.Reflection.BindingFlags.NonPublic)!;
            var keyInfo = Activator.CreateInstance(hookType)!;
            hookType.GetField("vkCode")!.SetValue(keyInfo, (uint)0x41);
            hookType.GetField("scanCode")!.SetValue(keyInfo, (uint)0x1E);
            var keyInfoPtr = System.Runtime.InteropServices.Marshal.AllocHGlobal(System.Runtime.InteropServices.Marshal.SizeOf(hookType));
            try
            {
                System.Runtime.InteropServices.Marshal.StructureToPtr(keyInfo, keyInfoPtr, false);
                var hook = typeof(NativeInputObserver).GetMethod("HookCallback", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                var keyDown = (IntPtr)hook.Invoke(observer, new object[] { 0, (IntPtr)0x0100, keyInfoPtr })!;
                bool captured = capturedEvent.Wait(1000) && captureLane == 1;
                var keyUp = (IntPtr)hook.Invoke(observer, new object[] { 0, (IntPtr)0x0101, keyInfoPtr })!;
                assert(captured && keyDown == (IntPtr)1 && keyUp == (IntPtr)1,
                    "七轨键位", "捕获时按键上下沿被全局钩子拦截并返回目标轨道");
                assert(Volatile.Read(ref normalInputs) == 0,
                    "七轨键位", "录入键不会进入音游按键判定和高亮输入事件");
            }
            finally { System.Runtime.InteropServices.Marshal.FreeHGlobal(keyInfoPtr); }
        }
        var legacy = new MidiAutoPlayer.Models.AppConfig { SemiAutoPositionSwitchVk = 0x20 };
        GameLaneKeyBinding.NormalizeConfig(legacy);
        assert(legacy.SemiAutoGameLaneKeyBindings?.Count == 6 && legacy.SemiAutoPositionSwitchVk == 0x4A,
            "七轨键位", "旧配置自动迁移为 1～6 + J");
        legacy.SemiAutoGameLaneKeyBindings![0] = new PhysicalKeyBinding(0x41, 0x1E);
        var saved = System.Text.Json.JsonSerializer.Deserialize<MidiAutoPlayer.Models.AppConfig>(System.Text.Json.JsonSerializer.Serialize(legacy));
        assert(saved?.SemiAutoGameLaneKeyBindings?[0].VirtualKey == 0x41
            && saved.SemiAutoPositionSwitchVk == 0x4A,
            "七轨键位", "自定义轨道键位可通过应用 JSON 配置往返保存");
        var invalid = new MidiAutoPlayer.Models.AppConfig
        {
            SemiAutoGameLaneKeyBindings = new[]
            {
                new PhysicalKeyBinding(0x41, 0x1E), new PhysicalKeyBinding(0x42, 0x1E),
                defaults[2], defaults[3], defaults[4], defaults[5]
            }.ToList(),
            SemiAutoPositionSwitchVk = 0x4A,
            SemiAutoPositionSwitchScanCode = 0x24
        };
        assert(GameLaneKeyBinding.NormalizeConfig(invalid)
            && invalid.SemiAutoGameLaneKeyBindings?.Select(k => k.VirtualKey).SequenceEqual(new[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36 }) == true,
            "七轨键位", "旧配置中的重复或损坏映射安全恢复为默认键位");
    }

    private static void Test_LaneKeyReset_PersistsDefaults(Action<bool, string, string> assert)
    {
        var defaults = GameLaneKeyBinding.CreateDefaultGameLanes();
        var config = new MidiAutoPlayer.Models.AppConfig
        {
            SemiAutoGameLaneKeyBindings = defaults.Select(k => new PhysicalKeyBinding(k.VirtualKey, k.ScanCode)).ToList(),
            SemiAutoPositionSwitchVk = 0x4A,
            SemiAutoPositionSwitchScanCode = 0x24
        };
        config.SemiAutoGameLaneKeyBindings[0] = new PhysicalKeyBinding(0x41, 0x1E);
        int saves = 0;
        using var overlay = new OverlayWindow();
        using var controller = new SemiAutoController(overlay, () => null, config: config, saveConfig: () => saves++);
        controller.ResetLaneKeyBindingsAsync().GetAwaiter().GetResult();
        assert(saves == 1, "七轨键位", "全轨恢复默认后调用一次配置保存接口");
        assert(config.SemiAutoGameLaneKeyBindings!.Select(k => k.VirtualKey).SequenceEqual(new[] { 0x31, 0x32, 0x33, 0x34, 0x35, 0x36 })
            && config.SemiAutoPositionSwitchVk == 0x4A && config.SemiAutoPositionSwitchScanCode == 0x24,
            "七轨键位", "恢复默认同步保存前六轨数字键和第七轨 J 键");
    }

    private static void Test_Compiler_EmptyMidi(Action<bool, string, string> assert)
    {
        var compiler = new GuitarChartCompiler();
        var chart = compiler.Compile(Array.Empty<RawMidiNote>());

        assert(chart != null, "GuitarChartCompiler", "空 MIDI 不崩溃且生成非空谱面对象");
        assert(chart?.Notes.Count == 0, "GuitarChartCompiler", "空 MIDI 生成音符数为 0");
        assert(chart?.PositionEvents.Count == 0, "GuitarChartCompiler", "空 MIDI 位置事件数为 0");
        assert(chart?.DurationUs == 0, "GuitarChartCompiler", "空 MIDI 谱面总时长为 0");
        assert(chart?.PlayabilityRate == 1.0, "GuitarChartCompiler", "空 MIDI 谱面可演奏率默认规范为 100%");
    }

    private static void Test_Compiler_FingeringCandidates_E4_E2(Action<bool, string, string> assert)
    {
        var compiler = new GuitarChartCompiler();

        // E4 is MIDI 64, open string 1. Game key is 7 - 1 = 6.
        var e4Note = new RawMidiNote(0, 500_000, 64, 100, 0, 24, 0, "Guitar");
        var chartE4 = compiler.Compile(new[] { e4Note });
        var noteE4 = chartE4.Notes.FirstOrDefault();

        assert(noteE4 != null, "GuitarChartCompiler", "E4 音符成功编译");
        assert(noteE4?.PhysicalStringNo == 1, "GuitarChartCompiler", "E4(64) 准确分配为物理 1 弦");
        assert(noteE4?.GameKey == 6, "GuitarChartCompiler", "物理 1 弦准确映射为游戏按键 6 (GameKey = 7 - 1)");
        assert(noteE4?.Position?.Page == 5 && noteE4?.Position?.Slot == 1, "GuitarChartCompiler", "0品映射到 P5 Slot 1");

        // E2 is MIDI 40, open string 6. Game key is 7 - 6 = 1.
        var e2Note = new RawMidiNote(0, 500_000, 40, 100, 0, 24, 0, "Guitar");
        var chartE2 = compiler.Compile(new[] { e2Note });
        var noteE2 = chartE2.Notes.FirstOrDefault();

        assert(noteE2 != null, "GuitarChartCompiler", "E2 音符成功编译");
        assert(noteE2?.PhysicalStringNo == 6, "GuitarChartCompiler", "E2(40) 准确分配为物理 6 弦");
        assert(noteE2?.GameKey == 1, "GuitarChartCompiler", "物理 6 弦准确映射为游戏按键 1 (GameKey = 7 - 6)");
        assert(noteE2?.Position?.Page == 5 && noteE2?.Position?.Slot == 1, "GuitarChartCompiler", "E2 0品映射到 P5 Slot 1");
    }

    private static void Test_Compiler_SimultaneousNotesShareOnePosition(Action<bool, string, string> assert)
    {
        var compiler = new GuitarChartCompiler();
        var chart = compiler.Compile(new[]
        {
            new RawMidiNote(0, 150_000, 64, 100, 0, 24, 0, "Guitar"),
            new RawMidiNote(5_000, 150_000, 65, 100, 0, 24, 0, "Guitar")
        }, new ChartCompileOptions { SequentializeSimultaneousNotes = true });
        assert(chart.Notes.All(note => note.Status == FeasibilityStatus.Unplayable),
            "GuitarChartCompiler", "同刻音符不能拆到不同轮盘位置并伪装为可演奏；无共同品位时整体标为不可演奏");
        assert(chart.PositionEvents.Count == 0,
            "GuitarChartCompiler", "无法共用轮盘位置的同刻音符不会生成同一时间互相冲突的自动换位事件");
    }

    private static void Test_Compiler_FretToPageSlotMapping(Action<bool, string, string> assert)
    {
        var p0 = GuitarPosition.FromFret(0);
        assert(p0.Page == 5 && p0.Slot == 1 && p0.Fret == 0, "GuitarChartCompiler", "fret=0 -> P5/Slot 1");

        var p1Min = GuitarPosition.FromFret(1);
        var p1Max = GuitarPosition.FromFret(6);
        assert(p1Min.Page == 1 && p1Min.Slot == 1, "GuitarChartCompiler", "fret=1 -> P1/Slot 1");
        assert(p1Max.Page == 1 && p1Max.Slot == 6, "GuitarChartCompiler", "fret=6 -> P1/Slot 6");

        var p2Min = GuitarPosition.FromFret(7);
        var p2Max = GuitarPosition.FromFret(12);
        assert(p2Min.Page == 2 && p2Min.Slot == 1, "GuitarChartCompiler", "fret=7 -> P2/Slot 1");
        assert(p2Max.Page == 2 && p2Max.Slot == 6, "GuitarChartCompiler", "fret=12 -> P2/Slot 6");

        var p3Min = GuitarPosition.FromFret(13);
        var p3Max = GuitarPosition.FromFret(18);
        assert(p3Min.Page == 3 && p3Min.Slot == 1, "GuitarChartCompiler", "fret=13 -> P3/Slot 1");
        assert(p3Max.Page == 3 && p3Max.Slot == 6, "GuitarChartCompiler", "fret=18 -> P3/Slot 6");

        var p4Min = GuitarPosition.FromFret(19);
        var p4Max = GuitarPosition.FromFret(24);
        assert(p4Min.Page == 4 && p4Min.Slot == 1, "GuitarChartCompiler", "fret=19 -> P4/Slot 1");
        assert(p4Max.Page == 4 && p4Max.Slot == 6, "GuitarChartCompiler", "fret=24 -> P4/Slot 6");
    }

    private static void Test_Compiler_ConcurrentNotesConstraint_And_NoCommonFret(Action<bool, string, string> assert)
    {
        var compiler = new GuitarChartCompiler();

        // 1. Valid concurrent chord: C4(60) on string 2 (59+1) and F4(65) on string 1 (64+1) - both fret 1!
        var validChord = new[]
        {
            new RawMidiNote(0, 500_000, 60, 100, 0, 24, 0),
            new RawMidiNote(0, 500_000, 65, 100, 0, 24, 0)
        };
        var chartValid = compiler.Compile(validChord);
        assert(chartValid.Notes.All(n => n.Status == FeasibilityStatus.Playable), "GuitarChartCompiler", "共品并发和弦全部标记为 Playable");
        assert(chartValid.Notes.Select(n => n.PhysicalStringNo).Distinct().Count() == 2, "GuitarChartCompiler", "并发音符分配到不同物理琴弦");

        // 2. Impossible concurrent chord: E2(40, requires fret 0) and D#6(87, requires fret 23)
        // No common fret between 0 and 24 can play both simultaneously!
        var impossibleChord = new[]
        {
            new RawMidiNote(100_000, 500_000, 40, 100, 0, 24, 0),
            new RawMidiNote(100_000, 500_000, 87, 100, 0, 24, 0)
        };
        var chartImpossible = compiler.Compile(impossibleChord);

        assert(chartImpossible.Notes.Count == 2, "GuitarChartCompiler", "不可演奏并发音符绝不静默删除，全部保留在谱面");
        assert(chartImpossible.Notes.All(n => n.Status == FeasibilityStatus.Unplayable), "GuitarChartCompiler", "冲突并发音符标记为 Unplayable");
        assert(chartImpossible.Issues.Any(i => i.Code == "NO_COMMON_FRET"), "GuitarChartCompiler", "输出明确的 NO_COMMON_FRET 诊断报告");
    }

    private static void Test_Compiler_NoSilentDeletion_OnTimingRisk(Action<bool, string, string> assert)
    {
        var compiler = new GuitarChartCompiler(new WheelPositionTimingModel());

        // Rapid position jump: fret 1 (P1) at 0ms, then fret 24 (P4) at 50ms (50,000 us).
        // 50ms is separate onset (>20ms), but way below the ~100ms+ required for P1->P4 + wheel settle.
        var rapidNotes = new[]
        {
            new RawMidiNote(0, 30_000, 65, 100, 0, 24, 0),          // 1 string 1 fret (P1)
            new RawMidiNote(50_000, 500_000, 88, 100, 0, 24, 0)     // 1 string 24 fret (P4)
        };

        var chart = compiler.Compile(rapidNotes);
        assert(chart.Notes.Count == 2, "GuitarChartCompiler", "准备时间不足时绝不删除音符");
        assert(chart.Notes.Any(n => n.Status == FeasibilityStatus.AtRisk), "GuitarChartCompiler", "时间紧迫事件正确标记为 AtRisk");
        assert(chart.Issues.Any(i => i.Code == "POSITION_PREPARE_TIME"), "GuitarChartCompiler", "输出清晰的时序风险 POSITION_PREPARE_TIME 警告");
    }

    private static void Test_Compiler_OpeningPrePosition(Action<bool, string, string> assert)
    {
        var compiler = new GuitarChartCompiler();

        // Single note starting at P3 (fret 13)
        var singleNoteP3 = new[]
        {
            new RawMidiNote(1_000_000, 500_000, 77, 100, 0, 24, 0) // 1 string 13 fret (P3 S1)
        };
        var chart = compiler.Compile(singleNoteP3);

        assert(chart.PositionEvents.Count == 0, "GuitarChartCompiler", "首个位置仅在开局预备阶段完成预定位，不生成正式位置切换事件");
    }

    private static void Test_Compiler_NoteTraceability(Action<bool, string, string> assert)
    {
        var raw = new RawMidiNote(250_000, 480_000, 64, 95, 2, 25, 3, "GuitarSolo");
        var compiler = new GuitarChartCompiler();
        var chart = compiler.Compile(new[] { raw });
        var note = chart.Notes.First();

        assert(note.StartUs == 250_000, "GuitarChartCompiler", "源音符 StartUs 完美保留");
        assert(note.DurationUs == 480_000, "GuitarChartCompiler", "源音符 DurationUs 完美保留");
        assert(note.NoteNumber == 64, "GuitarChartCompiler", "源音符 NoteNumber 完美保留");
        assert(note.Velocity == 95, "GuitarChartCompiler", "源音符 Velocity 完美保留");
        assert(note.Channel == 2, "GuitarChartCompiler", "源音符 Channel 完美保留");
        assert(note.Program == 25, "GuitarChartCompiler", "源音符 Program 完美保留");
        assert(note.TrackIndex == 3, "GuitarChartCompiler", "源音符 TrackIndex 完美保留");
        assert(note.TrackName == "GuitarSolo", "GuitarChartCompiler", "源音符 TrackName 完美保留");
    }

    private static void Test_TimingModel_CalibratedWheelRules_And_P0(Action<Action<bool, string, string>>? dummy, Action<bool, string, string> assert)
    {
        Test_TimingModel_CalibratedWheelRules_And_P0(assert);
    }

    private static void Test_TimingModel_CalibratedWheelRules_And_P0(Action<bool, string, string> assert)
    {
        var timing = new WheelPositionTimingModel();
        var opts = new ChartCompileOptions
        {
            PageKeyHoldUs = 25_000,
            PageInterTapGapUs = 12_000,
            WheelMouseSettleUs = 55_000,
            SafetyMarginUs = 8_000
        };

        // 1. Same position
        var posP1S1 = new GuitarPosition(1, 1, 1);
        long tSame = timing.EstimatePrepareUs(posP1S1, posP1S1, opts);
        assert(tSame == 0, "WheelPositionTimingModel", "同页同槽所需准备耗时为 0us");

        // 2. Intra-page slot change (P1S1 -> P1S2)
        var posP1S2 = new GuitarPosition(1, 2, 2);
        long tSlot = timing.EstimatePrepareUs(posP1S1, posP1S2, opts);
        long expectedSlot = opts.WheelMouseSettleUs + opts.SafetyMarginUs;
        assert(tSlot == expectedSlot, "WheelPositionTimingModel", "同页换槽仅包含轮盘选槽稳定时间与安全余量");

        // 3. P1 -> P5 (four T taps to enter the open-string page)
        var posP0S1 = new GuitarPosition(5, 1, 0);
        long tP1toP0 = timing.EstimatePrepareUs(posP1S1, posP0S1, opts);
        long singlePageStep = opts.PageKeyHoldUs + opts.PageInterTapGapUs;
        long expectedP1toP0 = 4 * singlePageStep + opts.WheelMouseSettleUs + opts.SafetyMarginUs;
        assert(tP1toP0 == expectedP1toP0, "WheelPositionTimingModel", "P1 到 P5 导航按 4 次 T 翻页加选槽沉降计算");

        // 4. P5 -> P1 (four R taps)
        long tP0toP1 = timing.EstimatePrepareUs(posP0S1, posP1S1, opts);
        assert(tP0toP1 == expectedP1toP0, "WheelPositionTimingModel", "P5 到 P1 导航按 4 次 R 翻页加选槽沉降计算");

        // 5. P3 -> P5 (two T taps)
        var posP3S1 = new GuitarPosition(3, 1, 13);
        long tP3toP0 = timing.EstimatePrepareUs(posP3S1, posP0S1, opts);
        long expectedP3toP0 = 2 * singlePageStep + opts.WheelMouseSettleUs + opts.SafetyMarginUs;
        assert(tP3toP0 == expectedP3toP0, "WheelPositionTimingModel", "P3 到 P5 跨 2 步串行 T 翻页导航");

        // 6. P5 -> P3 (two R taps)
        long tP0toP3 = timing.EstimatePrepareUs(posP0S1, posP3S1, opts);
        assert(tP0toP3 == expectedP3toP0, "WheelPositionTimingModel", "P5 到 P3 跨 2 步串行 R 翻页导航");
    }

    private static void Test_PracticeTimeline_HoldPauseResumeAndSpeed(Action<bool, string, string> assert)
    {
        var timeline = new PracticeTimeline();
        timeline.Start(2_000_000, 10_000, 100);
        assert(timeline.CurrentTimeUs(1_010_000) == 3_000_000,
            "PracticeTimeline", "按住期间以 100% 速度推进原 MIDI 时间");

        timeline.Pause(1_010_000);
        assert(timeline.CurrentTimeUs(5_000_000) == 3_000_000,
            "PracticeTimeline", "松开驱动键后时间轴冻结");

        timeline.Resume(5_000_000);
        timeline.SetSpeed(200, 5_000_000);
        assert(timeline.CurrentTimeUs(5_250_000) == 3_500_000,
            "PracticeTimeline", "恢复后从冻结位置继续，并按 200% 速度推进");

        timeline.Pause(5_250_000);
        timeline.Resume(6_000_000);
        timeline.SetSpeed(50, 6_000_000);
        assert(timeline.CurrentTimeUs(6_400_000) == 3_700_000,
            "PracticeTimeline", "速度切换不跳时间，50% 按倍率推进");
    }

    private static void Test_PracticeSession_TailRecovery(Action<bool, string, string> assert)
    {
        var chart = new GuitarChartCompiler().Compile(new[]
        {
            new RawMidiNote(100_000, 200_000, 64, 100, 0, 24, 0, "Guitar")
        });
        var session = new PracticeSessionController(chart);
        long? retryTimeUs = session.GetTailRecoveryTimeUs(chart.DurationUs, chart.DurationUs);
        assert(retryTimeUs == 100_000,
            "PracticeSession", "长按越过曲尾时，将未命中的末尾音符恢复到可练时间点");

        var target = session.GetUpcomingTargets(0).Single();
        session.EvaluateInput(target.GameKey!.Value, target.StartUs);
        assert(session.GetTailRecoveryTimeUs(chart.DurationUs, chart.DurationUs) == null,
            "PracticeSession", "已完成全部目标时曲尾不再回退或锁住练习状态");
    }

    private static void Test_ModeIsolation_PracticeUsesChartTiming(Action<bool, string, string> assert)
    {
        var chart = new GuitarChartCompiler().Compile(new[]
        {
            new RawMidiNote(0, 180_000, 64, 100, 0, 24, 0, "Guitar"),
            new RawMidiNote(10_000, 180_000, 59, 100, 0, 24, 0, "Guitar"),
            new RawMidiNote(250_000, 180_000, 65, 100, 0, 24, 0, "Guitar")
        });
        var session = new PracticeSessionController(chart);
        var firstGroup = session.GetTargetsAt(5_000, 80_000);
        assert(firstGroup.Count == 2 && Math.Abs(firstGroup[0].StartUs - firstGroup[1].StartUs) <= 20_000,
            "PracticeSession", "同一 onset 容差内的和弦目标同时有效，不被串行为连续拍");

        var early = session.EvaluateInput(firstGroup[0].GameKey!.Value, 200_000, 20_000);
        assert(early.Kind == "early" && session.JudgedCount == 0,
            "PracticeSession", "时间窗之外不能提前命中未来目标");

        var correct = session.EvaluateInput(firstGroup[0].GameKey!.Value, 5_000, 80_000);
        assert(correct.Kind == "correct" && session.JudgedCount == 1
            && session.GetTargetsAt(5_000, 80_000).Count == 1,
            "PracticeSession", "和弦成员分别判定，命中不删除谱面事件或改变源时间");
        assert(!session.Engine.Rules.ContinueTimelineOnWrongInput,
            "PracticeSession", "按错反馈不修改由长按控制的练习时间轴");
    }

    private static void Test_ModeIsolation_PracticeFeedback(Action<bool, string, string> assert)
    {
        var practice = new PracticeEngine();
        var correct = practice.EvaluateKey(1, 1, Guid.NewGuid());
        var wrong = practice.EvaluateKey(2, 5, Guid.NewGuid());

        assert(correct.Kind == "correct", "PracticeEngine", "正确按键仍返回练习命中反馈");
        assert(wrong.Kind == "wrong", "PracticeEngine", "错误按键仍返回练习错误反馈");
    }

    private static void Test_HitJudgement_FeedbackGrades(Action<bool, string, string> assert)
    {
        assert(HitJudgement.Judge(0, 80_000, 160_000) == HitGrade.Perfect,
            "HitJudgement", "命中时间内仍产生 Perfect 反馈等级");
        assert(HitJudgement.Judge(100_000, 80_000, 160_000) == HitGrade.Good,
            "HitJudgement", "较宽判定窗内仍产生 Good 反馈等级");
        assert(HitJudgement.Judge(200_000, 80_000, 160_000) == HitGrade.Miss,
            "HitJudgement", "超出判定窗仍产生 Miss 反馈等级");
    }

    private static void Test_OverlayWindow_Properties_And_ClickThrough(Action<bool, string, string> assert)
    {
        using var window = new OverlayWindow();

        assert(window.TopMost, "OverlayWindow", "悬浮窗保持 TopMost 置顶");
        assert(window.FormBorderStyle == FormBorderStyle.None, "OverlayWindow", "悬浮窗为无边框样式 FormBorderStyle.None");
        assert(!window.ShowInTaskbar, "OverlayWindow", "悬浮窗不占用系统任务栏");
        var workArea = Screen.PrimaryScreen!.WorkingArea;
        double dpiScale = Math.Max(1d, window.DeviceDpi / 96d);
        assert(window.Width >= 380 * dpiScale && window.Width <= 500 * dpiScale && window.Height >= workArea.Height * 0.80 && window.Width < window.Height,
            "OverlayWindow", "悬浮窗默认采用 380～500px 窄长条宽度并占据工作区 80% 以上高度");
        assert(window.MinimumSize.Width == window.MaximumSize.Width && window.Width == window.MinimumSize.Width,
            "OverlayWindow", "悬浮窗宽度被原生窗体刚性锁定，不能拖宽");
        assert(workArea.Contains(window.Bounds), "OverlayWindow", "悬浮窗初始 bounds 完整位于当前显示器工作区内");

        int portraitWidth = window.Width;
        window.SetWideLayout(true);
        var wideWorkArea = Screen.FromRectangle(window.Bounds).WorkingArea;
        assert(window.IsWideLayout && window.Width > portraitWidth && window.Width <= wideWorkArea.Width && window.Height < wideWorkArea.Height * .80,
            "OverlayWindow", "宽版窗口变宽变矮并完整适配当前显示器工作区");
        window.SetWideLayout(false);
        assert(!window.IsWideLayout && window.Width <= 500 * dpiScale && window.Width < window.Height,
            "OverlayWindow", "宽版可切回窄竖版并恢复竖版比例");

        // Verify SetClickThrough does not crash or invalidate window
        window.SetClickThrough(true);
        window.SetClickThrough(false);
        assert(window.WebView != null, "OverlayWindow", "WebView2 控件正常宿主且生命周期完整");

        window.SetOverlayOpacity(84);
        window.SetImmersive(true);
        assert(window.IsImmersive && Math.Abs(window.Opacity - 1d) < 0.001,
            "OverlayWindow", "全沉浸状态将宿主窗体切换为完全不透明层以显示透明网页内容");
        window.SetClickThrough(false);
        window.SetImmersive(false);
        assert(!window.IsImmersive && Math.Abs(window.Opacity - 0.84d) < 0.001,
            "OverlayWindow", "退出全沉浸恢复原透明度和可交互状态");
    }

    private static void Test_Bridge_MessageContract_And_Validation(Action<bool, string, string> assert)
    {
        var serviceMock = new MockSemiAutoAppService();
        var dispatcher = new BridgeDispatcher(serviceMock);

        // Test mode.change
        var docMode = System.Text.Json.JsonDocument.Parse("{\"mode\":\"rhythm-hell\"}");
        dispatcher.DispatchAsync("mode.change", docMode.RootElement);
        assert(serviceMock.LastMode == PlayMode.RhythmHell, "BridgeDispatcher", "准确分发 mode.change 到 RhythmHell");

        bool showcaseModeAccepted = false;
        try
        {
            var docShowcase = System.Text.Json.JsonDocument.Parse("{\"mode\":\"auto-showcase\"}");
            dispatcher.DispatchAsync("mode.change", docShowcase.RootElement);
            showcaseModeAccepted = serviceMock.LastMode.ToString() == "AutoShowcase";
        }
        catch (ArgumentException)
        {
            // The assertion below reports an unsupported mode as a regression.
        }
        assert(showcaseModeAccepted, "BridgeDispatcher", "auto-showcase 模式可通过 WebView2 桥接到控制器");

        // Test speed.change
        var docSpeed = System.Text.Json.JsonDocument.Parse("{\"speedPercent\":125}");
        dispatcher.DispatchAsync("speed.change", docSpeed.RootElement);
        assert(serviceMock.LastSpeed == 125, "BridgeDispatcher", "准确分发 speed.change 到 125%");

        dispatcher.DispatchAsync("window.opacity", System.Text.Json.JsonDocument.Parse("{\"percent\":84}").RootElement);
        assert(serviceMock.LastOpacity == 84, "BridgeDispatcher", "透明度设置从 WebView2 正确桥接至原生窗口");
        dispatcher.DispatchAsync("theme.change", System.Text.Json.JsonDocument.Parse("{\"theme\":\"light\"}").RootElement);
        assert(serviceMock.LastTheme == "light", "BridgeDispatcher", "主题切换按钮正确桥接至 WinForms 控制器");
        dispatcher.DispatchAsync("library.guitar.list", System.Text.Json.JsonDocument.Parse("{\"query\":\"小星星\"}").RootElement);
        assert(serviceMock.LastLibraryQuery == "小星星", "BridgeDispatcher", "曲库搜索请求携带查询词并桥接至吉他曲库");
        dispatcher.DispatchAsync("library.guitar.select", System.Text.Json.JsonDocument.Parse("{\"songId\":\"guitar-song-1\"}").RootElement);
        assert(serviceMock.LastSelectedSongId == "guitar-song-1", "BridgeDispatcher", "曲库选曲通过歌曲 ID 桥接至当前歌曲加载流程");
        dispatcher.DispatchAsync("window.layout", System.Text.Json.JsonDocument.Parse("{\"layout\":\"wide\"}").RootElement);
        assert(serviceMock.LastWideLayout, "BridgeDispatcher", "window.layout=wide 正确切换到宽版");
        dispatcher.DispatchAsync("immersion.auto", System.Text.Json.JsonDocument.Parse("{\"enabled\":true}").RootElement);
        assert(serviceMock.AutoImmersionEnabled, "BridgeDispatcher", "全沉浸自动进入开关正确传入控制器");
        dispatcher.DispatchAsync("immersion.hotkey.set", System.Text.Json.JsonDocument.Parse("{\"virtualKey\":79}").RootElement);
        assert(serviceMock.LastImmersionHotkeyVk == 79, "BridgeDispatcher", "全沉浸单键设置正确传入 virtual-key");
        bool invalidImmersionKeyRejected = false;
        try { dispatcher.DispatchAsync("immersion.hotkey.set", System.Text.Json.JsonDocument.Parse("{\"virtualKey\":8}").RootElement); }
        catch (ArgumentOutOfRangeException) { invalidImmersionKeyRejected = true; }
        assert(invalidImmersionKeyRejected, "BridgeDispatcher", "未支持的控制键不能设置为沉浸快捷键");
        bool threwLayout = false;
        try { dispatcher.DispatchAsync("window.layout", System.Text.Json.JsonDocument.Parse("{\"layout\":\"landscape\"}").RootElement); }
        catch (ArgumentException) { threwLayout = true; }
        assert(threwLayout, "BridgeDispatcher", "未知窗口布局被拒绝");
        dispatcher.DispatchAsync("position-key.capture", default);
        assert(serviceMock.CaptureRequested, "BridgeDispatcher", "换位键录入请求正确桥接至全局输入监听");
        dispatcher.DispatchAsync("lane-key.capture", System.Text.Json.JsonDocument.Parse("{\"lane\":4}").RootElement);
        assert(serviceMock.LastCapturedLane == 4, "七轨键位", "lane-key.capture 正确传递目标轨道");
        dispatcher.DispatchAsync("lane-key.reset", default);
        assert(serviceMock.LaneKeyResetRequested, "七轨键位", "lane-key.reset 正确桥接到全轨默认恢复");
        dispatcher.DispatchAsync("lane-key.cancel", default);
        assert(serviceMock.LaneKeyCancelRequested, "七轨键位", "lane-key.cancel 正确结束当前捕获");
        bool invalidLaneRejected = false;
        try { dispatcher.DispatchAsync("lane-key.capture", System.Text.Json.JsonDocument.Parse("{\"lane\":8}").RootElement); }
        catch (ArgumentOutOfRangeException) { invalidLaneRejected = true; }
        assert(invalidLaneRejected, "七轨键位", "轨道编号超出 1～7 时由 bridge 拒绝");
        dispatcher.DispatchAsync("play.reset", default);
        assert(serviceMock.ResetRequested, "BridgeDispatcher", "重置按钮正确桥接至会话重置接口");

        // Test invalid speed out of range
        bool threwSpeed = false;
        try
        {
            var docInvalidSpeed = System.Text.Json.JsonDocument.Parse("{\"speedPercent\":300}");
            dispatcher.DispatchAsync("speed.change", docInvalidSpeed.RootElement);
        }
        catch (ArgumentOutOfRangeException)
        {
            threwSpeed = true;
        }
        assert(threwSpeed, "BridgeDispatcher", "速度超出 25..200% 范围受到严格校验拦截");
    }

    private sealed class MockSemiAutoAppService : ISemiAutoAppService
    {
        public PlayMode LastMode { get; set; }
        public int LastSpeed { get; set; }
        public bool LastLocked { get; set; }
        public string LastTheme { get; set; } = "";
        public int LastOpacity { get; set; }
        public bool LastWideLayout { get; set; }
        public bool CaptureRequested { get; set; }
        public int LastCapturedLane { get; set; }
        public bool LaneKeyResetRequested { get; set; }
        public bool LaneKeyCancelRequested { get; set; }
        public bool ResetRequested { get; set; }
        public string LastLibraryQuery { get; set; } = "";
        public string LastSelectedSongId { get; set; } = "";
        public string LastRecommendedScoreId { get; set; } = "";
        public bool AutoImmersionEnabled { get; set; }
        public int LastImmersionHotkeyVk { get; set; }

        public Task ChangeModeAsync(PlayMode mode) { LastMode = mode; return Task.CompletedTask; }
        public Task SetExecutionModeAsync(PositionExecutionMode mode) => Task.CompletedTask;
        public Task StartAsync(int? initialPage = null) => Task.CompletedTask;
        public Task PauseAsync() => Task.CompletedTask;
        public Task ResetAsync() { ResetRequested = true; return Task.CompletedTask; }
        public Task SeekAsync(double ratio) => Task.CompletedTask;
        public Task SetSpeedAsync(int speedPercent) { LastSpeed = speedPercent; return Task.CompletedTask; }
        public Task ResynchronizeAsync() => Task.CompletedTask;
        public Task HideOverlayAsync() => Task.CompletedTask;
        public Task SetWindowLockedAsync(bool locked) { LastLocked = locked; return Task.CompletedTask; }
        public Task SetThemeAsync(string theme) { LastTheme = theme; return Task.CompletedTask; }
        public Task BeginPositionSwitchKeyCaptureAsync() { CaptureRequested = true; return Task.CompletedTask; }
        public Task BeginLaneKeyCaptureAsync(int lane) { LastCapturedLane = lane; return Task.CompletedTask; }
        public Task CancelLaneKeyCaptureAsync() { LaneKeyCancelRequested = true; return Task.CompletedTask; }
        public Task ResetLaneKeyBindingsAsync() { LaneKeyResetRequested = true; return Task.CompletedTask; }
        public Task ResetPositionSwitchKeyAsync() => Task.CompletedTask;
        public Task SetOverlayOpacityAsync(int percent) { LastOpacity = percent; return Task.CompletedTask; }
        public Task SetOverlayWideLayoutAsync(bool wide) { LastWideLayout = wide; return Task.CompletedTask; }
        public Task SetAutoImmersionAsync(bool enabled) { AutoImmersionEnabled = enabled; return Task.CompletedTask; }
        public Task BeginImmersionHotkeyCaptureAsync() => Task.CompletedTask;
        public Task CancelImmersionHotkeyCaptureAsync() => Task.CompletedTask;
        public Task SetImmersionHotkeyAsync(int virtualKey) { LastImmersionHotkeyVk = virtualKey; return Task.CompletedTask; }
        public Task SetManualPracticePositionAsync(bool enabled) => Task.CompletedTask;
        public Task ListGuitarSongsAsync(string query) { LastLibraryQuery = query; return Task.CompletedTask; }
        public Task SelectGuitarSongAsync(string songId) { LastSelectedSongId = songId; return Task.CompletedTask; }
        public Task ListRecommendedGuitarScoresAsync(string query) { LastLibraryQuery = query; return Task.CompletedTask; }
        public Task SelectRecommendedGuitarScoreAsync(string scoreId) { LastRecommendedScoreId = scoreId; return Task.CompletedTask; }
        public Task BeginWindowDragAsync() => Task.CompletedTask;
        public Task WebReadyAsync() => Task.CompletedTask;
    }
}
