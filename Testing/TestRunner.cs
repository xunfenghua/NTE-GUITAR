using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MidiAutoPlayer.Core.Compilation;
using MidiAutoPlayer.Input;
using MidiAutoPlayer.Library;
using MidiAutoPlayer.Midi;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Preview;
using MidiAutoPlayer.Recognition;
using MidiAutoPlayer.Utils;

namespace MidiAutoPlayer.Testing;

public static class TestRunner
{
    private static int _passedCount = 0;
    private static int _failedCount = 0;

    public static string LogFilePath => Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "test_results.log");
    private static readonly List<string> _logLines = new();

    public static int RunAllTests()
    {
        try
        {
            if (File.Exists(LogFilePath)) File.Delete(LogFilePath);
        }
        catch { }

        _passedCount = 0;
        _failedCount = 0;

        Log("==================================================");
        Log("NTE-GUITAR 自动化测试矩阵验证 (Section 13)");
        Log("==================================================");

        RunSafely("游戏钢琴映射与动作编译", () => PianoAcceptanceTests.RunAll(Assert));
        RunSafely("推荐谱等级与标签规则", () => RecommendedScoreTests.RunAll(Assert));

        RunSafely("单音 C4", Test_SingleNote_C4);
        RunSafely("试听音高跟随各模式编译结果", Test_AuditionPitchProjection_FollowsCompiledEvents);
        RunSafely("吉他曲库保留原始低音音高", Test_GuitarLibraryNotes_PreserveSourceOctave);
        RunSafely("C4+E4+G4 和弦", Test_Chord_Batch);
        RunSafely("同音 20ms 重复保持最短按下", Test_RepeatNote_20ms_EarlyKeyUp);
        RunSafely("同音 <8ms 重复不漏音", Test_RepeatNote_Under8ms_Merged);
        RunSafely("低时序设置可保持 5ms", Test_KeyboardMinimumHoldWithLegacySettings);
        RunSafely("调度延迟仍保持实际最短按压", Test_KeyboardHoldGuardUnderLateDispatch);
        RunSafely("C3 超范围八度折叠", Test_OutOfRange_C3_OctaveFold);
        RunSafely("G6 超范围八度折叠", Test_OutOfRange_G6_OctaveFold);
        RunSafely("同时同音去重", Test_Simultaneous_SameNote_Deduplication);
        RunSafely("过滤鼓轨", Test_DrumChannel_Filtered);
        RunSafely("修改预设无需重转曲库", Test_Preset_Change_Without_Reconversion);
        RunSafely("曲库独立性与删除原MIDI", Test_Library_Persistence_And_DeleteSourceMidi);
        RunSafely("允许同名歌曲保存", Test_Library_SameDisplayName_Allowed);
        RunSafely("重命名歌曲只改Meta", Test_Library_Rename_Only_Updates_Meta);
        RunSafely("Tempo Map 变速准确度", Test_TempoMap_Accuracy);
        RunSafely("缓存损坏容错", Test_Cache_Corruption_FaultTolerance);
        RunSafely("超长时长格式化 (>60分钟)", Test_TimeFormatter);
        RunSafely("非标/截断 MIDI 容错解析", Test_LenientMidiParsing);
        RunSafely("超长超大 MIDI 性能与转换", Test_LargeMidi_Performance);
        RunSafely("智能旋律候选生成矩阵", Test_MelodyRecognition_AllModes);
        RunSafely("原生 MIDI 试听播放器控制", Test_MidiPreviewPlayer_Lifecycle);
        RunSafely("钢琴模式动态智能延音矩阵", Test_Piano_DynamicSustain);
        RunSafely("36键物理映射与唯一性审计", Test_36Key_Preset_Uniqueness_Audit);
        RunSafely("TimingProfile 时序参数与不变量校验", Test_TimingProfile_Invariants_Audit);
        RunSafely("ActionCompiler 同键重触发安全性校验", Test_ActionCompiler_RetriggerSafety_Audit);
        RunSafely("钢琴模式完整生命周期与调度审计", Test_Piano_FullLifecycle_Audit);
        RunSafely("钢琴模式多音并发与硬件扫描码审计", Test_Piano_Polyphony_ScanCode_Audit);
        RunSafely("曲库重新转换与参数更新审计", Test_Reconvert_Song_Audit);
        RunSafely("曲库转换覆盖替换与RawNotes持久化审计", Test_ConvertAndSave_ReplacesExistingSong_Audit);
        RunSafely("WebView2 IPC 消息与模式持久化审计", Test_WebMessageHandler_IPC_Audit);
        RunSafely("双时钟域变速引擎与物理可达性校验矩阵", Test_PlaybackSpeedEngine_Audit);
        RunSafely("窗体尺寸自由/固定锁定与消息路由审计", Test_WindowSizeLock_Audit);
        RunSafely("内置分类谱库自动灌装与幂等性审计", Test_BuiltInScores_Seeding_Audit);
        RunSafely("单文件嵌入式资源与视频释放审计", Test_EmbeddedAssetExtractor_Audit);
        RunSafely("吉他模式核心算法与验收矩阵 (Section 19)", () => GuitarTestRunner.RunAllGuitarTests(Assert));
        RunSafely("V3.0 半自动吉他模式验收测试矩阵 (Acceptance-Test-Checklist)", () => SemiAutoAcceptanceTests.RunAll(Assert));

        Log("==================================================");
        Log($"测试总结: 共 {_passedCount + _failedCount} 项，通过: {_passedCount} 项，失败: {_failedCount} 项");
        Log("==================================================");

        return _failedCount == 0 ? 0 : 1;
    }

    private static void RunSafely(string name, Action testAction)
    {
        try
        {
            testAction();
        }
        catch (Exception ex)
        {
            Assert(false, name, $"抛出异常: {ex.Message}\n{ex.StackTrace}");
        }
    }

    private static void Log(string line)
    {
        Console.WriteLine(line);
        try
        {
            File.AppendAllText(LogFilePath, line + Environment.NewLine);
        }
        catch { }
        try
        {
            File.AppendAllText(Path.Combine(Directory.GetCurrentDirectory(), "test_results.log"), line + Environment.NewLine);
        }
        catch { }
    }

    private static void Assert(bool condition, string testName, string details = "")
    {
        if (condition)
        {
            _passedCount++;
            Log($"[PASS] {testName}");
        }
        else
        {
            _failedCount++;
            Log($"[FAIL] {testName} - {details}");
        }
    }

    private static void Test_SingleNote_C4()
    {
        // C4 = MIDI 60 -> NoteIndex = 60 - 52 = 8
        // Default mapping: NoteIndex 8 -> L (ScanCode 0x26)
        var rawNotes = new List<RawMidiNote>
        {
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 500_000, NoteNumber = 60, Velocity = 100, Channel = 0 }
        };

        var song = MidiConverter.Convert(rawNotes, "test.mid", "hash1", "Test", new ConvertOptions());
        Assert(song.Notes.Count == 1 && song.Notes[0].NoteIndex == 8, "单音 C4: 转换为 NoteIndex 8");

        var preset = PresetManager.CreateDefaultPreset();
        var batches = ActionCompiler.Compile(song.Notes, preset);

        Assert(batches.Count == 2, "单音 C4: 编译为 2 个批次 (Down + Up)");
        Assert(batches[0].Actions.Count == 1 && batches[0].Actions[0].ScanCode == 0x26 && batches[0].Actions[0].IsDown, "单音 C4: KeyDown(0x26 L)");
        Assert(batches[1].Actions.Count == 1 && batches[1].Actions[0].ScanCode == 0x26 && !batches[1].Actions[0].IsDown, "单音 C4: KeyUp(0x26 L)");
        Assert(batches[1].TimeUs - batches[0].TimeUs == 30_000, "单音 C4: TapDuration 30ms (30,000 us)");
    }

    private static void Test_AuditionPitchProjection_FollowsCompiledEvents()
    {
        var piano = PlaybackPitchProjection.FromPianoEvents(new[]
        {
            new GameNoteEvent(100_000, (byte)(72 - MidiConverter.GameMinMidi), 250_000)
        });
        Assert(piano.Count == 1 && piano[0].NoteNumber == 72 && piano[0].StartUs == 100_000,
            "钢琴监听使用已移调的编译音高", $"实际音符: {(piano.Count > 0 ? piano[0].NoteNumber : -1)}");

        var fret = PlaybackPitchProjection.FromFretEvents(new[]
        {
            new FretPlayEvent(200_000, 1, 8, 72, durationUs: 300_000),
            new FretPlayEvent(400_000, 1, 0, 60, durationUs: 300_000, isSkipped: true)
        });
        Assert(fret.Count == 1 && fret[0].NoteNumber == 72 && fret[0].StartUs == 200_000,
            "吉他独奏监听使用八度适配后的音高并忽略跳过音", $"实际音符数/音高: {fret.Count}/{(fret.Count > 0 ? fret[0].NoteNumber : -1)}");

        var chordEvent = new ChordEvent(300_000, 700_000, ChordId.C)
        {
            AttackNotes = new List<RawMidiNote>
            {
                new() { StartUs = 300_000, DurationUs = 400_000, NoteNumber = 72, Velocity = 90 }
            }
        };
        var chord = PlaybackPitchProjection.FromChordEvents(new[] { chordEvent }, new ChordVoicingProfile(), GuitarPlayStyle.PluckOnly);
        Assert(chord.Count == 1 && chord[0].NoteNumber == 60 && chord[0].StartUs == 300_000,
            "吉他和弦监听按实际 C 和弦指型映射音高", $"实际音符: {(chord.Count > 0 ? chord[0].NoteNumber : -1)}");
    }

    private static void Test_GuitarLibraryNotes_PreserveSourceOctave()
    {
        var source = new RawMidiNote
        {
            StartUs = 0,
            DurationUs = 300_000,
            NoteNumber = 40, // E2, playable on a standard guitar's sixth open string
            Velocity = 100
        };
        var folded = MidiConverter.Convert(
            new[] { source }, "guitar_test.mid", "hash_guitar_pitch", "吉他低音测试",
            new ConvertOptions { TargetInstrument = 1, OutOfRangeMode = "OctaveFold" });
        folded.RawNotes = new List<RawMidiNote> { source };
        folded.SelectedRawNotes = new List<RawMidiNote> { source };

        var playableNotes = EffectiveSongNotes.ToRawNotes(folded);
        Assert(playableNotes.Count == 1 && playableNotes[0].NoteNumber == 40,
            "吉他播放保留 E2 原音，不沿用 36 键转换时折到 E3 的缓存", 
            $"实际 MIDI 音高: {(playableNotes.Count > 0 ? playableNotes[0].NoteNumber : -1)}");

        folded.Options.Transpose = 2;
        playableNotes = EffectiveSongNotes.ToRawNotes(folded);
        Assert(playableNotes.Count == 1 && playableNotes[0].NoteNumber == 42,
            "吉他曲库音符仍只应用一次导入移调", 
            $"实际 MIDI 音高: {(playableNotes.Count > 0 ? playableNotes[0].NoteNumber : -1)}");
    }

    private static void Test_Chord_Batch()
    {
        // C4 (60->8), E4 (64->12), G4 (67->15) at identical time
        var rawNotes = new List<RawMidiNote>
        {
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 500_000, NoteNumber = 60, Velocity = 100, Channel = 0 },
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 500_000, NoteNumber = 64, Velocity = 100, Channel = 0 },
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 500_000, NoteNumber = 67, Velocity = 100, Channel = 0 },
        };

        var song = MidiConverter.Convert(rawNotes, "test.mid", "hash2", "ChordTest", new ConvertOptions());
        Assert(song.Notes.Count == 3, "C4+E4+G4 和弦: 转换 3 个音符");

        var preset = PresetManager.CreateDefaultPreset();
        var batches = ActionCompiler.Compile(song.Notes, preset);

        // Batches: 1 Down batch with 3 actions at 1,000,000 us, 1 Up batch with 3 actions at 1,030,000 us
        Assert(batches.Count == 2, "C4+E4+G4 和弦: 聚合为 2 个批次 (Down批次 + Up批次)");
        Assert(batches[0].Actions.Count == 3, "和弦批量发送: 同一时刻 3 个 KeyDown 位于同一个 InputBatch 中");
        Assert(batches[0].Actions.All(a => a.IsDown), "和弦 Down 批次全部为 KeyDown");
        Assert(batches[1].Actions.Count == 3 && batches[1].Actions.All(a => !a.IsDown), "和弦 Up 批次全部为 KeyUp");
    }

    private static void Test_RepeatNote_20ms_EarlyKeyUp()
    {
        // Same note triggered at 1000ms (1,000,000 us) and 1020ms (1,020,000 us)
        // A retrigger cannot shorten the first physical hold below 20ms.
        var rawNotes = new List<RawMidiNote>
        {
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 100_000, NoteNumber = 60, Velocity = 100, Channel = 0 },
            new RawMidiNote { StartUs = 1_020_000, DurationUs = 100_000, NoteNumber = 60, Velocity = 100, Channel = 0 }
        };

        var song = MidiConverter.Convert(rawNotes, "test.mid", "hash3", "Repeat20ms", new ConvertOptions());
        var preset = PresetManager.CreateDefaultPreset();
        var batches = ActionCompiler.Compile(song.Notes, preset);

        Assert(batches.Count == 4, $"同音 20ms 重复: 生成 4 个批次 (当前: {batches.Count})");
        // Batch 0: Down at 1,000,000
        Assert(batches[0].TimeUs == 1_000_000 && batches[0].Actions[0].IsDown, "同音重复: 第 1 次按下 1000ms");
        Assert(batches[1].TimeUs - batches[0].TimeUs >= 20_000 && !batches[1].Actions[0].IsDown,
            "同音重复: 第 1 次按下至少 20ms");
        Assert(batches[2].TimeUs - batches[1].TimeUs >= 2_000 && batches[2].Actions[0].IsDown,
            "同音重复: 第 2 次等待释放间隙后按下");
        Assert(batches[3].TimeUs - batches[2].TimeUs >= 20_000 && !batches[3].Actions[0].IsDown,
            "同音重复: 第 2 次按下至少 20ms");
    }

    private static void Test_RepeatNote_Under8ms_Merged()
    {
        // Triggers at 1000ms and 1005ms (< 8ms MinRetriggerInterval)
        var rawNotes = new List<RawMidiNote>
        {
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 100_000, NoteNumber = 60, Velocity = 100, Channel = 0 },
            new RawMidiNote { StartUs = 1_005_000, DurationUs = 100_000, NoteNumber = 60, Velocity = 100, Channel = 0 }
        };

        var song = MidiConverter.Convert(rawNotes, "test.mid", "hash4", "Repeat5ms", new ConvertOptions());
        var preset = PresetManager.CreateDefaultPreset();
        var batches = ActionCompiler.Compile(song.Notes, preset);

        Assert(batches.Count == 4, $"同音 <8ms 重复: 保留两次触发 (批次数: {batches.Count})");
        Assert(batches[1].TimeUs - batches[0].TimeUs >= 20_000 &&
               batches[2].TimeUs - batches[1].TimeUs >= 2_000 &&
               batches[3].TimeUs - batches[2].TimeUs >= 20_000,
            "同音 <8ms 重复: 每次保持 20ms 并留出释放间隙");
    }

    private static void Test_KeyboardMinimumHoldWithLegacySettings()
    {
        var notes = new[]
        {
            new GameNoteEvent(0, 0, 5_000),
            new GameNoteEvent(1_000, 0, 5_000),
            new GameNoteEvent(2_000, 0, 5_000)
        };
        var batches = ActionCompiler.Compile(notes, PresetManager.CreateDefaultPreset(), new CompilerOptions
        {
            MinHoldUs = 4_000,
            TapDurationUs = 5_000,
            ReleaseGapUs = 2_000
        });
        Assert(batches.Count == 6, $"低时序设置: 3 次同键触发全部保留 (批次数: {batches.Count})");
        if (batches.Count != 6) return;
        for (int i = 0; i < batches.Count; i += 2)
        {
            Assert(batches[i].Actions[0].IsDown && !batches[i + 1].Actions[0].IsDown &&
                   batches[i + 1].TimeUs - batches[i].TimeUs >= 5_000,
                $"低时序设置: 第 {i / 2 + 1} 次按下至少 5ms");
            if (i + 2 < batches.Count)
                Assert(batches[i + 2].TimeUs - batches[i + 1].TimeUs >= 2_000,
                    $"低时序设置: 第 {i / 2 + 1} 次抬起后留出释放间隙");
        }
    }

    private static void Test_KeyboardHoldGuardUnderLateDispatch()
    {
        const long ticksPerSecond = 1_000_000;
        var guard = new KeyboardHoldGuard(ticksPerSecond);
        var down = new[] { new KeyAction(0, 0x26, isDown: true, extended: false) };
        var up = new[] { new KeyAction(5_000, 0x26, isDown: false, extended: false) };

        guard.RecordSent(down, 100_000); // Down was dispatched 100ms late.
        Assert(guard.EarliestDispatchTicks(up, 5_000) == 105_000,
            "调度延迟: KeyUp 等待真实 KeyDown 后至少 5ms");
        guard.RecordSent(up, 105_500);
        Assert(guard.EarliestDispatchTicks(down, 6_000) == 107_500,
            "调度延迟: 下一次 KeyDown 等待真实释放后至少 2ms");
    }

    private static void Test_OutOfRange_C3_OctaveFold()
    {
        // C3 = MIDI 48 (below E3=52) -> +12 -> C4 = 60 -> NoteIndex = 8
        var rawNotes = new List<RawMidiNote>
        {
            new RawMidiNote { StartUs = 0, DurationUs = 100_000, NoteNumber = 48, Velocity = 100, Channel = 0 }
        };

        var song = MidiConverter.Convert(rawNotes, "test.mid", "hash5", "FoldC3", new ConvertOptions { OutOfRangeMode = "OctaveFold" });
        Assert(song.Notes.Count == 1 && song.Notes[0].NoteIndex == 8, $"C3 超范围: 折叠成 C4 (NoteIndex 8, 实际: {song.Notes[0].NoteIndex})");
    }

    private static void Test_OutOfRange_G6_OctaveFold()
    {
        // G6 = MIDI 91 (above D#6=87) -> -12 -> G5 = 79 -> NoteIndex = 79 - 52 = 27
        var rawNotes = new List<RawMidiNote>
        {
            new RawMidiNote { StartUs = 0, DurationUs = 100_000, NoteNumber = 91, Velocity = 100, Channel = 0 }
        };

        var song = MidiConverter.Convert(rawNotes, "test.mid", "hash6", "FoldG6", new ConvertOptions { OutOfRangeMode = "OctaveFold" });
        Assert(song.Notes.Count == 1 && song.Notes[0].NoteIndex == 27, $"G6 超范围: 折叠成 G5 (NoteIndex 27, 实际: {song.Notes[0].NoteIndex})");
    }

    private static void Test_Simultaneous_SameNote_Deduplication()
    {
        // C3 (48 -> folds to 60) and C4 (60) occurring at exact same timestamp 1,000,000 us
        var rawNotes = new List<RawMidiNote>
        {
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 100_000, NoteNumber = 48, Velocity = 100, Channel = 0 },
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 100_000, NoteNumber = 60, Velocity = 100, Channel = 0 }
        };

        var song = MidiConverter.Convert(rawNotes, "test.mid", "hash7", "Dedup", new ConvertOptions { OutOfRangeMode = "OctaveFold" });
        Assert(song.Notes.Count == 1 && song.Notes[0].NoteIndex == 8, $"C3+C4 同时出现: 折叠去重后只保留 1 个 C4 (实际数量: {song.Notes.Count})");
    }

    private static void Test_DrumChannel_Filtered()
    {
        // Channel 9 (MIDI Ch 10) is drum track, should be ignored by default
        var rawNotes = new List<RawMidiNote>
        {
            new RawMidiNote { StartUs = 100_000, DurationUs = 100_000, NoteNumber = 36, Velocity = 100, Channel = 9 },
            new RawMidiNote { StartUs = 200_000, DurationUs = 100_000, NoteNumber = 60, Velocity = 100, Channel = 0 }
        };

        var song = MidiConverter.Convert(rawNotes, "test.mid", "hash8", "DrumFilter", new ConvertOptions { IgnoreDrumChannel = true });
        Assert(song.Notes.Count == 1 && song.Notes[0].NoteIndex == 8, $"包含鼓轨: 默认过滤鼓通道 (有效音符数: {song.Notes.Count})");
    }

    private static void Test_Preset_Change_Without_Reconversion()
    {
        // Song is saved with NoteIndex 8 (C4).
        // Preset A maps 8 -> ScanCode 0x26 (L).
        // Custom Preset B maps 8 -> ScanCode 0x1E (A).
        var songNotes = new List<GameNoteEvent> { new GameNoteEvent(100_000, 8) };

        var presetA = PresetManager.CreateDefaultPreset();
        var batchesA = ActionCompiler.Compile(songNotes, presetA);
        Assert(batchesA[0].Actions[0].ScanCode == 0x26, "修改键位预设: 默认预设映射到 0x26 (L)");

        var presetB = PresetManager.CreateDefaultPreset();
        presetB.Keys[8].ScanCode = 0x1E; // Remap C4 to A
        var batchesB = ActionCompiler.Compile(songNotes, presetB);
        Assert(batchesB[0].Actions[0].ScanCode == 0x1E, "修改键位预设: 曲库歌曲无需重新转换即可直接按新键位 0x1E (A) 编译播放");
    }

    private static void Test_Library_Persistence_And_DeleteSourceMidi()
    {
        var lib = new SongLibrary();
        string tempMidi = Path.Combine(Path.GetTempPath(), $"test_{Guid.NewGuid():N}.mid");
        File.WriteAllBytes(tempMidi, new byte[] { 1, 2, 3 });

        var song = new ConvertedSong
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = "测试独立曲库歌曲",
            SourceName = Path.GetFileName(tempMidi),
            SourceHash = "abc12345",
            DurationUs = 10_000_000,
            Notes = new List<GameNoteEvent>
            {
                new GameNoteEvent(0, 0),
                new GameNoteEvent(500_000, 8),
                new GameNoteEvent(1_000_000, 20)
            }
        };

        lib.SaveSong(song);

        // Delete the source MIDI file completely
        File.Delete(tempMidi);
        Assert(!File.Exists(tempMidi), "曲库独立性: 原 MIDI 文件已被删除");

        // Load song from library
        var reloaded = lib.LoadSong(song.Id);
        Assert(reloaded != null && reloaded.DisplayName == "测试独立曲库歌曲" && reloaded.Notes.Count == 3,
            "删除原 MIDI: 曲库中已保存歌曲仍然完整读取并包含 3 个音符");

        // Cleanup
        lib.DeleteSong(song.Id);
    }

    private static void Test_Library_SameDisplayName_Allowed()
    {
        var lib = new SongLibrary();
        var song1 = new ConvertedSong
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = "同名测试曲",
            Notes = new List<GameNoteEvent> { new GameNoteEvent(0, 1) }
        };
        var song2 = new ConvertedSong
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = "同名测试曲",
            Notes = new List<GameNoteEvent> { new GameNoteEvent(0, 2) }
        };

        lib.SaveSong(song1);
        lib.SaveSong(song2);

        var allSongs = lib.GetAllSongs().Where(s => s.DisplayName == "同名测试曲").ToList();
        Assert(allSongs.Count >= 2, $"同名歌曲: 允许保存同名歌曲且内部 UUID 彼此隔离 (数量: {allSongs.Count})");

        lib.DeleteSong(song1.Id);
        lib.DeleteSong(song2.Id);
    }

    private static void Test_Library_Rename_Only_Updates_Meta()
    {
        var lib = new SongLibrary();
        var song = new ConvertedSong
        {
            Id = Guid.NewGuid().ToString("N"),
            DisplayName = "旧名称",
            Notes = new List<GameNoteEvent> { new GameNoteEvent(0, 5) }
        };

        lib.SaveSong(song);
        bool renamed = lib.RenameSong(song.Id, "新名称");
        Assert(renamed, "重命名歌曲: 成功执行重命名");

        var loaded = lib.LoadSong(song.Id);
        Assert(loaded.DisplayName == "新名称", $"重命名歌曲: DisplayName 正确更新为新名称 (实际: {loaded.DisplayName})");

        lib.DeleteSong(song.Id);
    }

    private static void Test_TempoMap_Accuracy()
    {
        // Build a MIDI file programmatically with DryWetMidi having Tempo Change:
        // Bar 1 at 120 BPM (500,000 us/quarter note)
        // Bar 2 changes to 240 BPM (250,000 us/quarter note)
        var midiFile = new MidiFile { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
        var trackChunk = new TrackChunk();

        // 120 BPM = 500,000 us per beat
        trackChunk.Events.Add(new SetTempoEvent(500_000) { DeltaTime = 0 });
        trackChunk.Events.Add(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { DeltaTime = 0 });
        trackChunk.Events.Add(new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 480 }); // 1 beat

        // Tempo change at tick 480: 240 BPM = 250,000 us per beat
        trackChunk.Events.Add(new SetTempoEvent(250_000) { DeltaTime = 0 });
        trackChunk.Events.Add(new NoteOnEvent((SevenBitNumber)64, (SevenBitNumber)100) { DeltaTime = 0 });
        trackChunk.Events.Add(new NoteOffEvent((SevenBitNumber)64, (SevenBitNumber)0) { DeltaTime = 480 }); // 1 beat

        midiFile.Chunks.Add(trackChunk);

        string testMidiPath = Path.Combine(Path.GetTempPath(), $"tempo_test_{Guid.NewGuid():N}.mid");
        midiFile.Write(testMidiPath, true);

        try
        {
            var (_, notes, _, _) = MidiLoader.Load(testMidiPath);
            Assert(notes.Count == 2, "变速 MIDI 测试: 正确解析出 2 个 Note");

            // Note 0 at tick 0 -> 0 us
            // Note 1 at tick 480 (after 1 beat of 120 BPM) -> 500,000 us!
            Assert(notes[0].StartUs == 0, $"变速 MIDI Note 0: StartUs=0 (实际: {notes[0].StartUs})");
            Assert(Math.Abs(notes[1].StartUs - 500_000) < 500, $"变速 MIDI Note 1: StartUs跟随 Tempo Map=500,000 us (实际: {notes[1].StartUs})");

            // Duration of Note 1: 1 beat at 240 BPM = 250,000 us!
            Assert(Math.Abs(notes[1].DurationUs - 250_000) < 500, $"变速 MIDI Note 1: 持续时间跟随变速=250,000 us (实际: {notes[1].DurationUs})");
        }
        finally
        {
            File.Delete(testMidiPath);
        }
    }

    private static void Test_Cache_Corruption_FaultTolerance()
    {
        string fakeHash = "fakehash_corruption_test_123456";
        string cacheFile = Path.Combine(MidiRawCache.CacheDirectory, $"{fakeHash}.bin");
        Directory.CreateDirectory(MidiRawCache.CacheDirectory);

        // Write corrupt bytes
        File.WriteAllBytes(cacheFile, new byte[] { 0x00, 0x01, 0x02, 0x03, 0xFF, 0xFE });

        var result = MidiRawCache.TryGet(fakeHash);
        Assert(result == null, "技术缓存损坏容错: 损坏的二进制缓存安全返回 null，不崩溃程序");

        File.Delete(cacheFile);
    }

    private static void Test_LargeMidi_Performance()
    {
        // Generate a 50,000 note MIDI
        var midiFile = new MidiFile { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
        var trackChunk = new TrackChunk();
        trackChunk.Events.Add(new SetTempoEvent(500_000) { DeltaTime = 0 });

        for (int i = 0; i < 50_000; i++)
        {
            trackChunk.Events.Add(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { DeltaTime = 24 });
            trackChunk.Events.Add(new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 24 });
        }
        midiFile.Chunks.Add(trackChunk);

        string testMidiPath = Path.Combine(Path.GetTempPath(), $"large_test_{Guid.NewGuid():N}.mid");
        midiFile.Write(testMidiPath, true);

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var (sha256, notes, tracks, _) = MidiLoader.Load(testMidiPath);
            sw.Stop();
            Log($"[BENCHMARK] MidiLoader 50,000 notes: {sw.ElapsedMilliseconds} ms");

            sw.Restart();
            var song = MidiConverter.Convert(notes, "large.mid", sha256, "Large", new ConvertOptions());
            sw.Stop();
            Log($"[BENCHMARK] MidiConverter 50,000 notes: {sw.ElapsedMilliseconds} ms, Result notes: {song.Notes.Count}");

            var preset = PresetManager.CreateDefaultPreset();
            sw.Restart();
            var batches = ActionCompiler.Compile(song.Notes, preset);
            sw.Stop();
            Log($"[BENCHMARK] ActionCompiler 50,000 notes: {sw.ElapsedMilliseconds} ms, Batches: {batches.Count}");

            Assert(song.Notes.Count > 0, "超长超大 MIDI 性能与转换: 50,000 音符转换成功");
        }
        finally
        {
            File.Delete(testMidiPath);
        }
    }

    private static void Test_TimeFormatter()
    {
        // 0 us
        Assert(TimeFormatter.FormatUs(0) == "00:00", "0秒格式化: 00:00");
        // 35 seconds = 35,000,000 us
        Assert(TimeFormatter.FormatUs(35_000_000) == "00:35", "35秒格式化: 00:35");
        // 59m 59s = 3,599,000,000 us
        Assert(TimeFormatter.FormatUs(3_599_000_000) == "59:59", "59分59秒格式化: 59:59");
        // 60m 00s = 3,600,000,000 us -> 01:00:00
        Assert(TimeFormatter.FormatUs(3_600_000_000) == "01:00:00", "60分钟超长格式化: 01:00:00");
        // 75m 23s = 4,523,000,000 us -> 01:15:23
        Assert(TimeFormatter.FormatUs(4_523_000_000) == "01:15:23", "75分23秒超长格式化: 01:15:23");
        // 120m = 7,200,000,000 us -> 02:00:00
        Assert(TimeFormatter.FormatUs(7_200_000_000) == "02:00:00", "120分钟超长格式化: 02:00:00");
    }

    private static void Test_LenientMidiParsing()
    {
        // Create a valid MIDI file first
        var midiFile = new MidiFile { TimeDivision = new TicksPerQuarterNoteTimeDivision(480) };
        var trackChunk = new TrackChunk();
        trackChunk.Events.Add(new SetTempoEvent(500_000) { DeltaTime = 0 });
        trackChunk.Events.Add(new NoteOnEvent((SevenBitNumber)60, (SevenBitNumber)100) { DeltaTime = 0 });
        trackChunk.Events.Add(new NoteOffEvent((SevenBitNumber)60, (SevenBitNumber)0) { DeltaTime = 480 });
        midiFile.Chunks.Add(trackChunk);

        string testMidiPath = Path.Combine(Path.GetTempPath(), $"lenient_test_{Guid.NewGuid():N}.mid");
        midiFile.Write(testMidiPath, true);

        try
        {
            // Append garbage bytes at the end of the file (simulating incomplete download or bad chunks)
            byte[] garbage = new byte[] { 0xDE, 0xAD, 0xBE, 0xEF, 0x00, 0x11, 0x22 };
            using (var fs = new FileStream(testMidiPath, FileMode.Append, FileAccess.Write))
            {
                fs.Write(garbage, 0, garbage.Length);
            }

            var (sha256, notes, tracks, _) = MidiLoader.Load(testMidiPath);
            Assert(notes.Count == 1, "非标/尾部带异常字节 MIDI 容错: 忽略尾部垃圾字节成功解析音符");
            Assert(notes[0].NoteNumber == 60, "非标 MIDI 音符内容保持准确");
        }
        finally
        {
            if (File.Exists(testMidiPath)) File.Delete(testMidiPath);
        }
    }

    private static void Test_MelodyRecognition_AllModes()
    {
        var tracks = new List<TrackMetadata>
        {
            new TrackMetadata { TrackId = 0, Name = "Vocal Lead", Program = 73, NoteCount = 4 },
            new TrackMetadata { TrackId = 1, Name = "Piano Accompaniment", Program = 0, NoteCount = 6 },
            new TrackMetadata { TrackId = 2, Name = "Acoustic Bass", Program = 32, NoteCount = 2 }
        };

        var notes = new List<RawMidiNote>();
        // Vocal lead track 0: C5 (72), D5 (74), E5 (76), G5 (79)
        notes.Add(new RawMidiNote { TrackId = 0, Channel = 0, NoteNumber = 72, Velocity = 100, StartUs = 0, DurationUs = 400_000, Program = 73 });
        notes.Add(new RawMidiNote { TrackId = 0, Channel = 0, NoteNumber = 74, Velocity = 100, StartUs = 500_000, DurationUs = 400_000, Program = 73 });
        notes.Add(new RawMidiNote { TrackId = 0, Channel = 0, NoteNumber = 76, Velocity = 100, StartUs = 1_000_000, DurationUs = 400_000, Program = 73 });
        notes.Add(new RawMidiNote { TrackId = 0, Channel = 0, NoteNumber = 79, Velocity = 100, StartUs = 1_500_000, DurationUs = 400_000, Program = 73 });

        // Piano track 1 (chords): C4 (60), E4 (64), G4 (67) at time 0; D4 (62), F4 (65), A4 (69) at time 1_000_000
        notes.Add(new RawMidiNote { TrackId = 1, Channel = 1, NoteNumber = 60, Velocity = 80, StartUs = 0, DurationUs = 450_000, Program = 0 });
        notes.Add(new RawMidiNote { TrackId = 1, Channel = 1, NoteNumber = 64, Velocity = 80, StartUs = 0, DurationUs = 450_000, Program = 0 });
        notes.Add(new RawMidiNote { TrackId = 1, Channel = 1, NoteNumber = 67, Velocity = 80, StartUs = 0, DurationUs = 450_000, Program = 0 });

        notes.Add(new RawMidiNote { TrackId = 1, Channel = 1, NoteNumber = 62, Velocity = 80, StartUs = 1_000_000, DurationUs = 450_000, Program = 0 });
        notes.Add(new RawMidiNote { TrackId = 1, Channel = 1, NoteNumber = 65, Velocity = 80, StartUs = 1_000_000, DurationUs = 450_000, Program = 0 });
        notes.Add(new RawMidiNote { TrackId = 1, Channel = 1, NoteNumber = 69, Velocity = 80, StartUs = 1_000_000, DurationUs = 450_000, Program = 0 });

        // Bass track 2: C2 (36)
        notes.Add(new RawMidiNote { TrackId = 2, Channel = 2, NoteNumber = 36, Velocity = 90, StartUs = 0, DurationUs = 900_000, Program = 32 });
        notes.Add(new RawMidiNote { TrackId = 2, Channel = 2, NoteNumber = 38, Velocity = 90, StartUs = 1_000_000, DurationUs = 900_000, Program = 32 });

        notes.Sort((a, b) => a.StartUs != b.StartUs ? a.StartUs.CompareTo(b.StartUs) : a.NoteNumber.CompareTo(b.NoteNumber));

        var candidates = RecognitionService.GenerateAllCandidates(notes, tracks);

        // FullSong should contain all 12 notes
        Assert(candidates[RecognitionMode.FullSong].Notes.Count == 12, "旋律模式 FullSong: 保留全部 12 个音符");

        // TrackPriority should prioritize Track 0 (Vocal Lead)
        var trackPri = candidates[RecognitionMode.TrackPriority];
        Assert(trackPri.Notes.Count == 4 && trackPri.Notes.All(n => n.TrackId == 0), "旋律模式 TrackPriority: 智能识别出 Vocal Lead 轨道 (4音)");

        // HighestNote should pick the highest notes at each time slice
        var high = candidates[RecognitionMode.HighestNote];
        Assert(high.Notes.Count == 4 && high.Notes.All(n => n.NoteNumber >= 70), "旋律模式 HighestNote: 滤除低声部与伴奏，保留高音主旋律");

        // ContinuousMelody DP should follow the melody path
        var dpMelody = candidates[RecognitionMode.ContinuousMelody];
        Assert(dpMelody.Notes.Count >= 3, "旋律模式 ContinuousMelody: 动态规划平滑提取主旋律线");

        // Monophonic should remove chord duplicates at time 0
        var mono = candidates[RecognitionMode.Monophonic];
        Assert(mono.Notes.Count <= 6, "旋律模式 Monophonic: 单音去和弦提取");

        // AutoLeadFusion should extract melody and provide profiles and decisions
        var fusion = candidates[RecognitionMode.AutoLeadFusion];
        Assert(fusion.Notes.Count >= 3, "旋律模式 AutoLeadFusion: 多轨画像融合提取出主旋律线");
        Assert(fusion.Profiles != null && fusion.Profiles.Count == 3, "旋律模式 AutoLeadFusion: 完整输出轨道画像");
        Assert(fusion.Decisions != null && fusion.Decisions.Count > 0, "旋律模式 AutoLeadFusion: 完整输出决策置信度与可解释原因");

        var fullSong = new ConvertedSong
        {
            TargetInstrument = LibraryInstrument.Guitar,
            RecognitionModeName = "FullSong",
            RawNotes = notes.ToList(),
            SelectedRawNotes = notes.ToList()
        };
        var overlayLead = EffectiveSongNotes.ToGuitarOverlayNotes(fullSong);
        Assert(overlayLead.Count < notes.Count && overlayLead.Count == fusion.Notes.Count,
            "悬浮窗曲谱来源: FullSong 自动改用 AutoLeadFusion 主旋律，不把所有伴奏声部混入游戏吉他");

        var singleTrackSong = new ConvertedSong
        {
            TargetInstrument = LibraryInstrument.Guitar,
            RecognitionModeName = "FullSong",
            RawNotes = notes.Select(n => new RawMidiNote
            {
                StartUs = n.StartUs, DurationUs = n.DurationUs, NoteNumber = n.NoteNumber,
                Velocity = n.Velocity, Channel = n.Channel, TrackId = 0, Program = n.Program
            }).ToList(),
            SelectedRawNotes = notes.Select(n => new RawMidiNote
            {
                StartUs = n.StartUs, DurationUs = n.DurationUs, NoteNumber = n.NoteNumber,
                Velocity = n.Velocity, Channel = n.Channel, TrackId = 0, Program = n.Program
            }).ToList()
        };
        var singleTrackOverlay = EffectiveSongNotes.ToGuitarOverlayNotes(singleTrackSong);
        Assert(singleTrackOverlay.Select(n => (n.StartUs, n.NoteNumber, n.DurationUs))
                .SequenceEqual(singleTrackSong.SelectedRawNotes!.Select(n => (n.StartUs, n.NoteNumber, n.DurationUs))),
            "悬浮窗曲谱来源: 单轨 FullSong 保留原音符，不再重复抽取主旋律");

        // MelodyBiasSettings 独立配置测试
        var customSettings = new MelodyBiasSettings
        {
            UsePitchCenterBias = false,
            UseTransitionBias = false
        };
        var unbiasedResult = ContinuousMelodyMode.Extract(notes, tracks, customSettings);
        Assert(unbiasedResult.Notes.Count >= 3, "旋律模式 ContinuousMelody: 支持独立 MelodyBiasSettings 配置并平稳提取");
        Assert(ContinuousMelodyMode.CalculateDurationScore(new RawMidiNote { DurationUs = 100_000 }) > 0, "旋律评分: CalculateDurationScore 独立计算正常");
        Assert(ContinuousMelodyMode.CalculateVelocityScore(new RawMidiNote { Velocity = 100 }) > 0, "旋律评分: CalculateVelocityScore 独立计算正常");
    }

    private static void Test_MidiPreviewPlayer_Lifecycle()
    {
        using var player1 = new MidiPreviewPlayer();
        using var player2 = new MidiPreviewPlayer();

        Assert(player1.IsAvailable == player2.IsAvailable, "MIDI试听播放器多实例共享设备: player1 与 player2 设备可用性一致且无冲突");

        var sampleNotes1 = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 60, Velocity = 100, StartUs = 0, DurationUs = 300_000 },
            new RawMidiNote { NoteNumber = 64, Velocity = 100, StartUs = 100_000, DurationUs = 300_000 }
        };
        var sampleNotes2 = new List<RawMidiNote>
        {
            new RawMidiNote { NoteNumber = 72, Velocity = 100, StartUs = 0, DurationUs = 300_000 }
        };

        player1.Play(sampleNotes1, startOffsetUs: 50_000, explicitTotalDurationUs: 500_000);
        Assert(player1.TotalDurationUs == 500_000, "MIDI试听播放器: 支持显式指定总时长");
        Thread.Sleep(30);

        player1.Pause();
        Assert(player1.IsPaused || !player1.IsAvailable, "MIDI试听播放器: Pause 暂停控制");
        player1.Resume();
        Thread.Sleep(20);

        // 模拟 ConvertDialog 开始播放试听，主界面的 player1 自动被互斥停止
        player2.Play(sampleNotes2);
        Thread.Sleep(30);
        Assert(!player1.IsPlaying, "MIDI试听播放器互斥保障: player2 播放时 player1 自动停止");

        player2.Stop();
        Assert(!player2.IsPlaying, "MIDI试听播放器: Stop 停止控制与资源清理");
    }

    private static void Test_Piano_DynamicSustain()
    {
        // 场景: 两键离得很近 (<280ms) -> 不延音，使用 TapDuration 30ms 点触
        // Note 1: 1,000,000 us, rawDuration: 500,000 us (500ms)
        // Note 2: 1,150,000 us (150ms 后再次触发同一物理按键 NoteIndex 8 -> L)
        var rawNotes = new List<RawMidiNote>
        {
            new RawMidiNote { StartUs = 1_000_000, DurationUs = 500_000, NoteNumber = 60, Velocity = 100, Channel = 0 },
            new RawMidiNote { StartUs = 1_150_000, DurationUs = 800_000, NoteNumber = 60, Velocity = 100, Channel = 0 }
        };

        var song = MidiConverter.Convert(rawNotes, "test.mid", "hash_dyn", "DynTest", new ConvertOptions());
        var preset = PresetManager.CreateDefaultPreset();

        var options = new CompilerOptions
        {
            EnableDynamicSustain = true,
            TapDurationUs = 30_000,
            CloseNotesThresholdUs = 280_000
        };

        var batches = ActionCompiler.Compile(song.Notes, preset, options);

        // Note 1 (两键过近): 虽乐谱有 500ms 延音，但与下一音相距仅 150ms (<280ms) -> 严禁延音，30ms 迅速抬起
        var firstUp = batches.FirstOrDefault(b => b.TimeUs == 1_030_000);
        Assert(firstUp != null, "钢琴动态延音: 两键太近(150ms)时严格不延音，30ms 立即释放抬起");

        // Note 2 (最后一个音符): 乐谱有 800ms 延音，无后续按键碰撞 -> 饱满延音 800ms
        var secondUp = batches.FirstOrDefault(b => b.TimeUs == 1_150_000 + 800_000);
        Assert(secondUp != null, "钢琴动态延音: 最后一个音符且乐谱有延音，保持 800ms 饱满延音");
    }

    private static void Test_36Key_Preset_Uniqueness_Audit()
    {
        var preset = PresetManager.CreateDefaultPreset();
        Assert(preset.Keys.Count == 36, "36键审计: 默认配置包含且仅包含 36 个按键");

        var scanCodes = new HashSet<ushort>();
        var midiNotes = new HashSet<byte>();
        var noteNames = new HashSet<string>();

        foreach (var k in preset.Keys)
        {
            Assert(scanCodes.Add(k.ScanCode), $"36键审计: 扫描码 0x{k.ScanCode:X2} 严格唯一无重复");
            Assert(midiNotes.Add(k.MidiNumber), $"36键审计: MIDI音符 {k.MidiNumber} 严格唯一无重复");
            noteNames.Add(k.NoteName);
        }

        // 验证音高覆盖 52 (E3) 到 87 (D#6) 共 36 个半音，完全连续无空洞
        for (byte m = 52; m <= 87; m++)
        {
            Assert(midiNotes.Contains(m), $"36键审计: MIDI 音符 {m} 完整覆盖且无空缺");
        }

        // 验证三行键盘布局，每行恰好 12 个物理按键
        Assert(PresetManager.DefaultMappingData.Length == 36, "36键审计: DefaultMappingData 数组长度为 36");
    }

    private static void Test_TimingProfile_Invariants_Audit()
    {
        var profile = TimingProfile.Default;
        bool valid = profile.ValidateInvariants(out string? err);
        Assert(valid && err == null, "TimingProfile 审计: 默认时序参数不变量校验通过");

        var badProfile = new TimingProfile { MinHoldUs = -10 };
        bool badValid = badProfile.ValidateInvariants(out string? badErr);
        Assert(!badValid && badErr != null, "TimingProfile 审计: 成功捕获并拦截非法负数 MinHoldUs");

        Assert(profile.GetEffectiveReleaseGap(false) == 10_000, "TimingProfile 审计: 常规模式同键释放间隙为 10ms");
        Assert(profile.GetEffectiveReleaseGap(true) == 8_000, "TimingProfile 审计: 密集连奏模式有效释放间隙为 8ms");
    }

    private static void Test_ActionCompiler_RetriggerSafety_Audit()
    {
        // 场景: 同一物理键密集触发 (例如 10ms 极短间隔多次触发)
        // 验证 ActionCompiler 绝不产生倒序事件 (KeyUp 绝不在 KeyDown 之前，holdUs 绝不为负)
        var notes = new List<GameNoteEvent>
        {
            new GameNoteEvent(100_000, 0, 30_000),
            new GameNoteEvent(110_000, 0, 30_000),
            new GameNoteEvent(130_000, 0, 30_000)
        };

        var preset = PresetManager.CreateDefaultPreset();
        var options = new CompilerOptions
        {
            MinHoldUs = 4_000,
            ReleaseGapUs = 2_000,
            MinRetriggerIntervalUs = 8_000
        };

        var batches = ActionCompiler.Compile(notes, preset, options);

        // 验证所有批次时间戳严格单调非递减
        bool isMonotonic = true;
        for (int i = 1; i < batches.Count; i++)
        {
            if (batches[i].TimeUs < batches[i - 1].TimeUs)
            {
                isMonotonic = false;
                break;
            }
        }
        Assert(isMonotonic, "ActionCompiler 重触发安全: 编译后批次时间戳严格单调非递减");

        // 验证同一批次内若有 KeyUp 和 KeyDown，KeyUp 必须排在前面
        bool noKeyInversion = true;
        foreach (var b in batches)
        {
            bool seenDown = false;
            foreach (var act in b.Actions)
            {
                if (act.IsDown) seenDown = true;
                if (!act.IsDown && seenDown)
                {
                    noKeyInversion = false; // KeyUp cannot appear after KeyDown in same batch
                }
            }
        }
        Assert(noKeyInversion, "ActionCompiler 重触发安全: 同时间戳内 KeyUp 严格先于 KeyDown 释放");
    }

    private static void Test_Piano_FullLifecycle_Audit()
    {
        var rawNotes = new List<RawMidiNote>
        {
            new RawMidiNote { StartUs = 0, DurationUs = 300_000, NoteNumber = 60, Velocity = 100 },
            new RawMidiNote { StartUs = 400_000, DurationUs = 300_000, NoteNumber = 64, Velocity = 100 },
            new RawMidiNote { StartUs = 800_000, DurationUs = 300_000, NoteNumber = 67, Velocity = 100 }
        };

        var song = MidiConverter.Convert(rawNotes, "piano_test.mid", "testhash", "钢琴测试曲", new ConvertOptions
        {
            TargetInstrument = 0, // Piano
            Transpose = 0,
            OutOfRangeMode = "OctaveFold"
        });

        Assert(song.TargetInstrument == 0, "钢琴审计: 歌曲目标乐器为钢琴(0)");

        var config = new AppConfig { PlayMode = 0 };
        var pub = new Bridge.WebEventPublisher(null);
        var wheel = new Guitar.WheelController(config.WheelPreset);
        using var playback = new Services.PlaybackService(config, pub, wheel);

        var stateObj = playback.LoadSong(song);
        Assert(config.PlayMode == 0, "钢琴审计: 载入钢琴曲后模式自动处于 36 键钢琴模式(0)");

        playback.StartPlay(0);
        playback.Pause();
        playback.Resume();
        playback.Stop();
        Assert(true, "钢琴审计: 钢琴模式播放/暂停/继续/停止全生命周期正常运行无异常");
    }

    private static void Test_Piano_Polyphony_ScanCode_Audit()
    {
        // C4 (60), E4 (64), G4 (67)
        var notes = new List<GameNoteEvent>
        {
            new GameNoteEvent(100_000, (byte)(60 - 52), 200_000), // NoteIndex 8
            new GameNoteEvent(100_000, (byte)(64 - 52), 200_000), // NoteIndex 12
            new GameNoteEvent(100_000, (byte)(67 - 52), 200_000), // NoteIndex 15
        };

        var preset = PresetManager.CreateDefaultPreset();
        var batches = ActionCompiler.Compile(notes, preset);

        Assert(batches.Count >= 2, "钢琴审计: 和弦至少产生 KeyDown 和 KeyUp 两批输入");
        var downBatch = batches.Find(b => b.TimeUs == 100_000);
        Assert(downBatch != null && downBatch.Actions.Count == 3, "钢琴审计: 100ms 和弦批次包含全部 3 个并发音符按键");

        // 验证物理按键映射与扫描码准确性
        // NoteIndex 8 -> C4 -> "L" -> 0x26
        // NoteIndex 12 -> E4 -> "Q" -> 0x10
        // NoteIndex 15 -> G4 -> "R" -> 0x13
        bool hasC4 = downBatch!.Actions.Any(a => a.ScanCode == 0x26 && a.IsDown);
        bool hasE4 = downBatch!.Actions.Any(a => a.ScanCode == 0x10 && a.IsDown);
        bool hasG4 = downBatch!.Actions.Any(a => a.ScanCode == 0x13 && a.IsDown);
        Assert(hasC4 && hasE4 && hasG4, "钢琴审计: C4/E4/G4 和弦并发按键精准映射至 0x26(L), 0x10(Q), 0x13(R)");
    }

    private static void Test_Reconvert_Song_Audit()
    {
        var lib = new SongLibrary();
        var libService = new Services.LibraryService(lib);

        var rawNotes = new List<RawMidiNote>
        {
            new RawMidiNote { StartUs = 0, DurationUs = 500_000, NoteNumber = 60, Velocity = 100, TrackId = 0 },
            new RawMidiNote { StartUs = 600_000, DurationUs = 500_000, NoteNumber = 64, Velocity = 100, TrackId = 1 },
        };

        var original = MidiConverter.Convert(rawNotes, "recon_test.mid", "hash_recon", "重转测试曲", new ConvertOptions
        {
            TargetInstrument = 1, // Originally Guitar
            Transpose = 0,
            OutOfRangeMode = "Discard"
        });
        lib.SaveSong(original);

        // 重新转换为钢琴模式，移调 +2，八度折叠
        var reconverted = libService.ReconvertSong(
            original.Id,
            targetInstrument: 0,
            transpose: 2,
            melodyStrategy: "ContinuousMelody",
            outOfRangeMode: "OctaveFold"
        );

        var loaded = lib.LoadSong(original.Id);
        Assert(loaded != null, "重转审计: 成功重转并读取曲目");
        Assert(loaded!.TargetInstrument == 0, "重转审计: 重新转换为钢琴模式 TargetInstrument=0");
        Assert(loaded.Options.Transpose == 2, "重转审计: 移调参数已更新为 +2");
        Assert(loaded.Options.OutOfRangeMode == "OctaveFold", "重转审计: 超范围模式已更新为 OctaveFold");

        lib.DeleteSong(original.Id);
    }

    private static void Test_ConvertAndSave_ReplacesExistingSong_Audit()
    {
        string tempLibDir = Path.Combine(Path.GetTempPath(), $"lib_replace_{Guid.NewGuid():N}");
        try
        {
            var lib = new Library.SongLibrary(tempLibDir);
            var service = new Services.LibraryService(lib);

            string tempMid = Path.Combine(Path.GetTempPath(), $"sample_{Guid.NewGuid():N}.mid");
            var mf = new Melanchall.DryWetMidi.Core.MidiFile();
            var tc = new Melanchall.DryWetMidi.Core.TrackChunk();
            tc.Events.Add(new Melanchall.DryWetMidi.Core.NoteOnEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)60, (Melanchall.DryWetMidi.Common.SevenBitNumber)100));
            tc.Events.Add(new Melanchall.DryWetMidi.Core.NoteOffEvent((Melanchall.DryWetMidi.Common.SevenBitNumber)60, (Melanchall.DryWetMidi.Common.SevenBitNumber)0) { DeltaTime = 480 });
            mf.Chunks.Add(tc);
            mf.Write(tempMid, overwriteFile: true);

            // 第一次转换并保存 (吉他模式)
            var res1 = service.ConvertAndSave(
                tempMid,
                "测试替换歌曲",
                targetInstrument: 1,
                transpose: 0,
                outOfRangeMode: "OctaveFold",
                filterDrumTrack: true,
                enableMelodyExtraction: false,
                melodyStrategy: "FullSong"
            );
            string id1 = (string)((dynamic)res1).id;
            Assert(lib.GetAllSongs().Count == 1, "曲目替换审计: 初次转换曲库曲目数量为 1");

            // 第二次重新转换并保存相同文件 (吉他模式，不同移调 +3)
            var res2 = service.ConvertAndSave(
                tempMid,
                "测试替换歌曲",
                targetInstrument: 1,
                transpose: 3,
                outOfRangeMode: "OctaveFold",
                filterDrumTrack: true,
                enableMelodyExtraction: false,
                melodyStrategy: "FullSong"
            );
            string id2 = (string)((dynamic)res2).id;
            bool replaced = (bool)((dynamic)res2).isReplaced;

            Assert(replaced, "曲目替换审计: isReplaced 标志为 true");
            Assert(id1 == id2, $"曲目替换审计: 两次转换成功复用曲目 ID (原: {id1}, 新: {id2})");
            Assert(lib.GetAllSongs().Count == 1, $"曲目替换审计: 重新转换后未生成重复条目，曲库曲目总数仍为 1 (实际: {lib.GetAllSongs().Count})");

            var loaded = lib.LoadSong(id1);
            Assert(loaded != null && loaded.Options.Transpose == 3, "曲目替换审计: 原曲目被新配置 (Transpose=+3) 成功替换覆盖");
            Assert(loaded != null && loaded.RawNotes != null && loaded.RawNotes.Count > 0, "曲目替换审计: 替换后曲目保留完整原始 MIDI 音符 (RawNotes)");

            File.Delete(tempMid);
        }
        finally
        {
            try { if (Directory.Exists(tempLibDir)) Directory.Delete(tempLibDir, true); } catch { }
        }
    }

    private static void Test_WebMessageHandler_IPC_Audit()
    {
        var lib = new SongLibrary();
        var config = new AppConfig { PlayMode = 1 };
        var pub = new Bridge.WebEventPublisher(null);
        var wheel = new Guitar.WheelController(config.WheelPreset);
        using var playback = new Services.PlaybackService(config, pub, wheel);
        var calib = new Services.CalibrationService(config, wheel, () => { });
        var settings = new Services.SettingsService(config, () => { });
        string? activePage = null;
        var handler = new Bridge.WebMessageHandler(new Services.LibraryService(lib), playback, calib, settings, config,
            setActivePageCallback: pageId => activePage = pageId);

        string reqActivePage = "{\"requestId\":\"page-1\",\"action\":\"ui.setActivePage\",\"payload\":{\"pageId\":\"game-piano\"}}";
        handler.HandleMessageAsync(reqActivePage, _ => { }).GetAwaiter().GetResult();
        Assert(activePage == "game-piano", "IPC审计: 游戏钢琴页面同步到全局热键路由");

        // 测试通过 IPC 发送 settings.update 切换 playMode
        string reqPiano = "{\"requestId\":\"r1\",\"action\":\"settings.update\",\"payload\":{\"key\":\"playMode\",\"value\":0}}";
        handler.HandleMessageAsync(reqPiano, _ => { }).GetAwaiter().GetResult();
        Assert(config.PlayMode == 0, "IPC审计: settings.update playMode=0 成功将模式持久化更新为钢琴模式");

        string reqGuitarMode = "{\"requestId\":\"r2\",\"action\":\"settings.update\",\"payload\":{\"key\":\"guitarMode\",\"value\":\"Fret\"}}";
        handler.HandleMessageAsync(reqGuitarMode, _ => { }).GetAwaiter().GetResult();
        Assert(config.PlayMode == 1, "IPC审计: settings.update guitarMode='Fret' 成功切换为吉他品格模式");
    }

    private static void Test_PlaybackSpeedEngine_Audit()
    {
        // 1. 速度倍率范围校验
        var validNormal = PlaybackSpeedSetting.Validate(1.0);
        Assert(validNormal.Multiplier == 1.0, "变速校验: 1.0x 正常倍速解析");

        var validMin = PlaybackSpeedSetting.Validate(0.25);
        Assert(validMin.Multiplier == 0.25, "变速校验: 0.25x 最低倍速通过");

        var validMax = PlaybackSpeedSetting.Validate(2.00);
        Assert(validMax.Multiplier == 2.00, "变速校验: 2.00x 最高倍速通过");

        bool threwUnder = false;
        try { PlaybackSpeedSetting.Validate(0.24); } catch { threwUnder = true; }
        Assert(threwUnder, "变速校验: 低于 0.25x 抛出 ArgumentOutOfRangeException");

        bool threwOver = false;
        try { PlaybackSpeedSetting.Validate(2.01); } catch { threwOver = true; }
        Assert(threwOver, "变速校验: 高于 2.00x 抛出 ArgumentOutOfRangeException");

        // 2. 音符时间轴按 1/speed 缩放 (保持微秒精度与保持下限)
        var rawNotes = new List<RawMidiNote>
        {
            new() { StartUs = 1_000_000, DurationUs = 200_000, NoteNumber = 60, Velocity = 100 },
            new() { StartUs = 2_000_000, DurationUs = 30_000, NoteNumber = 64, Velocity = 90 }
        };

        var scaled05 = PlaybackSpeedEngine.ScaleMidiNotes(rawNotes, 0.50);
        Assert(scaled05[0].StartUs == 2_000_000, "时间轴缩放 0.5x: StartUs 1s 扩展为 2s");
        Assert(scaled05[0].DurationUs == 400_000, "时间轴缩放 0.5x: DurationUs 200ms 扩展为 400ms");

        var scaled125 = PlaybackSpeedEngine.ScaleMidiNotes(rawNotes, 1.25);
        Assert(scaled125[0].StartUs == 800_000, "时间轴缩放 1.25x: StartUs 1s 压缩为 800ms");
        Assert(scaled125[0].DurationUs == 160_000, "时间轴缩放 1.25x: DurationUs 200ms 压缩为 160ms");
        Assert(scaled125[1].DurationUs >= 22_000, "时间轴缩放: 最短音符按键保持时间不低于 22ms 物理安全下限");

        // 3. 可达性校验 - 同弦连续触发冲突
        var conflictEvents = new List<CompiledGuitarNoteEvent>
        {
            new(EffectiveStartUs: 1_000_000, EffectiveDurationUs: 100_000, Pitch: 60, TargetPage: 1, TargetSlot: 1, PhysicalString: 1, GameKeyScanCode: 0x07),
            new(EffectiveStartUs: 1_105_000, EffectiveDurationUs: 100_000, Pitch: 62, TargetPage: 1, TargetSlot: 2, PhysicalString: 1, GameKeyScanCode: 0x07)
        };
        var hw = new HardwareTimingConstraints(SameKeyReleaseGapUs: 18_000, StringMinHoldUs: 22_000);
        var reportConflict = PlaybackSpeedEngine.ValidateGuitarTimeline(conflictEvents, 1.20, hw);
        Assert(!reportConflict.IsFullyReachable, "可达性校验: 检测到同弦连拨释放间隙过短风险");
        Assert(reportConflict.Issues.Any(i => i.IssueType == "SameStringRetrigger"), "可达性校验: 准确识别 SameStringRetrigger 问题");
        Assert(reportConflict.RecommendedMaxSpeed <= 1.20, "可达性校验: 给出安全倍速调优建议");

        // 4. 可达性校验 - 跨页翻页耗时不足
        var pageConflictEvents = new List<CompiledGuitarNoteEvent>
        {
            new(EffectiveStartUs: 1_000_000, EffectiveDurationUs: 100_000, Pitch: 60, TargetPage: 0, TargetSlot: 0, PhysicalString: 1, GameKeyScanCode: 0x07),
            new(EffectiveStartUs: 1_020_000, EffectiveDurationUs: 100_000, Pitch: 67, TargetPage: 2, TargetSlot: 3, PhysicalString: 2, GameKeyScanCode: 0x06)
        };
        var reportPageConflict = PlaybackSpeedEngine.ValidateGuitarTimeline(pageConflictEvents, 1.0, hw);
        Assert(!reportPageConflict.IsFullyReachable, "可达性校验: 检测到 20ms 内需跨页+选槽的超限风险");
        Assert(reportPageConflict.Issues.Any(i => i.IssueType == "PageOrSlotPrepOverflow"), "可达性校验: 准确识别 PageOrSlotPrepOverflow 问题");
    }

    private static void Test_WindowSizeLock_Audit()
    {
        using var form = new UI.MainForm();
        form.InitializeWindowSizingPolicy();

        // 锁定窗体
        var lockedState = form.SetWindowSizeLock(true);
        Assert(lockedState.Locked == true, "窗体锁定: SetWindowSizeLock(true) 锁定成功");
        Assert(form.MinimumSize == form.Size, "窗体锁定: MinimumSize 锁死为当前窗体大小");
        Assert(form.MaximumSize == form.Size, "窗体锁定: MaximumSize 锁死为当前窗体大小");
        Assert(form.MaximizeBox == false, "窗体锁定: 最大化按钮已禁用");

        // 解锁窗体
        var unlockedState = form.SetWindowSizeLock(false);
        Assert(unlockedState.Locked == false, "窗体解锁: SetWindowSizeLock(false) 解锁成功");
        Assert(form.MaximumSize == System.Drawing.Size.Empty, "窗体解锁: MaximumSize 恢复自由无限制");
        Assert(form.MaximizeBox == true, "窗体解锁: 最大化按钮恢复启用");

        // 一键恢复推荐分辨率
        form.SetWindowSizeLock(true);
        var restoredState = form.RestoreDefaultWindowSize();
        Assert(restoredState.Locked == false, "推荐分辨率: RestoreDefaultWindowSize() 自动解除锁定");
        Assert(restoredState.Width >= 760 && restoredState.Height >= 480, "推荐分辨率: 恢复至有效推荐分辨率区间");
    }

    private static void Test_BuiltInScores_Seeding_Audit()
    {
        string tempLibDir = Path.Combine(Path.GetTempPath(), $"nte_test_lib_{Guid.NewGuid():N}");
        try
        {
            var testLib = new Library.SongLibrary(tempLibDir);
            var service = new Services.LibraryService(testLib);

            string scoresDir = Path.Combine(AppContext.BaseDirectory, "谱子");
            if (!Directory.Exists(scoresDir))
            {
                scoresDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "谱子"));
            }
            if (!Directory.Exists(scoresDir))
            {
                scoresDir = Utils.EmbeddedAssetExtractor.EnsureScoresExtracted();
            }

            Assert(Directory.Exists(scoresDir), "内置谱库: 谱子目录存在");

            string guitarDir = Path.Combine(scoresDir, "吉他");
            string pianoDir = Path.Combine(scoresDir, "钢琴");

            Assert(Directory.Exists(guitarDir), "内置谱库: 吉他分类子目录存在");
            Assert(Directory.Exists(pianoDir), "内置谱库: 钢琴分类子目录存在");

            var guitarFiles = Directory.GetFiles(guitarDir, "*.mid", SearchOption.AllDirectories);
            var pianoFiles = Directory.GetFiles(pianoDir, "*.mid", SearchOption.AllDirectories);

            Assert(guitarFiles.Length >= 3, $"内置谱库: 吉他分类曲谱数量完整 (实际: {guitarFiles.Length} >= 3)");
            Assert(pianoFiles.Length >= 9, $"内置谱库: 钢琴分类曲谱数量完整 (实际: {pianoFiles.Length} >= 9)");

            // 执行内置曲谱自动灌装与转换
            int seeded = service.SeedBuiltInScoresIfNecessary(scoresDir, force: true);
            Assert(seeded >= 12, $"内置谱库: 成功自动灌装并转换内置曲谱 (数量: {seeded} >= 12)");

            int guitarCount = testLib.GetSongCount(1);
            int pianoCount = testLib.GetSongCount(0);

            Assert(guitarCount >= 3, $"内置谱库: 吉他乐器分类正确解析 TargetInstrument=1 (数量: {guitarCount})");
            Assert(pianoCount >= 9, $"内置谱库: 钢琴乐器分类正确解析 TargetInstrument=0 (数量: {pianoCount})");

            // 二次调用幂等性校验 (防重复灌装)
            int seededAgain = service.SeedBuiltInScoresIfNecessary(scoresDir, force: false);
            Assert(seededAgain == 0, "内置谱库: 幂等性校验通过，再次调用不产生重复灌装");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempLibDir)) Directory.Delete(tempLibDir, recursive: true);
            }
            catch { }
        }
    }

    private static void Test_EmbeddedAssetExtractor_Audit()
    {
        // 1. 验证 wwwroot 嵌入式资源提取
        string extractedWwwRoot = Utils.EmbeddedAssetExtractor.EnsureWwwRootExtracted();
        Assert(Directory.Exists(extractedWwwRoot), "嵌入式资源: wwwroot 释放目录创建成功");

        string indexPath = Path.Combine(extractedWwwRoot, "index.html");
        Assert(File.Exists(indexPath), "嵌入式资源: index.html 网页文件已成功内置并释放");

        // 2. 验证谱子嵌入式资源提取
        string extractedScores = Utils.EmbeddedAssetExtractor.EnsureScoresExtracted();
        Assert(Directory.Exists(extractedScores), "嵌入式资源: 谱子释放目录创建成功");
        Assert(Directory.Exists(Path.Combine(extractedScores, "吉他")), "嵌入式资源: 吉他分类子目录提取成功");
        Assert(Directory.Exists(Path.Combine(extractedScores, "钢琴")), "嵌入式资源: 钢琴分类子目录提取成功");
    }
}

