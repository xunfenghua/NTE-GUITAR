using MidiAutoPlayer.Models;
using System;
using System.Collections.Generic;
using System.Linq;

namespace MidiAutoPlayer.Guitar
{
    /// <summary>
    /// 速度控制状态管理：支持导入自动变速与用户手动改速覆盖
    /// </summary>
    public sealed class SpeedControlManager
    {
        public const int MinAllowedSpeedPercent = 25;
        public const int MaxAllowedSpeedPercent = 200;

        /// <summary>是否处于自动变速模式（用户未手动覆盖）</summary>
        public bool IsAutoSpeed { get; private set; } = true;

        /// <summary>算法根据 20ms 约束自动计算出的推荐速度百分比 (25~100)</summary>
        public int AutoRecommendedSpeedPercent { get; private set; } = 100;

        /// <summary>用户手动设定的速度百分比（若为 null 则使用自动速度）</summary>
        public int? UserOverrideSpeedPercent { get; private set; }

        /// <summary>当前实际生效的速度百分比</summary>
        public int EffectiveSpeedPercent => IsAutoSpeed
            ? AutoRecommendedSpeedPercent
            : (UserOverrideSpeedPercent ?? AutoRecommendedSpeedPercent);

        /// <summary>时间轴缩放倍率 (TimeScale > 1.0 表示放慢，TimeUs_New = TimeUs_Raw * TimeScale)</summary>
        public double TimeScaleFactor => 100.0 / Math.Clamp(EffectiveSpeedPercent, MinAllowedSpeedPercent, MaxAllowedSpeedPercent);

        /// <summary>MIDI 导入时调用：更新自动变速结果（若用户未锁死手动速度，则自动生效）</summary>
        public void ApplyAutoSpeedOnMidiImport(int autoSpeedPercent, bool resetUserOverride = true)
        {
            AutoRecommendedSpeedPercent = Math.Clamp(autoSpeedPercent, MinAllowedSpeedPercent, 100);
            if (resetUserOverride)
            {
                IsAutoSpeed = true;
                UserOverrideSpeedPercent = null;
            }
        }

        /// <summary>用户在 UI 手动拖动速度滑块时调用</summary>
        public void SetUserManualSpeed(int userSpeedPercent)
        {
            IsAutoSpeed = false;
            UserOverrideSpeedPercent = Math.Clamp(userSpeedPercent, MinAllowedSpeedPercent, MaxAllowedSpeedPercent);
        }

        /// <summary>重置回自动配速</summary>
        public void ResetToAutoSpeed()
        {
            IsAutoSpeed = true;
            UserOverrideSpeedPercent = null;
        }
    }

    public sealed class MidiSpeedOptimizerOptions
    {
        /// <summary>游戏要求的硬件最短按键按压时长：20ms (20,000us)</summary>
        public long MinKeyHoldUs { get; set; } = 20_000;

        /// <summary>同键连续触发之间的最小抬起释放间隙：8ms (8,000us)</summary>
        public long MinKeyReleaseGapUs { get; set; } = 8_000;

        /// <summary>轮盘选槽后等待稳定的最小物理时间：50ms (50,000us)</summary>
        public long WheelMouseSettleUs { get; set; } = 50_000;

        /// <summary>控制动作（翻页/切模式）按键后的最小安全间隔：8ms</summary>
        public long ControlInterTapGapUs { get; set; } = 8_000;

        /// <summary>并发音符聚类容差：18ms 内的音符视为同一次齐奏/和弦，不计入连击间隔压缩</summary>
        public long SimultaneousToleranceUs { get; set; } = 18_000;

        /// <summary>密集间隔保护分位数；默认 1.0，按全曲最紧的有效间隔配速</summary>
        public double PercentileThreshold { get; set; } = 1.0;
    }

    public static class MidiSpeedOptimizer
    {
        /// <summary>
        /// 分析原始 MIDI 音符序列，自动计算满足“所有按键时长 >= 20ms”的最佳速度百分比 (25% ~ 100%)
        /// </summary>
        public static int CalculateAutoSpeedPercent(
            IReadOnlyList<RawMidiNote> rawNotes,
            MidiSpeedOptimizerOptions? options = null)
        {
            if (rawNotes == null || rawNotes.Count == 0)
                return 100;

            options ??= new MidiSpeedOptimizerOptions();

            // 1. 过滤打击乐通道并按起音时间排序
            var notes = rawNotes
                .Where(n => n.Channel != 9)
                .OrderBy(n => n.StartUs)
                .ToList();

            if (notes.Count <= 1)
                return 100;

            // 单次按键完整周期（按下 20ms + 抬起间隙 8ms = 28ms）
            long minSingleKeyCycleUs = options.MinKeyHoldUs + options.MinKeyReleaseGapUs;

            // 2. 聚合相同时刻的并发音符簇（Onset Clusters）
            var onsetTimes = new List<long>();
            var clusterMaxPitch = new List<byte>();
            int idx = 0;
            while (idx < notes.Count)
            {
                long start = notes[idx].StartUs;
                byte maxPitch = notes[idx].NoteNumber;
                int next = idx + 1;
                while (next < notes.Count && (notes[next].StartUs - start) <= options.SimultaneousToleranceUs)
                {
                    if (notes[next].NoteNumber > maxPitch)
                        maxPitch = notes[next].NoteNumber;
                    next++;
                }
                onsetTimes.Add(start);
                clusterMaxPitch.Add(maxPitch);
                idx = next;
            }

            if (onsetTimes.Count <= 1)
                return 100;

            var requiredScaleFactors = new List<double>();

            // 3. 逐音簇评估时间拉伸需求
            for (int i = 1; i < onsetTimes.Count; i++)
            {
                long deltaUs = onsetTimes[i] - onsetTimes[i - 1];
                if (deltaUs <= options.SimultaneousToleranceUs)
                    continue;

                bool isSamePitch = (clusterMaxPitch[i] == clusterMaxPitch[i - 1]);

                // 情况 A: 同音高连击（必然占用同一根弦，必须满足 20ms 按压 + 8ms 释放）
                if (isSamePitch)
                {
                    double scaleForRetrigger = (double)minSingleKeyCycleUs / deltaUs;
                    requiredScaleFactors.Add(scaleForRetrigger);
                }
                else
                {
                    // 情况 B: 异音连奏。若音高跨度较大（超过 4 个半音，高概率需要切槽或翻页），
                    // 前一个音必须至少按满 20ms，且留出选槽稳定时间的一半缓冲
                    int pitchDiff = Math.Abs(clusterMaxPitch[i] - clusterMaxPitch[i - 1]);
                    long requiredGapUs = options.MinKeyHoldUs + 5_000; // 基础：前音按满 20ms + 5ms 屏障缓冲

                    if (pitchDiff >= 6)
                    {
                        // 跨把位大跳，需预留单次翻页(20ms+8ms)或轮盘选槽准备时间
                        requiredGapUs += options.MinKeyHoldUs + options.ControlInterTapGapUs;
                    }

                    double scaleForTransition = (double)requiredGapUs / deltaUs;
                    requiredScaleFactors.Add(scaleForTransition);
                }
            }

            // 4. 检查单个音符本身的原始 MIDI 时值是否普遍过短
            foreach (var n in notes)
            {
                if (n.DurationUs > 2_000 && n.DurationUs < options.MinKeyHoldUs)
                {
                    // 若 MIDI 自身音符极短，记录适度拉伸系数（上限 1.5 倍，因编译器可自动延长短音）
                    double scaleForDuration = Math.Min(1.5, (double)options.MinKeyHoldUs / n.DurationUs);
                    requiredScaleFactors.Add(scaleForDuration);
                }
            }

            if (requiredScaleFactors.Count == 0)
                return 100;

            // 5. Use the tightest analyzed interval by default. A P95 speed can
            // leave short dense passages physically unreachable and cause the
            // scheduler to fall behind even when most of the song is playable.
            requiredScaleFactors.Sort();
            int pIndex = (int)Math.Clamp(
                Math.Floor(requiredScaleFactors.Count * options.PercentileThreshold),
                0,
                requiredScaleFactors.Count - 1);

            double targetScale = requiredScaleFactors[pIndex];

            // 若曲速本就从容（targetScale <= 1.0），保持 100% 原速，绝不主动加速
            if (targetScale <= 1.0)
                return 100;

            int recommendedPercent = (int)Math.Floor(100.0 / targetScale);
            return Math.Clamp(recommendedPercent, SpeedControlManager.MinAllowedSpeedPercent, 100);
        }

        /// <summary>
        /// 将生效的速度百分比应用到原始 MIDI 音符列表，生成变速后的音符副本
        /// </summary>
        public static List<RawMidiNote> ScaleMidiNotes(
            IReadOnlyList<RawMidiNote> rawNotes,
            double timeScaleFactor,
            long minHoldUs = 20_000)
        {
            if (rawNotes == null || rawNotes.Count == 0)
                return new List<RawMidiNote>();

            if (Math.Abs(timeScaleFactor - 1.0) < 1e-6)
            {
                // 即使 1.0 倍速，也确保基础 DurationUs 至少为 20ms
                return rawNotes.Select(n => Copy(n, n.StartUs, Math.Max(minHoldUs, n.DurationUs))).ToList();
            }

            var scaled = new List<RawMidiNote>(rawNotes.Count);
            foreach (var n in rawNotes)
            {
                long newStartUs = (long)Math.Round(n.StartUs * timeScaleFactor);
                long newDurUs = Math.Max(minHoldUs, (long)Math.Round(n.DurationUs * timeScaleFactor));
                scaled.Add(Copy(n, newStartUs, newDurUs));
            }
            return scaled;
        }
        private static RawMidiNote Copy(RawMidiNote n, long startUs, long durationUs) => new()
        {
            StartUs = startUs, DurationUs = durationUs, NoteNumber = n.NoteNumber,
            Velocity = n.Velocity, Channel = n.Channel, TrackId = n.TrackId, Program = n.Program,
            IsPrimaryMelody = n.IsPrimaryMelody,
            OrigString = n.OrigString, OrigFret = n.OrigFret, VoiceIndex = n.VoiceIndex,
            IsLetRing = n.IsLetRing, IsPalmMute = n.IsPalmMute, IsBrushChord = n.IsBrushChord,
            IsBrushUp = n.IsBrushUp, ExplicitChordName = n.ExplicitChordName, SourceFormat = n.SourceFormat
        };
    }
}

