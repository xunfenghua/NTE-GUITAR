using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using MidiAutoPlayer.Midi;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Guitar;
using MidiAutoPlayer.Recognition;

namespace MidiAutoPlayer.Guitar;

public static class GuitarDiagnostics
{
    public static int Run()
    {
        Console.WriteLine("==================================================");
        Console.WriteLine("=== NTE-GUITAR 深度算法排查诊断报告 ===");
        Console.WriteLine("==================================================");

        string guitarDir = Path.Combine(AppContext.BaseDirectory, "谱子", "吉他");
        var files = Directory.Exists(guitarDir) ? Directory.GetFiles(guitarDir, "*.mid").ToList() : new List<string>();
        string sampleMid = Path.Combine(AppContext.BaseDirectory, "Resources", "示例曲目_小星星.mid");
        if (File.Exists(sampleMid)) files.Add(sampleMid);

        foreach (var file in files)
        {
            DiagnoseFile(file);
        }

        return 0;
    }

    private static void DiagnoseFile(string filePath)
    {
        string fileName = Path.GetFileName(filePath);
        Console.WriteLine($"\n>>>>>>>>>> [曲目分析]: {fileName} <<<<<<<<<<");
        var (hash, rawNotes, trackIds, trackMetas) = MidiLoader.Load(filePath);
        Console.WriteLine($"原始音符数: {rawNotes.Count}, 音轨数: {trackMetas.Count}");

        foreach (var t in trackMetas)
        {
            Console.WriteLine($"  - Track {t.TrackId}: \"{t.Name}\" | 音符: {t.NoteCount} | 声道: [{string.Join(",", t.Channels)}] | 音域: {t.MinPitch}..{t.MaxPitch} | 乐器: {t.Program}");
        }

        // 1. 测试各旋律提取模式的音符数量与音域
        var allCandidates = RecognitionService.GenerateAllCandidates(rawNotes, trackMetas);
        Console.WriteLine("\n[1. 识别模式输出对比]:");
        foreach (var kvp in allCandidates)
        {
            var res = kvp.Value;
            int minP = res.Notes.Count > 0 ? res.Notes.Min(n => (int)n.NoteNumber) : 0;
            int maxP = res.Notes.Count > 0 ? res.Notes.Max(n => (int)n.NoteNumber) : 0;
            Console.WriteLine($"  * 模式 {kvp.Key} ({res.Name}): {res.Notes.Count} 音符, 音域: {minP}..{maxP}, 评分: {res.MelodyScore}, 摘要: {res.Summary}");
        }

        // 2. 对每种输入源测试 Fret 独奏模式
        Console.WriteLine("\n[2. 品格独奏模式 (Fret Solo) 深度转换分析]:");
        TestFretConversion("FullSong (全曲音符)", rawNotes);
        if (allCandidates.TryGetValue(RecognitionMode.ContinuousMelody, out var cm) && cm.Notes.Count > 0)
        {
            TestFretConversion("ContinuousMelody (连续旋律)", cm.Notes);
        }
        if (allCandidates.TryGetValue(RecognitionMode.HighestNote, out var hn) && hn.Notes.Count > 0)
        {
            TestFretConversion("HighestNote (最高音旋律)", hn.Notes);
        }

        // 3. 测试 Chord 和弦模式
        Console.WriteLine("\n[3. 和弦伴奏模式 (Chord Mode) 深度转换分析]:");
        TestChordConversion(rawNotes);
    }

    private static void TestFretConversion(string sourceLabel, IReadOnlyList<RawMidiNote> notes)
    {
        Console.WriteLine($"\n  --- 测试输入源: {sourceLabel} (输入 {notes.Count} 音符) ---");
        var tuning = GuitarTuning.Standard;

        // A. 移调分析
        int bestShift = FretConverter.FindBestGlobalOctaveShift(notes, tuning);
        Console.WriteLine($"    [移调决策]: FindBestGlobalOctaveShift = {bestShift} (移调 {(bestShift >= 0 ? "+" : "")}{bestShift} 半音 / {bestShift / 12} 八度)");

        // B. Sanitize 分析
        var cleanNotes = FretConverter.SanitizeForFretSolo(notes, isMonophonic: false);
        Console.WriteLine($"    [并发去重]: SanitizeForFretSolo 产出 {cleanNotes.Count} 音符 (被去重/丢弃 {notes.Count - cleanNotes.Count} 音符, 保留率 {(cleanNotes.Count * 100.0 / Math.Max(1, notes.Count)):F1}%)");

        // C. Fret 转换
        var fretEvents = FretConverter.Convert(notes, tuning);
        int skipped = fretEvents.Count(e => e.IsSkipped);
        int conflicts = fretEvents.Count(e => e.IsTimingConflict);
        Console.WriteLine($"    [DP指法规划]: 生成 {fretEvents.Count} 事件 | 跳过(不可奏/越界): {skipped} | 时序冲突: {conflicts}");

        // 统计页面使用分布
        var pageGroups = fretEvents.Where(e => !e.IsSkipped).GroupBy(e => e.Page).OrderBy(g => g.Key);
        string pageDist = string.Join(", ", pageGroups.Select(g => $"P{g.Key}:{g.Count()}次"));
        Console.WriteLine($"    [轮盘页面分布]: {pageDist}");

        // 统计琴弦使用分布
        var stringGroups = fretEvents.Where(e => !e.IsSkipped).GroupBy(e => e.StringNo).OrderBy(g => g.Key);
        string stringDist = string.Join(", ", stringGroups.Select(g => $"{g.Key}弦(键{GameScanCodes.GetGameDigit(g.Key)}):{g.Count()}次"));
        Console.WriteLine($"    [琴弦按键分布]: {stringDist}");

        // 统计翻页和切品频率
        int pageSwitches = 0;
        int slotSwitches = 0;
        for (int i = 1; i < fretEvents.Count; i++)
        {
            if (fretEvents[i].IsSkipped || fretEvents[i - 1].IsSkipped) continue;
            if (fretEvents[i].Page != fretEvents[i - 1].Page) pageSwitches++;
            else if (fretEvents[i].Slot != fretEvents[i - 1].Slot) slotSwitches++;
        }
        Console.WriteLine($"    [操作动作开销]: 跨页翻页次数={pageSwitches}, 同页切槽次数={slotSwitches}");

        // D. 动作编译与时序检验
        var preset = new WheelPreset();
        var actions = GuitarCompiler.CompileFretEvents(fretEvents, preset);
        int navCount = actions.Count(a => a.Type == GuitarActionType.PageNav);
        int wheelCount = actions.Count(a => a.Type == GuitarActionType.WheelSelect);
        int stringDownCount = actions.Count(a => a.Type == GuitarActionType.StringDown);
        Console.WriteLine($"    [编译器动作]: 总动作数={actions.Count} | 翻页Nav={navCount} | 选槽Wheel={wheelCount} | 拨弦StringDown={stringDownCount}");

        // 打印前 10 个有效事件详情
        Console.WriteLine("    [前 10 个事件详情采样]:");
        for (int i = 0; i < Math.Min(10, fretEvents.Count); i++)
        {
            var e = fretEvents[i];
            int key = GameScanCodes.GetGameDigit(e.StringNo);
            Console.WriteLine($"      #{i + 1:D2} [{e.TimeUs / 1000}ms] Note={e.NoteNumber} -> 物理{e.StringNo}弦(按键{key}), {e.Fret}品(P{e.Page} S{e.Slot}) Skip={e.IsSkipped} Conflict={e.IsTimingConflict}");
        }
    }

    private static void TestChordConversion(IReadOnlyList<RawMidiNote> notes)
    {
        var detailedEvents = ChordAnalyzer.AnalyzeDetailed(notes);
        Console.WriteLine($"    [和弦事件数]: {detailedEvents.Count}");
        int unknownCount = detailedEvents.Count(e => e.Chord == ChordId.Unknown);
        Console.WriteLine($"    [和弦识别未知数 (Unknown)]: {unknownCount} (占比 {(unknownCount * 100.0 / Math.Max(1, detailedEvents.Count)):F1}%)");

        var chordFreq = detailedEvents.Where(e => e.Chord != ChordId.Unknown)
                                      .GroupBy(e => e.Chord)
                                      .OrderByDescending(g => g.Count())
                                      .Take(6);
        string topChords = string.Join(", ", chordFreq.Select(g => $"{g.Key}:{g.Count()}次"));
        Console.WriteLine($"    [高频和弦]: {topChords}");

        var preset = new WheelPreset();
        var chordActions = GuitarCompiler.CompileChordDetailedEvents(detailedEvents, preset);
        int strumClicks = chordActions.Count(a => a.Type == GuitarActionType.StrumClick);
        int wheelSelects = chordActions.Count(a => a.Type == GuitarActionType.WheelSelect);
        int stringDowns = chordActions.Count(a => a.Type == GuitarActionType.StringDown);
        Console.WriteLine($"    [和弦编译动作]: 选槽Wheel={wheelSelects} | 扫弦Strum={strumClicks} | 拨弦Pluck={stringDowns}");

        // 打印前 8 个和弦事件采样
        Console.WriteLine("    [前 8 个和弦采样]:");
        for (int i = 0; i < Math.Min(8, detailedEvents.Count); i++)
        {
            var d = detailedEvents[i];
            Console.WriteLine($"      #{i + 1:D2} [{d.StartUs / 1000}ms] 和弦: {d.Chord} (P{d.Page} S{d.Slot}) | 形态: {d.Articulation} | 方向: {d.Direction} | 置信: {d.Confidence:P0}");
        }
    }
}
