using MidiAutoPlayer.Models;
using MidiAutoPlayer.Midi;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MidiAutoPlayer.Guitar
{
    public sealed class ChordAnalyzerV2Options
    {
        public ChordRecognitionMode Mode { get; set; } = ChordRecognitionMode.Loose;
        /// <summary>是否启用自动半音移调（优先保留游戏和弦轮支持的调性）</summary>
        public bool EnableAutoTranspose { get; set; } = true;

        /// <summary>Attack grouping window; staggered same-track strings may join within 25ms.</summary>
        public long AttackClusterUs { get; set; } = 0;

        /// <summary>和声观察窗口：320ms（覆盖约半拍到一拍的和声延续）</summary>
        public long HarmonicWindowUs { get; set; } = 320_000;

        /// <summary>和弦最小稳定保持时间：240ms（防止短促装饰音引发频繁切槽）</summary>
        public long MinChordStabilityUs { get; set; } = 240_000;

        /// <summary>切换迟滞阈值：新和弦得分需超过旧和弦至少 0.14 才切换</summary>
        public double HysteresisMargin { get; set; } = 0.14;
    }

    public static class ChordAnalyzerV2
    {
        /// <summary>
        /// 游戏内置 24 个和弦在 6~1 弦（低音 6 弦 -> 高音 1 弦）的标准发声 MIDI 音高表
        /// 用于和弦模式下精准分配拨弦（1~6 键）及判断单音是否可直接借用当前和弦弹出
        /// </summary>
        public static readonly IReadOnlyDictionary<ChordId, byte[]> ChordStringPitches =
            new Dictionary<ChordId, byte[]>
            {
                // P1: G, Am, Bm, C, D, Em
                [ChordId.G]      = new byte[] { 43, 47, 50, 55, 59, 67 }, // 6弦->1弦: G2,B2,D3,G3,B3,G4
                [ChordId.Am]     = new byte[] { 45, 45, 52, 57, 60, 64 }, // A2,E3,A3,C4,E4
                [ChordId.Bm]     = new byte[] { 47, 47, 54, 59, 62, 66 }, // B2,F#3,B3,D4,F#4
                [ChordId.C]      = new byte[] { 48, 48, 52, 55, 60, 64 }, // C3,E3,G3,C4,E4
                [ChordId.D]      = new byte[] { 50, 45, 50, 57, 62, 66 }, // D3,A3,D4,F#4
                [ChordId.Em]     = new byte[] { 40, 47, 52, 55, 59, 64 }, // E2,B2,E3,G3,B3,E4

                // P2: Cmaj7, C7, Cm7, G7, C9, Cm9
                [ChordId.Cmaj7]  = new byte[] { 48, 48, 52, 55, 59, 64 },
                [ChordId.C7]     = new byte[] { 48, 48, 52, 58, 60, 64 },
                [ChordId.Cm7]    = new byte[] { 48, 48, 55, 58, 63, 67 },
                [ChordId.G7]     = new byte[] { 43, 47, 50, 55, 59, 65 },
                [ChordId.C9]     = new byte[] { 48, 48, 52, 58, 62, 67 },
                [ChordId.Cm9]    = new byte[] { 48, 48, 51, 58, 62, 67 },

                // P3: C5, D5, G5, A5, Cmaj9, Cadd9
                [ChordId.C5]     = new byte[] { 48, 48, 55, 60, 60, 67 },
                [ChordId.D5]     = new byte[] { 50, 50, 57, 62, 62, 69 },
                [ChordId.G5]     = new byte[] { 43, 50, 55, 55, 62, 67 },
                [ChordId.A5]     = new byte[] { 45, 45, 52, 57, 57, 64 },
                [ChordId.Cmaj9]  = new byte[] { 48, 48, 52, 59, 62, 67 },
                [ChordId.Cadd9]  = new byte[] { 48, 48, 52, 55, 62, 64 },

                // P4: G/B, Am7/G, D/F#, F/A, A, Dm
                [ChordId.G_B]    = new byte[] { 47, 47, 50, 55, 62, 67 },
                [ChordId.Am7_G]  = new byte[] { 43, 45, 52, 55, 60, 64 },
                [ChordId.D_Fsharp] = new byte[] { 42, 45, 50, 57, 62, 66 },
                [ChordId.F_A]    = new byte[] { 45, 45, 53, 57, 60, 65 },
                [ChordId.A]      = new byte[] { 45, 45, 52, 57, 61, 64 },
                [ChordId.Dm]     = new byte[] { 50, 45, 50, 57, 62, 65 }
            };

        /// <summary>
        /// 自动寻找最佳半音移调量 (-6 ~ +5)，优先保留游戏和弦轮可表达的 C/G/D/F 大调及其关系小调。
        /// </summary>
        public static int FindBestSemitoneTranspose(IReadOnlyList<RawMidiNote> notes)
        {
            if (notes == null || notes.Count == 0) return 0;

            // Equal-priority pitch-class profiles for C/G/D/F major and their relative minors.
            // In particular, retain F# in D/G and Bb in F instead of forcing these songs toward C.
            int[][] supportedScales =
            [
                [0, 2, 4, 5, 7, 9, 11],       // C / Am
                [0, 2, 4, 6, 7, 9, 11],       // G / Em
                [1, 2, 4, 6, 7, 9, 11],       // D / Bm
                [0, 2, 4, 5, 7, 9, 10]        // F / Dm
            ];

            double[] pcHistogram = new double[12];
            foreach (var n in notes)
            {
                if (n.Channel == 9) continue;
                double durWeight = Math.Clamp((n.DurationUs > 0 ? n.DurationUs : 250_000) / 100_000.0, 0.3, 8.0);
                double bassBoost = n.NoteNumber < 60 ? 1.8 : 1.0;
                pcHistogram[n.NoteNumber % 12] += durWeight * (n.Velocity / 127.0) * bassBoost;
            }

            double totalHistogram = pcHistogram.Sum();
            if (totalHistogram <= 0.001) return 0;

            int bestShift = 0;
            double bestScore = double.MinValue;

            for (int shift = -6; shift <= 5; shift++)
            {
                double bestScaleScore = double.MinValue;
                foreach (var scale in supportedScales)
                {
                    double scaleScore = 0;
                    for (int pc = 0; pc < 12; pc++)
                    {
                        int shiftedPc = (pc + shift + 12) % 12;
                        scaleScore += pcHistogram[pc] * (Array.IndexOf(scale, shiftedPc) >= 0 ? 1.0 : -0.75);
                    }
                    bestScaleScore = Math.Max(bestScaleScore, scaleScore);
                }

                // Keep the original key when it is already supported; transpose only for a
                // clear improvement, with equal costs in either direction.
                double score = bestScaleScore - Math.Abs(shift) * 0.055 * totalHistogram;

                if (score > bestScore)
                {
                    bestScore = score;
                    bestShift = shift;
                }
            }

            return bestShift;
        }

        /// <summary>
        /// 重写后的高精度 MIDI 和弦识别主入口
        /// </summary>
        public static List<ChordEvent> AnalyzeDetailed(
            IReadOnlyList<RawMidiNote> rawNotes,
            out int appliedSemitoneShift,
            ChordAnalyzerV2Options? options = null)
        {
            appliedSemitoneShift = 0;
            if (rawNotes == null || rawNotes.Count == 0)
                return new List<ChordEvent>();

            options ??= new ChordAnalyzerV2Options();
            var filtered = rawNotes.Where(n => n.Channel != 9).OrderBy(n => n.StartUs).ToList();
            if (filtered.Count == 0)
                return new List<ChordEvent>();

            // 1. 自动半音移调对齐到游戏和弦轮可表达的调性
            if (options.EnableAutoTranspose)
            {
                appliedSemitoneShift = FindBestSemitoneTranspose(filtered);
            }

            int shift = appliedSemitoneShift;
            var notes = (shift == 0)
                ? filtered
                : filtered.Select(n => PhraseOctaveFolder.CloneWithPitch(n,
                    Math.Clamp(n.NoteNumber + shift, 0, 127))).ToList();

            // 2. 起音聚类
            var clusters = ClusterAttacks(notes, options.AttackClusterUs);
            var results = new List<ChordEvent>(clusters.Count);

            ChordId currentChord = ChordId.Unknown;
            long lastSwitchTimeUs = -options.MinChordStabilityUs;
            int windowEndIndex = 0;
            var activeWindow = new LinkedList<RawMidiNote>();
            var expirations = new PriorityQueue<LinkedListNode<RawMidiNote>, long>();

            foreach (var cluster in clusters)
            {
                long attackTime = cluster[0].StartUs;
                long winEnd = attackTime + options.HarmonicWindowUs;

                double[] evidence = new double[12];
                double totalEvidence = 0;
                int lowestMidi = int.MaxValue;
                byte lowestBassPc = 255;
                var activeNotes = new List<RawMidiNote>();

                while (windowEndIndex < notes.Count && notes[windowEndIndex].StartUs < winEnd)
                {
                    var note = notes[windowEndIndex++];
                    var node = activeWindow.AddLast(note);
                    long noteEnd = note.StartUs + (note.DurationUs > 0 ? note.DurationUs : 250_000);
                    expirations.Enqueue(node, noteEnd);
                }
                while (expirations.TryPeek(out _, out long endUs) && endUs <= attackTime)
                    activeWindow.Remove(expirations.Dequeue());

                // 3. 声部加权证据收集（强化低音根音与长音，抑制高音短促旋律经过音）
                foreach (var n in activeWindow)
                {
                    long noteEnd = n.StartUs + (n.DurationUs > 0 ? n.DurationUs : 250_000);
                    if (n.StartUs >= winEnd || noteEnd <= attackTime) continue;

                    long overlap = Math.Min(noteEnd, winEnd) - Math.Max(n.StartUs, attackTime);
                    if (overlap <= 0) continue;

                    activeNotes.Add(n);

                    double durationRatio = overlap / (double)options.HarmonicWindowUs;
                    double velFactor = Math.Clamp(n.Velocity / 127.0, 0.35, 1.0);

                    // 关键改进：低音(<60)赋予 2.4 倍和声权重；高音短音(<120ms)视为旋律经过音，权重降至 0.35
                    double roleWeight = 1.0;
                    if (n.NoteNumber < 55) roleWeight = 2.4;
                    else if (n.NoteNumber < 64) roleWeight = 1.6;
                    else if (n.DurationUs < 120_000) roleWeight = 0.35;

                    double weight = durationRatio * velFactor * roleWeight;
                    byte pc = (byte)(n.NoteNumber % 12);
                    evidence[pc] += weight;
                    totalEvidence += weight;

                    // 仅用中低音区或足够长的音作为有效低音判据
                    if (n.NoteNumber < lowestMidi && (n.NoteNumber <= 64 || cluster.Count == 1))
                    {
                        lowestMidi = n.NoteNumber;
                        lowestBassPc = pc;
                    }
                }

                if (totalEvidence < 0.04) continue;

                // 4. 对 24 个内置和弦进行分层打分
                var (bestChord, bestScore, currentScore) = Score24ChordsEnhanced(
                    evidence, totalEvidence, lowestBassPc, currentChord, options.Mode);

                // 5. 防抖稳定区判断
                bool canSwitch = (currentChord == ChordId.Unknown)
                    || (bestChord != currentChord
                        && (attackTime - lastSwitchTimeUs >= options.MinChordStabilityUs || bestScore >= currentScore + 0.32)
                        && bestScore >= currentScore + options.HysteresisMargin);

                if (canSwitch && bestChord != ChordId.Unknown)
                {
                    currentChord = bestChord;
                    lastSwitchTimeUs = attackTime;
                }

                ChordId finalChord = (currentChord != ChordId.Unknown) ? currentChord : bestChord;
                var def = ChordAnalyzer.GetDefinition(finalChord);

                // 6. 识别演奏形态与上下扫方向
                var (articulation, direction) = ClassifyArticulationAndDirection(cluster, activeNotes);

                results.Add(new ChordEvent
                {
                    StartUs = attackTime,
                    EndUs = winEnd,
                    Chord = finalChord,
                    Page = def?.Page ?? 1,
                    Slot = def?.Slot ?? 1,
                    Confidence = Math.Clamp(bestScore, 0.0, 1.0),
                    SourceNotes = activeNotes,
                    AttackNotes = cluster,
                    Articulation = articulation,
                    Direction = direction
                });
            }

            return results;
        }

        /// <summary>
        /// 改进版 24 和弦评分器：修复五度和弦分母漏洞与九和弦误判
        /// </summary>
        public static (ChordId BestChord, double BestScore, double CurrentScore) Score24ChordsEnhanced(
            double[] evidence,
            double totalEvidence,
            byte lowestBassPc,
            ChordId currentChord,
            ChordRecognitionMode mode = ChordRecognitionMode.Loose)
        {
            ChordId bestChord = ChordId.C;
            double bestScore = double.MinValue;
            double currentScore = 0;

            foreach (var def in ChordAnalyzer.SupportedChords)
            {
                double inChordEvidence = 0;
                double outChordEvidence = 0;
                int presentCount = 0;

                for (byte pc = 0; pc < 12; pc++)
                {
                    if (def.PitchClasses.Contains(pc))
                    {
                        inChordEvidence += evidence[pc];
                        if (evidence[pc] > 0.04 * totalEvidence) presentCount++;
                    }
                    else
                    {
                        outChordEvidence += evidence[pc];
                    }
                }

                double coverage = inChordEvidence / Math.Max(0.001, totalEvidence);
                // 修复五度和弦漏洞：分母至少按 3 音三和弦归一化，避免 2 音五度和弦轻易拿满分
                double completeness = presentCount / Math.Max(3.0, def.PitchClasses.Count);

                // 根音与低音匹配奖励
                double rootBonus = (evidence[def.RootPitchClass] > 0.08 * totalEvidence) ? 0.16 : -0.10;
                double bassBonus = 0;
                if (def.SpecialBassPitchClass.HasValue)
                {
                    bassBonus = (lowestBassPc == def.SpecialBassPitchClass.Value) ? 0.32 : -0.50;
                }
                else if (lowestBassPc == def.RootPitchClass)
                {
                    bassBonus = 0.22;
                }

                // Keep chord-page bias small so accurate extensions and inversions can compete.
                double tierPrior = def.Page switch
                {
                    1 => 0.02,
                    4 => 0.02,
                    2 => 0.01,
                    3 => def.PitchClasses.Count == 2 ? -0.08 : 0.01,
                    _ => 0.0
                };

                double score = 0.62 * coverage
                             + 0.26 * completeness
                             + rootBonus
                             + bassBonus
                             + tierPrior
                             - 0.35 * (outChordEvidence / Math.Max(0.001, totalEvidence));
                if (mode == ChordRecognitionMode.Strict)
                    score -= 0.30 * (outChordEvidence / Math.Max(0.001, totalEvidence));
                else if (mode == ChordRecognitionMode.BassRoot)
                    score += bassBonus * 0.75;

                if (def.PitchClasses.Count == 2)
                {
                    int minorThird = (def.RootPitchClass + 3) % 12;
                    int majorThird = (def.RootPitchClass + 4) % 12;
                    if (evidence[minorThird] + evidence[majorThird] > 0.08 * totalEvidence)
                        score -= 0.35;
                }

                int extensionPc = def.Id switch
                {
                    ChordId.Cmaj7 => 11,
                    ChordId.C7 or ChordId.Cm7 => 10,
                    ChordId.G7 => 5,
                    ChordId.C9 or ChordId.Cm9 or ChordId.Cmaj9 or ChordId.Cadd9 => 2,
                    _ => -1
                };
                if (extensionPc >= 0 && evidence[extensionPc] < 0.08 * totalEvidence)
                    score -= 0.28;

                if (def.Id == currentChord)
                {
                    score += 0.12;
                    currentScore = score;
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestChord = def.Id;
                }
            }

            return (bestChord, bestScore, currentScore);
        }

        internal static (ChordArticulation Articulation, StrumDirectionHint Direction) ClassifyArticulationAndDirection(
            IReadOnlyList<RawMidiNote> cluster,
            IReadOnlyList<RawMidiNote> activeNotes)
        {
            if (cluster.Count == 1)
            {
                return (ChordArticulation.Arpeggio, StrumDirectionHint.Unknown);
            }

            long spanUs = cluster.Max(n => n.StartUs) - cluster.Min(n => n.StartUs);

            // >= 3 个音同时或微时差进入：优先使用扫弦 (Strum)，比死板的数字键齐按 (Block) 更有吉他真实感
            if (cluster.Count >= 3)
            {
                var ordered = cluster.OrderBy(n => n.StartUs).ThenBy(n => n.NoteNumber).ToList();
                int asc = 0, desc = 0;
                for (int i = 0; i < ordered.Count - 1; i++)
                {
                    int diff = ordered[i + 1].NoteNumber - ordered[i].NoteNumber;
                    if (diff > 0) asc++;
                    else if (diff < 0) desc++;
                }

                if (spanUs >= 12_000)
                {
                    if (asc > desc) return (ChordArticulation.Strum, StrumDirectionHint.LowToHigh); // 低音到高音 -> 下扫(右键)
                    if (desc > asc) return (ChordArticulation.Strum, StrumDirectionHint.HighToLow); // 高音到低音 -> 上扫(左键)
                }

                return (ChordArticulation.Strum, StrumDirectionHint.Unknown);
            }

            // 双音齐奏 (如根音+高音旋律)：采用 Block 双弦同时点按
            if (cluster.Count == 2 && spanUs <= 22_000)
            {
                return (ChordArticulation.Block, StrumDirectionHint.Unknown);
            }

            return (ChordArticulation.Arpeggio, StrumDirectionHint.Unknown);
        }

        private static List<List<RawMidiNote>> ClusterAttacks(IReadOnlyList<RawMidiNote> sortedNotes, long windowUs)
        {
            var clusters = new List<List<RawMidiNote>>();
            int i = 0;
            while (i < sortedNotes.Count)
            {
                long start = sortedNotes[i].StartUs;
                var batch = new List<RawMidiNote> { sortedNotes[i] };
                int j = i + 1;
                while (j < sortedNotes.Count && (sortedNotes[j].StartUs - start) <= windowUs)
                {
                    if (!AttackClusterPolicy.CanJoin(batch, sortedNotes[j], start,
                            windowUs * 25 / 30, GuitarTuning.Standard))
                        break;

                    batch.Add(sortedNotes[j]);
                    j++;
                }
                clusters.Add(batch);
                i = j;
            }
            return clusters;
        }
    }
}

