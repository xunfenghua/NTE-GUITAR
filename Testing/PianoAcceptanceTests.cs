using MidiAutoPlayer.Models;
using MidiAutoPlayer.Piano.Compiler;
using MidiAutoPlayer.Piano.Mapping;
using MidiAutoPlayer.Piano.Models;
using MidiAutoPlayer.Piano.Services;
using MidiAutoPlayer.Bridge;
using System.Text.Json;
using MidiAutoPlayer.UI;

namespace MidiAutoPlayer.Testing;

public static class PianoAcceptanceTests
{
    public static void RunAll(Action<bool, string, string> assert)
    {
        SevenKeyScaleMapsBelowTonicWithFloorArithmetic(assert);
        SevenKeyRejectsChromaticPitch(assert);
        ChangingScaleChangesMapping(assert);
        TwelveKeyPrefersDirectBinding(assert);
        AbsoluteTimeIsScaledOnce(assert);
        OverlappingSameKeyNotesReleaseAfterLastEnd(assert);
        TransposeOverflowIsReported(assert);
        UncalibratedDefaultsBlockPlayback(assert);
        DrumChannelIsFiltered(assert);
        ModifierScanCollisionInvalidatesProfile(assert);
        SkipPolicyRequiresAcknowledgement(assert);
        StartWaitsForGameFocusAndCanBeCancelled(assert);
        HotkeyRoutesToSelectedOrActivePiano(assert);
    }

    private static void SevenKeyScaleMapsBelowTonicWithFloorArithmetic(Action<bool, string, string> assert)
    {
        var result = PianoPitchMapper.TryMapSevenKey(59, SevenKeyProfile(60, "C Major"));
        bool passed = result.IsMapped &&
                      result.MappedNote?.Row == PianoRow.Low &&
                      result.MappedNote.Binding.Position == 7;
        assert(passed, "Game Piano 七键：B3 映射为低音区第 7 级",
            result.Issue?.Message ?? $"实际映射: {result.MappedNote?.Row}/{result.MappedNote?.Binding.Position}");
    }

    private static void SevenKeyRejectsChromaticPitch(Action<bool, string, string> assert)
    {
        var result = PianoPitchMapper.TryMapSevenKey(61, SevenKeyProfile(60, "C Major"));
        bool passed = !result.IsMapped && result.Issue?.Code == PianoIssueCode.UnsupportedPitchClass;
        assert(passed, "Game Piano 七键：C 大调 C#4 标为不可演奏且不映射修饰键",
            result.Issue?.Code.ToString() ?? "没有返回诊断");
    }

    private static void ChangingScaleChangesMapping(Action<bool, string, string> assert)
    {
        var cMajor = PianoPitchMapper.TryMapSevenKey(64, SevenKeyProfile(60, "C Major"));
        var dMajor = PianoPitchMapper.TryMapSevenKey(64, SevenKeyProfile(62, "D Major"));
        bool passed = cMajor.IsMapped && dMajor.IsMapped &&
                      cMajor.MappedNote?.Binding.Position == 3 &&
                      dMajor.MappedNote?.Binding.Position == 2;
        assert(passed, "Game Piano 七键：切换至 D 大调后 E4 重新映射为第 2 级",
            $"C 大调={cMajor.MappedNote?.Binding.Position}, D 大调={dMajor.MappedNote?.Binding.Position}");
    }

    private static void TwelveKeyPrefersDirectBinding(Action<bool, string, string> assert)
    {
        var profile = TwelveKeyProfile();
        var candidates = PianoPitchMapper.FindTwelveKeyCandidates(60, profile);
        bool passed = candidates.Count > 0 && candidates[0].Modifier == PianoModifier.None &&
                      candidates[0].Binding.BaseMidiPitch == 60;
        assert(passed, "Game Piano 十二键：存在直接音高键位时优先于修饰键",
            candidates.Count == 0 ? "没有候选" : $"首选修饰键: {candidates[0].Modifier}");
    }

    private static void AbsoluteTimeIsScaledOnce(Action<bool, string, string> assert)
    {
        var note = new RawMidiNote { StartUs = 100_000, DurationUs = 200_000, NoteNumber = 60, Velocity = 90 };
        var result = PianoMidiCompiler.Compile(
            new[] { note }, SevenKeyProfile(60, "C Major"),
            new PianoCompileOptions { SpeedPercent = 50 });
        bool passed = result.Actions.Count == 2 &&
                      result.Actions[0].TimeUs == 200_000 &&
                      result.Actions[1].TimeUs == 600_000;
        assert(passed, "Game Piano 编译：50% 速度将起音与释音时间各缩放一次",
            string.Join(",", result.Actions.Select(x => x.TimeUs)));
    }

    private static void OverlappingSameKeyNotesReleaseAfterLastEnd(Action<bool, string, string> assert)
    {
        var notes = new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 100_000, NoteNumber = 60, Velocity = 90 },
            new RawMidiNote { StartUs = 50_000, DurationUs = 100_000, NoteNumber = 60, Velocity = 90 }
        };
        var result = PianoMidiCompiler.Compile(notes, SevenKeyProfile(60, "C Major"), new PianoCompileOptions());
        var keyActions = result.Actions.Where(x => x.MidiPitch == 60 &&
            x.Type is PianoActionType.KeyDown or PianoActionType.KeyUp).ToList();
        bool passed = keyActions.Count == 2 &&
                      keyActions[0].Type == PianoActionType.KeyDown && keyActions[0].TimeUs == 0 &&
                      keyActions[1].Type == PianoActionType.KeyUp && keyActions[1].TimeUs == 150_000 &&
                      result.Issues.Any(x => x.Code == PianoIssueCode.SameKeyOverlap);
        assert(passed, "Game Piano 编译：同键重叠合并持键并报告冲突",
            string.Join(",", keyActions.Select(x => $"{x.Type}@{x.TimeUs}")));
    }

    private static void TransposeOverflowIsReported(Action<bool, string, string> assert)
    {
        var note = new RawMidiNote { StartUs = 0, DurationUs = 100_000, NoteNumber = 120, Velocity = 90 };
        var result = PianoMidiCompiler.Compile(
            new[] { note }, SevenKeyProfile(120, "C Major"),
            new PianoCompileOptions { TransposeSemitones = 24 });
        bool passed = !result.CanStart && result.Issues.Any(x => x.Code == PianoIssueCode.TransposeOutOfMidiRange);
        assert(passed, "Game Piano 编译：移调超出 MIDI 0..127 时报告并阻止启动",
            string.Join(",", result.Issues.Select(x => x.Code)));
    }

    private static void UncalibratedDefaultsBlockPlayback(Action<bool, string, string> assert)
    {
        var profile = PianoLayoutProfile.CreateUncalibrated(PianoLayoutKind.SevenKeyScale);
        var result = PianoMidiCompiler.Compile(
            new[] { new RawMidiNote { StartUs = 0, DurationUs = 100_000, NoteNumber = 60, Velocity = 90 } },
            profile,
            new PianoCompileOptions());
        bool passed = !profile.IsFullyCalibrated && !result.CanStart && result.Actions.Count == 0 &&
                      result.Issues.Any(issue => issue.Code == PianoIssueCode.MissingBinding);
        assert(passed, "Game Piano 安全：未校准的默认键位阻止实际播放", string.Join(",", result.Issues.Select(issue => issue.Code)));
    }

    private static void DrumChannelIsFiltered(Action<bool, string, string> assert)
    {
        var note = new RawMidiNote { StartUs = 0, DurationUs = 100_000, NoteNumber = 60, Velocity = 90, Channel = 9 };
        var result = PianoMidiCompiler.Compile(new[] { note }, SevenKeyProfile(60, "C Major"), new PianoCompileOptions());
        bool passed = result.SourceNoteCount == 1 && result.CompiledNoteCount == 0 && result.Actions.Count == 0 &&
                      result.Issues.Any(issue => issue.Severity == PianoIssueSeverity.Info);
        assert(passed, "Game Piano MIDI：默认过滤 10 声道鼓音", $"编译音符={result.CompiledNoteCount}, 动作={result.Actions.Count}");
    }

    private static void ModifierScanCollisionInvalidatesProfile(Action<bool, string, string> assert)
    {
        var profile = TwelveKeyProfile();
        profile.ModifierProfile!.Shift = new PianoModifierKey { ScanCode = profile.Bindings[0].ScanCode, IsVerified = true, SemitoneDelta = 1 };
        var errors = profile.ValidateStructure();
        bool passed = errors.Any(error => error.Contains("修饰键不能与音符键", StringComparison.Ordinal));
        assert(passed, "Game Piano 校准：修饰键与音符键扫描码冲突时拒绝配置", string.Join(";", errors));
    }

    private static void SkipPolicyRequiresAcknowledgement(Action<bool, string, string> assert)
    {
        var notes = new[]
        {
            new RawMidiNote { StartUs = 0, DurationUs = 100_000, NoteNumber = 60, Velocity = 90 },
            new RawMidiNote { StartUs = 150_000, DurationUs = 100_000, NoteNumber = 61, Velocity = 90 }
        };
        var result = PianoMidiCompiler.Compile(notes, SevenKeyProfile(60, "C Major"),
            new PianoCompileOptions { UnplayableNotePolicy = UnplayableNotePolicy.SkipWithConfirmation });
        bool passed = result.CanStart && result.RequiresUserConfirmation && result.SkippedNoteCount == 1;
        assert(passed, "Game Piano 编译：跳过不可演奏音符时要求用户确认", $"CanStart={result.CanStart}, Confirmation={result.RequiresUserConfirmation}");
    }

    private static void StartWaitsForGameFocusAndCanBeCancelled(Action<bool, string, string> assert)
    {
        var config = new AppConfig { TargetWindowHandle = new IntPtr(1) };
        using var session = new PianoSessionService(config, new WebEventPublisher());
        var notes = new[] { new RawMidiNote { StartUs = 0, DurationUs = 100_000, NoteNumber = 60, Velocity = 90 } };
        session.Start(notes, "countdown-test", SevenKeyProfile(60, "C Major"), new PianoCompileOptions());
        var started = JsonSerializer.SerializeToElement(session.GetState());
        assert(started.GetProperty("status").GetString() == "countdown" &&
               started.GetProperty("countdownSeconds").GetInt32() == 3 && !session.IsPlaying,
            "Game Piano 开始后先等待 3 秒且不立即发送按键", started.ToString());

        session.TogglePause();
        var paused = JsonSerializer.SerializeToElement(session.GetState());
        assert(paused.GetProperty("status").GetString() == "paused",
            "Game Piano 倒计时可由播放热键暂停", paused.ToString());

        session.TogglePause();
        var resumed = JsonSerializer.SerializeToElement(session.GetState());
        assert(resumed.GetProperty("status").GetString() == "countdown" &&
               resumed.GetProperty("countdownSeconds").GetInt32() == 3,
            "Game Piano 倒计时可由播放热键继续", resumed.ToString());

        session.Stop();
        Thread.Sleep(3200);
        var stopped = JsonSerializer.SerializeToElement(session.GetState());
        assert(stopped.GetProperty("status").GetString() == "stopped" && !session.IsPlaying,
            "Game Piano 倒计时停止后不会延迟启动", stopped.ToString());
    }

    private static void HotkeyRoutesToSelectedOrActivePiano(Action<bool, string, string> assert)
    {
        bool correct = PlaybackHotkeyRouter.Resolve(overlayVisible: false, pianoPageSelected: true, pianoActive: false, generalActive: false) == PlaybackHotkeyTarget.Piano &&
                       PlaybackHotkeyRouter.Resolve(overlayVisible: false, pianoPageSelected: false, pianoActive: true, generalActive: false) == PlaybackHotkeyTarget.Piano &&
                       PlaybackHotkeyRouter.Resolve(overlayVisible: true, pianoPageSelected: true, pianoActive: true, generalActive: false) == PlaybackHotkeyTarget.Overlay &&
                       PlaybackHotkeyRouter.Resolve(overlayVisible: false, pianoPageSelected: true, pianoActive: false, generalActive: true) == PlaybackHotkeyTarget.General &&
                       PlaybackHotkeyRouter.Resolve(overlayVisible: false, pianoPageSelected: false, pianoActive: false, generalActive: false) == PlaybackHotkeyTarget.General;
        assert(correct, "F8 按当前钢琴页面或钢琴活动状态路由，且不误启动吉他", "热键目标错误");
    }

    private static PianoLayoutProfile SevenKeyProfile(int tonicMidi, string scaleName)
    {
        var bindings = new List<PianoKeyBinding>();
        ushort scanCode = 1;
        foreach (var row in new[] { PianoRow.Low, PianoRow.Middle, PianoRow.High })
        for (int position = 1; position <= 7; position++)
        {
            bindings.Add(new PianoKeyBinding
            {
                Row = row,
                Position = position,
                DisplayKey = $"Test{scanCode}",
                ScanCode = scanCode++,
                IsVerified = true
            });
        }

        return new PianoLayoutProfile
        {
            Id = "test-seven",
            DisplayName = "Test Seven",
            Kind = PianoLayoutKind.SevenKeyScale,
            Rows = new List<PianoRow> { PianoRow.Low, PianoRow.Middle, PianoRow.High },
            Bindings = bindings,
            ScaleProfile = new PianoScaleProfile
            {
                Id = "test-scale",
                DisplayName = scaleName,
                TonicMidiPitchAtMiddleRow = tonicMidi,
                Intervals = new[] { 0, 2, 4, 5, 7, 9, 11 }.ToList()
            }
        };
    }

    private static PianoLayoutProfile TwelveKeyProfile()
    {
        var bindings = new List<PianoKeyBinding>();
        ushort scanCode = 1;
        var rows = new[] { PianoRow.Low, PianoRow.Middle, PianoRow.High };
        for (int rowIndex = 0; rowIndex < rows.Length; rowIndex++)
        for (int position = 1; position <= 12; position++)
        {
            bindings.Add(new PianoKeyBinding
            {
                Row = rows[rowIndex],
                Position = position,
                DisplayKey = $"Test{scanCode}",
                ScanCode = scanCode++,
                BaseMidiPitch = 48 + rowIndex * 12 + position - 1,
                IsVerified = true
            });
        }

        return new PianoLayoutProfile
        {
            Id = "test-twelve",
            DisplayName = "Test Twelve",
            Kind = PianoLayoutKind.TwelveKeyChromatic,
            Rows = rows.ToList(),
            Bindings = bindings,
            ModifierProfile = new PianoModifierProfile { IsVerified = false }
        };
    }
}
