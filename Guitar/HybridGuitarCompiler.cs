using MidiAutoPlayer.Models;
using MidiAutoPlayer.Midi;
using MidiAutoPlayer.Recognition;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MidiAutoPlayer.Guitar
{
    public enum GuitarPlayModeType : byte
    {
        Fret = 0,  // 品格模式 (P1~P5: 1~24品 + P5空弦)
        Chord = 1  // 和弦模式 (P1~P4: 24个预置和弦)
    }

    public sealed class HybridCompilerOptions
    {
        /// <summary>起始模式（默认品格模式 P1）</summary>
        public GuitarPlayModeType InitialMode { get; set; } = GuitarPlayModeType.Fret;

        /// <summary>起始页码（默认 P1）</summary>
        public byte InitialPage { get; set; } = 1;
        public ChordRecognitionMode ChordMode { get; set; } = ChordRecognitionMode.Loose;
        public GuitarPlayStyle PlayStyle { get; set; } = GuitarPlayStyle.Hybrid;
        public AutoStrumMode AutoStrum { get; set; } = AutoStrumMode.Auto;
        public bool AvoidStrum { get; set; }

        /// <summary>模式切换键 ScanCode (Z 键 = 0x2C)</summary>
        public ushort ModeSwitchScanCode { get; set; } = 0x2C;

        /// <summary>上一页 ScanCode (R 键 = 0x13)</summary>
        public ushort PagePrevScanCode { get; set; } = 0x13;

        /// <summary>下一页 ScanCode (T 键 = 0x14)</summary>
        public ushort PageNextScanCode { get; set; } = 0x14;

        /// <summary>游戏硬件要求的最小按键按压保持时长：20ms (20,000us)</summary>
        public long MinKeyHoldUs { get; set; } = 20_000;

        /// <summary>默认点按琴弦脉冲时长：25ms (>= 20ms)</summary>
        public long StringTapDurationUs { get; set; } = 25_000;

        /// <summary>同键连按最小释放间隔：22ms</summary>
        public long ReleaseGapUs { get; set; } = 22_000;

        /// <summary>两次控制键（Z / R / T）之间的最小释放间隔：15ms</summary>
        public long ControlInterTapGapUs { get; set; } = 15_000;

        /// <summary>鼠标点击轮盘选槽后的最小稳定时间：55ms</summary>
        public long WheelMouseSettleUs { get; set; } = 55_000;

        /// <summary>鼠标左键 = 上扫音 (依实机截图设定)</summary>
        public MouseButton UpStrumMouseButton { get; set; } = MouseButton.Left;

        /// <summary>鼠标右键 = 下扫音 (依实机截图设定)</summary>
        public MouseButton DownStrumMouseButton { get; set; } = MouseButton.Right;
    }

    /// <summary>
    /// 混合演奏 DP 状态节点：同时表征品格或和弦状态
    /// </summary>
    public sealed record HybridStateCandidate
    {
        public GuitarPlayModeType Mode { get; init; }
        public byte Page { get; init; }           // Fret: 1..5, Chord: 1..4
        public byte Slot { get; init; }           // 1..6
        public byte Fret { get; init; }           // Fret 模式下 0..24
        public ChordId Chord { get; init; }       // Chord 模式下生效的和弦
        public ChordArticulation Articulation { get; init; }
        public StrumDirectionHint StrumDirection { get; init; }
        public IReadOnlyList<(byte StringNo, byte NoteNumber, long DurationUs)> StringNotes { get; init; }
            = Array.Empty<(byte, byte, long)>();
        public double LocalCost { get; init; }
        public bool IsHoldPositionFallback { get; init; }
    }

    public static class HybridGuitarCompiler
    {
        /// <summary>
        /// 修正后的品格页码换算：
        /// 1~24 品 -> P1~P4 (每页 6 品)
        /// 0 品 (空弦) -> P5 / S1 (第五页第 1 槽位，符合“从1开始、0结束”的 5 页轮盘设计)
        /// </summary>
        public static (byte Page, byte Slot) GetFretPageAndSlot(byte fret)
        {
            if (fret == 0)
            {
                return (5, 1); // P5 第五页：0 品空弦
            }

            byte page = (byte)(((fret - 1) / 6) + 1); // 1..4
            byte slot = (byte)(((fret - 1) % 6) + 1); // 1..6
            return (page, slot);
        }

        /// <summary>
        /// 完整弹奏模式（品格+和弦自动来回切换）总入口：
        /// 1. 自动计算满足 >=20ms 的配速（或跟随用户设定速度）
        /// 2. 识别全曲和声与自动半音移调
        /// 3. 使用全局 Viterbi 动态规划求解最佳【品格 <-> 和弦】切换路径
        /// 4. 编译生成带 Z 键切换、P1 重置、R/T 翻页、轮盘选槽、左右键上下扫弦及数字键拨弦的动作序列
        /// </summary>
        public static List<GuitarAction> CompileFullPerformance(
            IReadOnlyList<RawMidiNote> rawMidiNotes,
            SpeedControlManager speedManager,
            WheelPreset preset,
            HybridCompilerOptions? options = null,
            GuitarTuning? tuning = null)
        {
            if (rawMidiNotes == null || rawMidiNotes.Count == 0)
                return new List<GuitarAction>();

            options ??= new HybridCompilerOptions();
            options.MinKeyHoldUs = Math.Max(20_000, options.MinKeyHoldUs);
            options.ReleaseGapUs = Math.Max(22_000, options.ReleaseGapUs);
            options.ControlInterTapGapUs = Math.Max(15_000, options.ControlInterTapGapUs);
            options.WheelMouseSettleUs = Math.Max(55_000, options.WheelMouseSettleUs);
            tuning ??= GuitarTuning.Standard;

            var taggedNotes = TagPrimaryMelody(rawMidiNotes);
            var cleanNotes = MidiPolyphonyReducer.ReduceForGuitar(taggedNotes);
            if (cleanNotes.Count == 0)
                return new List<GuitarAction>();

            // 1. 自动变速计算（若用户未手动改速，则自动确保按键时长 >= 20ms）
            int autoSpeed = MidiSpeedOptimizer.CalculateAutoSpeedPercent(cleanNotes, new MidiSpeedOptimizerOptions
            {
                MinKeyHoldUs = options.MinKeyHoldUs,
                MinKeyReleaseGapUs = options.ReleaseGapUs,
                WheelMouseSettleUs = options.WheelMouseSettleUs
            });
            speedManager.ApplyAutoSpeedOnMidiImport(autoSpeed, resetUserOverride: false);

            var scaledNotes = MidiSpeedOptimizer.ScaleMidiNotes(
                cleanNotes,
                speedManager.TimeScaleFactor,
                options.MinKeyHoldUs);

            // 2. 运行 V2 和弦识别（获取自动半音移调与时间窗和弦序列）
            long rollWindowUs = (long)Math.Round((options.AvoidStrum ? 65_000 : 30_000) * speedManager.TimeScaleFactor);
            var chordTimeline = ChordAnalyzerV2.AnalyzeDetailed(
                scaledNotes,
                out int semitoneShift,
                new ChordAnalyzerV2Options { EnableAutoTranspose = true, Mode = options.ChordMode,
                    AttackClusterUs = rollWindowUs });

            var alignedNotes = AlignPlayableNotes(scaledNotes, semitoneShift, tuning);

            if (alignedNotes.Count == 0)
                return new List<GuitarAction>();

            // 3. Group short rolls across playable strings into one attack.
            var frames = BuildOnsetFrames(alignedNotes, rollWindowUs, tuning, options.AvoidStrum,
                (long)Math.Round(25_000 * speedManager.TimeScaleFactor));

            // 4. 为每一帧构建【品格模式候选】与【和弦模式候选】
            var frameCandidates = new List<List<HybridStateCandidate>>(frames.Count);
            int activeChordIndex = -1;
            for (int i = 0; i < frames.Count; i++)
            {
                var frame = frames[i];
                while (activeChordIndex + 1 < chordTimeline.Count &&
                       chordTimeline[activeChordIndex + 1].StartUs <= frame.StartUs + 20_000)
                    activeChordIndex++;
                var activeChordEv = activeChordIndex >= 0 ? chordTimeline[activeChordIndex] : null;
                var candidates = BuildFrameCandidates(frame, activeChordEv, tuning, options);
                frameCandidates.Add(candidates);
            }

            // 5. 全局 Viterbi 求解品格与和弦切换路径（内含 Z 键重置 P1 物理时间约束）
            var chosenPath = SolveHybridViterbiPath(frames, frameCandidates, options, tuning);

            // 6. 编译为底层硬件动作指令（保证所有按键按压 >= 20ms）
            return EmitHybridActions(frames, chosenPath, preset, options);
        }

        public static List<RawMidiNote> AlignPlayableNotes(
            IReadOnlyList<RawMidiNote> notes, int semitoneShift, GuitarTuning? tuning = null)
        {
            tuning ??= GuitarTuning.Standard;
            // One global octave choice preserves intervals. Per-note clamping
            // would collapse unrelated high or low melody notes together.
            var semitoneAligned = notes.Where(n => n.Channel != 9).OrderBy(n => n.StartUs)
                .Select(n => PhraseOctaveFolder.CloneWithPitch(n,
                    Math.Clamp(n.NoteNumber + semitoneShift, 0, 127)))
                .ToList();
            if (semitoneAligned.Count == 0) return semitoneAligned;
            int octaveShift = FretConverter.FindBestGlobalOctaveShift(semitoneAligned, tuning);
            return semitoneAligned
                .Where(n => n.NoteNumber + octaveShift >= tuning.MinPlayableNote &&
                            n.NoteNumber + octaveShift <= tuning.MaxPlayableNote)
                .Select(n => PhraseOctaveFolder.CloneWithPitch(n, n.NoteNumber + octaveShift))
                .ToList();
        }

        private static List<RawMidiNote> TagPrimaryMelody(IReadOnlyList<RawMidiNote> source)
        {
            var nonDrum = source.Where(note => note.Channel != 9).ToList();
            var trackCount = nonDrum.Select(note => note.TrackId).Distinct().Take(2).Count();
            var melody = trackCount <= 1
                ? PolyphonicMelodyExtractor.ExtractSingleTrackMelody(nonDrum)
                : AutoLeadFusionMode.Extract(nonDrum, tracks: null).Notes;
            var keys = melody.Select(note => (note.TrackId, note.Channel, note.StartUs, note.NoteNumber)).ToHashSet();
            return source.Select(note =>
            {
                var clone = PhraseOctaveFolder.CloneWithPitch(note, note.NoteNumber);
                clone.IsPrimaryMelody = note.IsPrimaryMelody || keys.Contains((note.TrackId, note.Channel, note.StartUs, note.NoteNumber));
                return clone;
            }).ToList();
        }

        private sealed record OnsetFrame(long StartUs, List<RawMidiNote> Notes, bool IsLeadActive, bool IsShielded,
            long NextLeadDeltaUs, byte NextLeadFretPage);

        private static List<OnsetFrame> BuildOnsetFrames(List<RawMidiNote> sortedNotes, long toleranceUs,
            GuitarTuning tuning, bool avoidStrum = false, long maxRollGapUs = 25_000)
        {
            var batches = new List<(long StartUs, List<RawMidiNote> Notes)>();
            int i = 0;
            while (i < sortedNotes.Count)
            {
                long start = sortedNotes[i].StartUs;
                var list = new List<RawMidiNote> { sortedNotes[i] };
                int j = i + 1;
                while (j < sortedNotes.Count && (sortedNotes[j].StartUs - start) <= toleranceUs)
                {
                    if (avoidStrum && sortedNotes[j].StartUs - sortedNotes[j - 1].StartUs > maxRollGapUs)
                        break;
                    if (!AttackClusterPolicy.CanJoin(list, sortedNotes[j], start,
                            toleranceUs * 25 / 30, tuning))
                        break;

                    list.Add(sortedNotes[j]);
                    j++;
                }
                // A staggered two-note cross-track pair is usually two plucks.
                // Keep it separate unless a third attack confirms a chord roll.
                if (list.Count == 2 && list[0].TrackId != list[1].TrackId &&
                    list[1].StartUs > start && !list.Any(note => note.IsBrushChord))
                {
                    list.RemoveAt(1);
                    j--;
                }
                batches.Add((start, list));
                i = j;
            }
            if (batches.Count == 0) return new List<OnsetFrame>();

            int[] skyline = batches.Select(b => (int)b.Notes.Max(n => n.NoteNumber)).OrderBy(p => p).ToArray();
            int medianTop = skyline[skyline.Length / 2];
            var frames = new List<OnsetFrame>(batches.Count);
            long leadStart = long.MinValue / 2;
            long leadEnd = long.MinValue / 2;
            int leadPitch = 0;
            foreach (var batch in batches)
            {
                var top = batch.Notes.OrderByDescending(n => n.NoteNumber).ThenByDescending(n => n.Velocity).First();
                bool hasPrimary = batch.Notes.Any(note => note.IsPrimaryMelody);
                bool shielded = !hasPrimary && batch.Notes.Count == 1 && batch.StartUs - leadStart < 260_000
                    && batch.StartUs < leadEnd - 30_000 && leadPitch - top.NoteNumber >= 7;
                // The skyline is a hint. Requiring a real upper voice avoids
                // treating every bass arpeggio as a new melody attack.
                bool leadActive = hasPrimary || (!shielded && (top.NoteNumber >= 64 ||
                    (top.NoteNumber >= Math.Max(60, medianTop - 3) && batch.Notes.Count <= 2)));
                if (leadActive)
                {
                    var activeLead = batch.Notes.Where(note => note.IsPrimaryMelody)
                        .OrderByDescending(note => note.NoteNumber).FirstOrDefault() ?? top;
                    leadStart = batch.StartUs;
                    leadEnd = batch.StartUs + Math.Max(20_000, activeLead.DurationUs);
                    leadPitch = activeLead.NoteNumber;
                }
                frames.Add(new OnsetFrame(batch.StartUs, batch.Notes, leadActive, shielded, long.MaxValue, 1));
            }
            long nextLeadStart = long.MaxValue;
            byte nextLeadPage = 1;
            for (int reverseIndex = frames.Count - 1; reverseIndex >= 0; reverseIndex--)
            {
                frames[reverseIndex] = frames[reverseIndex] with
                {
                    NextLeadDeltaUs = nextLeadStart == long.MaxValue ? long.MaxValue : nextLeadStart - frames[reverseIndex].StartUs,
                    NextLeadFretPage = nextLeadPage
                };
                if (frames[reverseIndex].IsLeadActive) nextLeadStart = frames[reverseIndex].StartUs;
                if (frames[reverseIndex].IsLeadActive)
                {
                    RawMidiNote lead = frames[reverseIndex].Notes.Where(note => note.IsPrimaryMelody)
                        .OrderByDescending(note => note.NoteNumber).FirstOrDefault()
                        ?? frames[reverseIndex].Notes.OrderByDescending(note => note.NoteNumber).First();
                    nextLeadPage = FindLowestMovementFretPage(lead.NoteNumber, tuning);
                }
            }
            return frames;
        }

        private static byte FindLowestMovementFretPage(byte noteNumber, GuitarTuning tuning)
        {
            byte bestPage = 1;
            int bestDistance = int.MaxValue;
            foreach (var candidate in FretConverter.GenerateCandidates(noteNumber, tuning))
            {
                int distance = CircularPageNavigator.GetShortestPath(1, candidate.Page, 5).Steps;
                if (distance < bestDistance) { bestDistance = distance; bestPage = candidate.Page; }
            }
            return bestPage;
        }

        /// <summary>
        /// 为单个时间帧同时生成【品格候选】与【和弦候选】
        /// </summary>
        private static List<HybridStateCandidate> BuildFrameCandidates(
            OnsetFrame frame,
            ChordEvent? activeChordEv,
            GuitarTuning tuning,
            HybridCompilerOptions options)
        {
            var list = new List<HybridStateCandidate>();
            var distinctNotes = frame.Notes
                .GroupBy(n => n.NoteNumber)
                .Select(g => g.OrderByDescending(x => x.IsPrimaryMelody).ThenByDescending(x => x.Velocity).First())
                .OrderByDescending(n => n.IsPrimaryMelody).ThenByDescending(n => n.NoteNumber)
                .ToList();

            // ========= 候选分支 A：品格模式 (Fret Mode, P1~P5) =========
            // 提取最高主旋律音（若有可同品共奏的和声音符则一并加入）
            var primaryNote = distinctNotes.FirstOrDefault(note => note.IsPrimaryMelody) ?? distinctNotes[0];
            bool isStrumBurstFrame = options.AvoidStrum && IsStrumRollCluster(frame.Notes);
            bool hasFrettedCandidate = Enumerable.Range(1, 6).Any(s =>
            {
                int fret = primaryNote.NoteNumber - tuning.GetOpenMidiNote(s);
                return fret is >= 1 and <= 24;
            });
            for (int s = 1; s <= 6; s++)
            {
                int fret = primaryNote.NoteNumber - tuning.GetOpenMidiNote(s);
                if (fret < 0 || fret > 24 || fret == 0 && hasFrettedCandidate) continue;

                var (page, slot) = GetFretPageAndSlot((byte)fret);
                var assigned = new List<(byte StringNo, byte NoteNumber, long DurationUs)>
                {
                    ((byte)s, primaryNote.NoteNumber, primaryNote.DurationUs)
                };
                var usedStrings = new HashSet<int> { s };

                // 检查同帧其他音符是否恰好能在同一个品位（不同琴弦）同时弹出
                foreach (var other in (isStrumBurstFrame ? Enumerable.Empty<RawMidiNote>() : distinctNotes.Skip(1)))
                {
                    for (int s2 = 1; s2 <= 6; s2++)
                    {
                        if (usedStrings.Contains(s2)) continue;
                        if (tuning.GetOpenMidiNote(s2) + fret == other.NoteNumber)
                        {
                            usedStrings.Add(s2);
                            assigned.Add(((byte)s2, other.NoteNumber, other.DurationUs));
                            break;
                        }
                    }
                }

                // 计算品格本地代价：如果原帧是 3~6 音的丰满和弦，但品格模式只能弹出 1 个单音，施加丢音代价以驱动切往和弦模式
                int droppedNotes = isStrumBurstFrame ? 0 : distinctNotes.Count - assigned.Count;
                double fretLocalCost = (frame.IsLeadActive ? droppedNotes * 1.8 : droppedNotes * 2.8)
                    + (fret > 12 ? (fret - 12) * 0.15 : 0.0);
                if (fret == 0) fretLocalCost += 0.2; // P5 空弦微调

                list.Add(new HybridStateCandidate
                {
                    Mode = GuitarPlayModeType.Fret,
                    Page = page,
                    Slot = slot,
                    Fret = (byte)fret,
                    Chord = ChordId.Unknown,
                    StringNotes = assigned,
                    LocalCost = fretLocalCost
                });
            }

            // ========= 候选分支 B：基于游戏真实弦音的混合和弦候选 =========
            // 只对本混合模式使用 chord.json 的真实音高与安全弦表。
            if (activeChordEv != null && GameChordGroundTruth.Table.TryGetValue(activeChordEv.Chord, out var chordInfo))
            {
                // Preserve a chord candidate that can play the current lead exactly.
                // The path solver evaluates the real cost of switching back later;
                // a round-trip time check here wrongly removes rapid chord arpeggios.
                byte exactLeadString = GameChordGroundTruth.FindExactCleanString(chordInfo.Id, primaryNote.NoteNumber);
                bool fullStrumPreservesLead = !frame.IsLeadActive && !frame.IsShielded ||
                    chordInfo.ActualMidi1To6.Contains(primaryNote.NoteNumber) ||
                    (options.AutoStrum != AutoStrumMode.Disabled && options.ChordMode != ChordRecognitionMode.Strict &&
                     chordInfo.ActualMidi1To6.Any(pitch => pitch % 12 == primaryNote.NoteNumber % 12));
                if (!options.AvoidStrum && chordInfo.CanUseFullMouseStrum && distinctNotes.Count >= 3 && fullStrumPreservesLead &&
                    options.AutoStrum != AutoStrumMode.Disabled &&
                    options.PlayStyle is not GuitarPlayStyle.PluckOnly and not GuitarPlayStyle.BlockOnly)
                {
                    double chordBonusCost = Math.Max(0.1, 1.5 - activeChordEv.Confidence);
                    list.Add(new HybridStateCandidate
                    {
                        Mode = GuitarPlayModeType.Chord,
                        Page = chordInfo.Page,
                        Slot = chordInfo.Slot,
                        Chord = chordInfo.Id,
                        Articulation = ChordArticulation.Strum,
                        StrumDirection = activeChordEv.Direction,
                        LocalCost = chordBonusCost
                    });
                }

                if (options.PlayStyle != GuitarPlayStyle.StrumOnly)
                {
                    var safeNotes = new List<(byte StringNo, byte NoteNumber, long DurationUs)>();
                    var occupied = new HashSet<byte>();
                    foreach (var note in (isStrumBurstFrame ? distinctNotes.Take(1) : distinctNotes))
                    {
                        byte stringNo = GameChordGroundTruth.FindExactCleanString(chordInfo.Id, note.NoteNumber, occupied);
                        if (stringNo == 0 && !frame.IsLeadActive)
                            stringNo = GameChordGroundTruth.FindPitchClassCompatibleString(chordInfo.Id, note.NoteNumber, occupied);
                        if (stringNo == 0 || !occupied.Add(stringNo)) continue;

                        // For octave-equivalent accompaniment, emit the chord.json pitch actually produced by this string.
                        byte actualPitch = chordInfo.ActualMidi1To6[stringNo - 1];
                        safeNotes.Add((stringNo, actualPitch, note.DurationUs));
                    }

                    bool leadMatched = !frame.IsLeadActive && !frame.IsShielded ||
                        safeNotes.Any(note => note.NoteNumber == primaryNote.NoteNumber);
                    if (safeNotes.Count > 0 && leadMatched)
                    {
                        list.Add(new HybridStateCandidate
                        {
                            Mode = GuitarPlayModeType.Chord,
                            Page = chordInfo.Page,
                            Slot = chordInfo.Slot,
                            Chord = chordInfo.Id,
                            Articulation = safeNotes.Count >= 2 ? ChordArticulation.Block : ChordArticulation.Arpeggio,
                            StringNotes = safeNotes,
                            LocalCost = exactLeadString > 0 ? 0.35 : 0.75
                        });
                    }
                }
            }

            return list;
        }

        private static bool IsStrumRollCluster(IReadOnlyList<RawMidiNote> notes)
        {
            if (notes.Any(n => n.IsBrushChord)) return true;
            var ordered = notes.OrderBy(n => n.StartUs).ToList();
            if (ordered.Count < 3 || ordered.Select(n => n.NoteNumber).Distinct().Count() < 3 ||
                ordered.Max(n => n.NoteNumber) - ordered.Min(n => n.NoteNumber) < 5)
                return false;

            long spanUs = ordered[^1].StartUs - ordered[0].StartUs;
            if (spanUs <= 25_000) return true;
            int asc = 0, desc = 0;
            for (int i = 0; i < ordered.Count - 1; i++)
            {
                int difference = ordered[i + 1].NoteNumber - ordered[i].NoteNumber;
                if (difference > 0) asc++;
                else if (difference < 0) desc++;
            }
            return asc >= (ordered.Count - 1) * .7 || desc >= (ordered.Count - 1) * .7;
        }

        /// <summary>
        /// 计算两个混合状态之间的物理准备耗时与转移代价（核心：Z 键切模式自动重置到 P1）
        /// </summary>
        public static (long PrepareTimeUs, double Cost) CalculateHybridTransition(
            HybridStateCandidate prev,
            HybridStateCandidate cur,
            long deltaUs,
            HybridCompilerOptions opt)
        {
            if (cur.IsHoldPositionFallback)
                return prev.Mode == cur.Mode && prev.Page == cur.Page && prev.Slot == cur.Slot
                    ? (0, cur.LocalCost) : (0, double.PositiveInfinity);

            long singleKeyCycleUs = Math.Max(25_000, opt.MinKeyHoldUs)
                + Math.Max(15_000, opt.ControlInterTapGapUs);
            bool modeChanged = prev.Mode != cur.Mode;
            int totalPages = cur.Mode == GuitarPlayModeType.Fret ? 5 : 4;
            byte startPage = modeChanged ? (byte)1 : prev.Page;
            int pageSteps = CircularPageNavigator.GetShortestPath(startPage, cur.Page, totalPages).Steps;
            bool slotSelectionNeeded = modeChanged || pageSteps > 0 || cur.Slot != prev.Slot;

            long settleUs = Math.Max(55_000, opt.WheelMouseSettleUs);
            long prepareUs = (modeChanged ? singleKeyCycleUs : 0)
                + pageSteps * singleKeyCycleUs
                + (slotSelectionNeeded ? settleUs : 0);
            if (!slotSelectionNeeded) return (0, cur.LocalCost);

            long requiredGapUs = Math.Max(5_000, opt.MinKeyHoldUs) + 6_000 + prepareUs + 6_000;
            long modeTransitionFloorUs = 220_000;
            if (deltaUs < requiredGapUs || modeChanged && deltaUs < Math.Max(requiredGapUs, modeTransitionFloorUs))
                return (prepareUs, double.PositiveInfinity);

            double cost = cur.LocalCost + (modeChanged ? 3.2 : 0.8)
                + pageSteps * (modeChanged ? 1.1 : 1.3);
            if (deltaUs < 300_000) cost += (300_000 - deltaUs) / 300_000.0 * 4.5;
            return (prepareUs, cost);
        }

        private sealed record PathNode(HybridStateCandidate State, double Cost, int Parent);

        private static List<HybridStateCandidate> SolveHybridViterbiPath(
            List<OnsetFrame> frames,
            List<List<HybridStateCandidate>> candidatesPerFrame,
            HybridCompilerOptions opt,
            GuitarTuning tuning)
        {
            var layers = new List<PathNode>[frames.Count];
            var initial = new List<PathNode>();
            foreach (var candidate in candidatesPerFrame[0])
            {
                int totalPages = candidate.Mode == GuitarPlayModeType.Fret ? 5 : 4;
                byte startPage = candidate.Mode == opt.InitialMode ? opt.InitialPage : (byte)1;
                int pageSteps = CircularPageNavigator.GetShortestPath(startPage, candidate.Page, totalPages).Steps;
                initial.Add(new PathNode(candidate, candidate.LocalCost + pageSteps * 0.5, -1));
            }
            var initialState = new HybridStateCandidate
            {
                Mode = opt.InitialMode, Page = opt.InitialPage, Slot = 0,
                LocalCost = 14, IsHoldPositionFallback = true
            };
            initial.Add(new PathNode(initialState, initialState.LocalCost, -1));
            layers[0] = initial;

            for (int i = 1; i < frames.Count; i++)
            {
                var next = new List<PathNode>();
                var bestByPosition = new Dictionary<(GuitarPlayModeType, byte, byte, byte, ChordId), int>();
                long deltaUs = Math.Max(1, frames[i].StartUs - frames[i - 1].StartUs);
                for (int k = 0; k < layers[i - 1].Count; k++)
                {
                    var previous = layers[i - 1][k];
                    foreach (var candidate in candidatesPerFrame[i].Append(MaterializeHoldState(frames[i], previous.State, tuning)))
                    {
                        if (frames[i].IsShielded && !candidate.IsHoldPositionFallback &&
                            (candidate.Mode != previous.State.Mode || candidate.Page != previous.State.Page ||
                             candidate.Slot != previous.State.Slot))
                            continue;
                        var (_, transitionCost) = CalculateHybridTransition(previous.State, candidate, deltaUs, opt);
                        if (double.IsPositiveInfinity(transitionCost)) continue;
                        double total = previous.Cost + transitionCost;
                        var key = (candidate.Mode, candidate.Page, candidate.Slot, candidate.Fret, candidate.Chord);
                        if (bestByPosition.TryGetValue(key, out int index))
                        {
                            if (total < next[index].Cost) next[index] = new PathNode(candidate, total, k);
                        }
                        else
                        {
                            bestByPosition[key] = next.Count;
                            next.Add(new PathNode(candidate, total, k));
                        }
                    }
                }
                layers[i] = next;
            }

            var path = new HybridStateCandidate[frames.Count];
            int bestIndex = 0;
            for (int j = 1; j < layers[^1].Count; j++)
                if (layers[^1][j].Cost < layers[^1][bestIndex].Cost) bestIndex = j;
            for (int i = frames.Count - 1; i >= 0; i--)
            {
                var node = layers[i][bestIndex];
                path[i] = node.State;
                bestIndex = node.Parent;
            }
            return path.ToList();
        }

        private static HybridStateCandidate MaterializeHoldState(OnsetFrame frame,
            HybridStateCandidate previous, GuitarTuning tuning)
        {
            var notes = new List<(byte StringNo, byte NoteNumber, long DurationUs)>();
            if (!frame.IsShielded && previous.Slot > 0)
            {
                IEnumerable<RawMidiNote> holdCandidates = frame.Notes.Any(note => note.IsPrimaryMelody)
                    ? frame.Notes.Where(note => note.IsPrimaryMelody).OrderByDescending(note => note.NoteNumber)
                    : frame.Notes.OrderByDescending(note => note.NoteNumber);
                foreach (var note in holdCandidates)
                {
                    if (previous.Mode == GuitarPlayModeType.Fret)
                    {
                        for (byte stringNo = 1; stringNo <= 6; stringNo++)
                            if (tuning.GetOpenMidiNote(stringNo) + previous.Fret == note.NoteNumber)
                            {
                                notes.Add((stringNo, note.NoteNumber, note.DurationUs));
                                break;
                            }
                    }
                    else if (GameChordGroundTruth.FindExactCleanString(previous.Chord, note.NoteNumber) is byte stringNo && stringNo > 0)
                    {
                        notes.Add((stringNo, note.NoteNumber, note.DurationUs));
                    }
                    if (notes.Count > 0) break;
                }
            }
            return previous with
            {
                StringNotes = notes,
                Articulation = ChordArticulation.Arpeggio,
                // Shielding only forbids disruptive controls. An empty hold state
                // still drops an audible attack, so it must not outrank a chord
                // pluck that uses the already selected page and slot.
                LocalCost = notes.Count > 0 ? 0.2 : 14.0,
                IsHoldPositionFallback = true
            };
        }

        /// <summary>
        /// 将全局最优混合路径编译为 GuitarAction 序列，严格保证所有按键按压 >= 20ms
        /// </summary>
        private static List<GuitarAction> EmitHybridActions(
            List<OnsetFrame> frames,
            List<HybridStateCandidate> path,
            WheelPreset preset,
            HybridCompilerOptions opt)
        {
            var actions = new List<GuitarAction>();
            var barrierTimes = new List<long>();
            var lastStringUpUs = new Dictionary<byte, long>();
            var lastStringDownUs = new Dictionary<byte, long>();
            var stringActionPairs = new List<(int DownIndex, int UpIndex)>();

            GuitarPlayModeType currentMode = opt.InitialMode;
            byte currentPage = opt.InitialPage;
            byte currentSlot = 0;
            long lastAttackTimeUs = -1;
            long lastControlFinishedUs = -1;
            bool alternateStrumUp = false;

            // The game polls guitar string inputs and can miss chord attacks
            // shorter than one 20 ms input window. Keep this hardware floor
            // even when the user's general minimum hold setting is lower.
            long keyHoldUs = Math.Max(20_000, opt.MinKeyHoldUs);
            opt.ReleaseGapUs = Math.Max(22_000, opt.ReleaseGapUs);
            long navHoldUs = Math.Max(25_000, opt.MinKeyHoldUs);
            long navGapUs = Math.Max(15_000, opt.ControlInterTapGapUs);
            long wheelSettleUs = Math.Max(55_000, opt.WheelMouseSettleUs);
            long singleTapStepUs = navHoldUs + navGapUs;

            // 1. 生成模式切换 (Z)、页面翻转 (R/T) 与轮盘选槽动作
            for (int i = 0; i < frames.Count; i++)
            {
                long evTimeUs = frames[i].StartUs;
                var state = path[i];

                bool modeChanged = (state.Mode != currentMode);
                // 关键规则：若按下 Z 切换了模式，当前页自动归位为 P1 (Page = 1)
                byte effectiveStartPage = modeChanged ? (byte)1 : currentPage;
                int totalPages = state.Mode == GuitarPlayModeType.Fret ? 5 : 4;
                var (pageSteps, pageDirection) = CircularPageNavigator.GetShortestPath(effectiveStartPage, state.Page, totalPages);
                bool needWheelSelect = modeChanged || pageSteps > 0 || state.Slot != currentSlot;
                long soundTimeUs = evTimeUs;

                if (needWheelSelect)
                {
                    // 倒推控制序列起点：确保在前置音符按满 20ms 之后才开始切模式/翻页
                    long selectTimeUs = evTimeUs - wheelSettleUs;
                    long minSafeControlStartUs = (lastAttackTimeUs >= 0)
                        ? (lastAttackTimeUs + keyHoldUs + 8_000)
                        : 0;
                    if (lastControlFinishedUs >= 0)
                        minSafeControlStartUs = Math.Max(minSafeControlStartUs, lastControlFinishedUs + 6_000);

                    long totalNavDurationUs = (modeChanged ? singleTapStepUs : 0) + pageSteps * singleTapStepUs;
                    long controlStartUs = Math.Max(0, selectTimeUs - totalNavDurationUs);

                    if (controlStartUs < minSafeControlStartUs)
                    {
                        controlStartUs = minSafeControlStartUs;
                    }
                    selectTimeUs = controlStartUs + totalNavDurationUs;

                    // Close a previous sustain before touching Z/R/T or the wheel.
                    // Updating this now also lets a later same-string attack use
                    // the real release time instead of the MIDI's original tail.
                    long firstBarrierUs = totalNavDurationUs > 0 ? controlStartUs : selectTimeUs;
                    foreach (byte stringNo in lastStringUpUs.Keys.ToArray())
                    {
                        if (lastStringUpUs[stringNo] <= firstBarrierUs - 8_000) continue;
                        long safeUp = Math.Max(lastStringDownUs[stringNo] + keyHoldUs, firstBarrierUs - 8_000);
                        int upIndex = actions.FindLastIndex(a => a.Type == GuitarActionType.StringUp && a.Value == stringNo);
                        if (upIndex >= 0 && safeUp < actions[upIndex].TimeUs)
                        {
                            var prior = actions[upIndex];
                            actions[upIndex] = new GuitarAction(safeUp, prior.Type, prior.Value, prior.ScanCode,
                                prior.MouseButton, safeUp + 60_000, prior.RequiredPage, prior.RequiredSlot,
                                prior.Resource, prior.FallbackPolicy, prior.NoteNumber);
                            lastStringUpUs[stringNo] = safeUp;
                        }
                    }

                    long cursorUs = controlStartUs;

                    // (a) 若模式改变，先按 Z 键切换模式（页面自动重置为 P1）
                    if (modeChanged)
                    {
                        actions.Add(new GuitarAction(
                            cursorUs,
                            GuitarActionType.ModeSwitchDown,
                            state.Mode == GuitarPlayModeType.Fret ? (byte)1 : (byte)2,
                            opt.ModeSwitchScanCode,
                            MouseButton.Left,
                            deadlineUs: cursorUs + 500_000,
                            requiredPage: 1,
                            requiredSlot: 1,
                            resource: GameActionResource.Nav));

                        actions.Add(new GuitarAction(
                            cursorUs + navHoldUs,
                            GuitarActionType.ModeSwitchUp,
                            state.Mode == GuitarPlayModeType.Fret ? (byte)1 : (byte)2,
                            opt.ModeSwitchScanCode,
                            MouseButton.Left,
                            deadlineUs: cursorUs + navHoldUs + 500_000,
                            requiredPage: 1,
                            requiredSlot: 1,
                            resource: GameActionResource.Nav));

                        barrierTimes.Add(cursorUs);
                        cursorUs += singleTapStepUs;
                        currentMode = state.Mode;
                        currentPage = 1; // Z 键触发后重置到 P1
                    }

                    // (b) 从 currentPage 逐步按 T 或 R 翻到目标页 state.Page
                    if (pageSteps > 0)
                    {
                        ushort navScan = pageDirection > 0 ? preset.PageNextScanCode : preset.PagePrevScanCode;

                        for (int step = 0; step < pageSteps; step++)
                        {
                            byte nextPage = CircularPageNavigator.StepPage(currentPage, pageDirection, totalPages);
                            actions.Add(new GuitarAction(
                                cursorUs,
                                GuitarActionType.PageNav,
                                nextPage,
                                navScan,
                                MouseButton.Left,
                                deadlineUs: cursorUs + 500_000,
                                requiredPage: state.Page,
                                requiredSlot: state.Slot,
                                resource: GameActionResource.Nav));

                            actions.Add(new GuitarAction(
                                cursorUs + navHoldUs,
                                GuitarActionType.PageNavUp,
                                nextPage,
                                navScan,
                                MouseButton.Left,
                                deadlineUs: cursorUs + navHoldUs + 500_000,
                                requiredPage: state.Page,
                                requiredSlot: state.Slot,
                                resource: GameActionResource.Nav));

                            barrierTimes.Add(cursorUs);
                            cursorUs += singleTapStepUs;
                            currentPage = nextPage;
                        }
                    }

                    // (c) 点击轮盘槽位 (I ~ VI)
                    actions.Add(new GuitarAction(
                        selectTimeUs,
                        GuitarActionType.WheelSelect,
                        state.Slot,
                        0,
                        MouseButton.Left,
                        deadlineUs: selectTimeUs + 500_000,
                        requiredPage: state.Page,
                        requiredSlot: state.Slot,
                        resource: GameActionResource.Wheel));

                    barrierTimes.Add(selectTimeUs);
                    currentSlot = state.Slot;
                    lastControlFinishedUs = selectTimeUs + wheelSettleUs;
                    soundTimeUs = Math.Max(evTimeUs, lastControlFinishedUs);
                }

                // 2. 生成发音动作（扫弦鼠标左/右键 或 琴弦 1~6 数字键）
                if (state.Mode == GuitarPlayModeType.Chord && state.Articulation == ChordArticulation.Strum && !opt.AvoidStrum)
                {
                    // 实机规则：左键 = 上扫音 (HighToLow)，右键 = 下扫音 (LowToHigh)
                    MouseButton strumBtn;
                    if (opt.AutoStrum == AutoStrumMode.AllDown)
                    {
                        strumBtn = opt.DownStrumMouseButton;
                    }
                    else if (opt.AutoStrum == AutoStrumMode.Alternate)
                    {
                        strumBtn = alternateStrumUp ? opt.UpStrumMouseButton : opt.DownStrumMouseButton;
                        alternateStrumUp = !alternateStrumUp;
                    }
                    else if (state.StrumDirection == StrumDirectionHint.HighToLow)
                    {
                        strumBtn = opt.UpStrumMouseButton;   // 左键上扫
                        alternateStrumUp = false;
                    }
                    else if (state.StrumDirection == StrumDirectionHint.LowToHigh)
                    {
                        strumBtn = opt.DownStrumMouseButton; // 右键下扫
                        alternateStrumUp = true;
                    }
                    else
                    {
                        strumBtn = opt.AutoStrum == AutoStrumMode.FollowMidi
                            ? opt.DownStrumMouseButton
                            : alternateStrumUp ? opt.UpStrumMouseButton : opt.DownStrumMouseButton;
                        if (opt.AutoStrum == AutoStrumMode.Auto) alternateStrumUp = !alternateStrumUp;
                    }

                    actions.Add(new GuitarAction(
                        soundTimeUs,
                        GuitarActionType.StrumClick,
                        0,
                        0,
                        strumBtn,
                        deadlineUs: soundTimeUs + 45_000,
                        requiredPage: state.Page,
                        requiredSlot: state.Slot,
                        resource: GameActionResource.Mouse));
                }
                else
                {
                    // 品格模式拨弦 或 和弦模式下的分解拨弦/块和弦
                    IReadOnlyList<(byte StringNo, byte NoteNumber, long DurationUs)> notesToEmit = state.StringNotes;
                    if (opt.AvoidStrum && state.Mode == GuitarPlayModeType.Chord &&
                        state.Articulation == ChordArticulation.Strum && frames[i].Notes.Count > 0 &&
                        GameChordGroundTruth.Table.TryGetValue(state.Chord, out var strumChord))
                    {
                        var lead = frames[i].Notes.OrderByDescending(n => n.IsPrimaryMelody)
                            .ThenByDescending(n => n.NoteNumber).ThenByDescending(n => n.Velocity).First();
                        byte safeString = GameChordGroundTruth.FindExactCleanString(state.Chord, lead.NoteNumber);
                        if (safeString == 0)
                            safeString = GameChordGroundTruth.FindPitchClassCompatibleString(state.Chord, lead.NoteNumber);
                        notesToEmit = safeString > 0
                            ? new[] { (safeString, strumChord.ActualMidi1To6[safeString - 1], lead.DurationUs) }
                            : Array.Empty<(byte, byte, long)>();
                    }
                    foreach (var sn in notesToEmit)
                    {
                        ushort strScan = (preset.StringScanCodes != null && preset.StringScanCodes.Length >= sn.StringNo)
                            ? preset.StringScanCodes[sn.StringNo - 1]
                            : GameScanCodes.ForGuitarString(sn.StringNo);

                        // 计算按压时长：无论后续是否有屏障，强制保证 upTime - downTime >= 20,000us (20ms)
                        long previousDownUs = lastStringDownUs.GetValueOrDefault(sn.StringNo, long.MinValue / 2);
                        long downTimeUs = Math.Max(soundTimeUs, previousDownUs + keyHoldUs + opt.ReleaseGapUs);
                        long previousUpUs = lastStringUpUs.GetValueOrDefault(sn.StringNo, long.MinValue / 2);
                        if (previousUpUs > downTimeUs - opt.ReleaseGapUs)
                        {
                            long safeUpUs = Math.Max(previousDownUs + keyHoldUs, downTimeUs - opt.ReleaseGapUs);
                            int previousUpIndex = actions.FindLastIndex(action =>
                                action.Type == GuitarActionType.StringUp && action.Value == sn.StringNo);
                            if (previousUpIndex >= 0 && safeUpUs < previousUpUs)
                            {
                                var previousUp = actions[previousUpIndex];
                                actions[previousUpIndex] = new GuitarAction(safeUpUs, previousUp.Type,
                                    previousUp.Value, previousUp.ScanCode, previousUp.MouseButton,
                                    safeUpUs + 60_000, previousUp.RequiredPage, previousUp.RequiredSlot,
                                    previousUp.Resource, previousUp.FallbackPolicy, previousUp.NoteNumber);
                                lastStringUpUs[sn.StringNo] = safeUpUs;
                            }
                        }
                        downTimeUs = Math.Max(downTimeUs,
                            lastStringUpUs.GetValueOrDefault(sn.StringNo, long.MinValue / 2) + opt.ReleaseGapUs);
                        long desiredHoldUs = Math.Max(keyHoldUs, Math.Min(sn.DurationUs, 1_500_000));
                        long upTimeUs = downTimeUs + desiredHoldUs;
                        lastStringDownUs[sn.StringNo] = downTimeUs;
                        lastStringUpUs[sn.StringNo] = upTimeUs;

                        int downActionIndex = actions.Count;
                        actions.Add(new GuitarAction(
                            downTimeUs,
                            GuitarActionType.StringDown,
                            sn.StringNo,
                            strScan,
                            MouseButton.Left,
                            deadlineUs: downTimeUs + 35_000,
                            requiredPage: state.Page,
                            requiredSlot: state.Slot,
                            resource: GameActionResource.StringKeys,
                            fallbackPolicy: FallbackPolicy.Skip,
                            noteNumber: sn.NoteNumber));

                        int upActionIndex = actions.Count;
                        actions.Add(new GuitarAction(
                            upTimeUs,
                            GuitarActionType.StringUp,
                            sn.StringNo,
                            strScan,
                            MouseButton.Left,
                            deadlineUs: upTimeUs + 60_000,
                            requiredPage: state.Page,
                            requiredSlot: state.Slot,
                            resource: GameActionResource.StringKeys));
                        stringActionPairs.Add((downActionIndex, upActionIndex));
                    }
                }

                if (state.StringNotes.Count > 0)
                    lastAttackTimeUs = Math.Max(lastAttackTimeUs, state.StringNotes.Max(sn => lastStringDownUs[sn.StringNo]));
                else if (state.Articulation == ChordArticulation.Strum)
                    lastAttackTimeUs = soundTimeUs;
            }

            // Pair each release with its own press before checking the next press
            // and control barrier. A release timestamp can be later than several
            // later presses, so it cannot identify its press through binary search.
            barrierTimes.Sort();
            foreach (var group in stringActionPairs.GroupBy(pair => actions[pair.DownIndex].Value))
            {
                var pairs = group.OrderBy(pair => actions[pair.DownIndex].TimeUs).ToArray();
                for (int pairIndex = 0; pairIndex < pairs.Length; pairIndex++)
                {
                    var pair = pairs[pairIndex];
                    var down = actions[pair.DownIndex];
                    var up = actions[pair.UpIndex];
                    long nextDown = pairIndex + 1 < pairs.Length
                        ? actions[pairs[pairIndex + 1].DownIndex].TimeUs : long.MaxValue;
                    int barrierIndex = barrierTimes.BinarySearch(down.TimeUs + 1);
                    if (barrierIndex < 0) barrierIndex = ~barrierIndex;
                    long nextBarrier = barrierIndex < barrierTimes.Count ? barrierTimes[barrierIndex] : long.MaxValue;
                    long safeUp = Math.Min(up.TimeUs,
                        Math.Min(nextDown - Math.Max(22_000, opt.ReleaseGapUs),
                            nextBarrier - 8_000));
                    safeUp = Math.Max(down.TimeUs + keyHoldUs, safeUp);
                    if (safeUp != up.TimeUs)
                        actions[pair.UpIndex] = new GuitarAction(safeUp, up.Type, up.Value, up.ScanCode,
                            up.MouseButton, safeUp + 60_000, up.RequiredPage, up.RequiredSlot,
                            up.Resource, up.FallbackPolicy, up.NoteNumber);
                }
            }

            // 3. 排序动作队列（同时间戳下：StringUp/PageNavUp 优先释放 -> PageNav(含Z/R/T) -> WheelSelect -> StrumClick -> StringDown）
            actions.Sort((a, b) =>
            {
                int cmp = a.TimeUs.CompareTo(b.TimeUs);
                if (cmp != 0) return cmp;
                return GetActionPriority(a.Type).CompareTo(GetActionPriority(b.Type));
            });

            return actions;
        }

        private static int GetActionPriority(GuitarActionType type) => type switch
        {
            GuitarActionType.StringUp => 0,
            GuitarActionType.PageNavUp => 1,
            GuitarActionType.ModeSwitchUp => 1,
            GuitarActionType.PageNav => 2,
            GuitarActionType.ModeSwitchDown => 2,
            GuitarActionType.WheelSelect => 3,
            GuitarActionType.StrumClick => 4,
            GuitarActionType.StringDown => 5,
            _ => 6
        };
    }
}

