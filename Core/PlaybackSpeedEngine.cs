using System;
using System.Collections.Generic;
using System.Linq;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Core.Compilation;

/// <summary>
/// 1. 速度倍率强类型校验模型 (0.25x ~ 2.00x)
/// </summary>
public sealed record PlaybackSpeedSetting(double Multiplier)
{
    public const double MinSpeed = 0.25;
    public const double MaxSpeed = 2.00;

    public static PlaybackSpeedSetting Validate(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value < MinSpeed || value > MaxSpeed)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"演奏速度倍率必须在 {MinSpeed:0.00}x ～ {MaxSpeed:0.00}x 之间。");
        }
        // 规范化到小数点后两位
        return new PlaybackSpeedSetting(Math.Round(value, 2));
    }
}

/// <summary>
/// 2. 硬件物理安全常数 (单位：微秒 us，绝对不随曲目 speed 压缩！)
/// </summary>
public sealed record HardwareTimingConstraints(
    long PageKeyHoldUs = 24_000,       // R/T 翻页按键最小按下保持 24ms
    long PageSwitchCooldownUs = 28_000,// 连续翻页之间的最小释放冷却 28ms
    long SlotSelectSettleUs = 20_000,  // 鼠标点击轮盘槽位后等待游戏生效的沉降时间 20ms
    long StringMinHoldUs = 22_000,     // 琴弦/琴键最小按下保持时间 22ms (防低于1帧被吞)
    long SameKeyReleaseGapUs = 18_000, // 同键位连续重触发的最小抬起间隔 18ms
    long SafetyMarginUs = 4_000        // 操作系统线程调度安全余量 4ms
);

public sealed record CompiledGuitarNoteEvent(
    long EffectiveStartUs,
    long EffectiveDurationUs,
    int Pitch,
    int TargetPage,       // 0..4 (P0..P4)
    int TargetSlot,       // 1..6 (Slot 1..6, P0空弦时为0)
    int PhysicalString,   // 1..6
    int GameKeyScanCode   // 0x02..0x07
);

public sealed record ReachabilityIssue(
    long AtEffectiveUs,
    string IssueType,
    string Message,
    double RecommendedMaxSpeed
);

public sealed record ReachabilityReport(
    bool IsFullyReachable,
    double TestedSpeed,
    double RecommendedMaxSpeed,
    IReadOnlyList<ReachabilityIssue> Issues
);

public sealed record SetSpeedResultDto(
    double Speed,
    long EffectiveDurationMs,
    bool IsFullyReachable,
    double RecommendedMaxSpeed,
    int IssueCount,
    string? FirstIssueSummary
);

/// <summary>
/// 3. 双时钟域缩放与可达性验证器
/// </summary>
public static class PlaybackSpeedEngine
{
    /// <summary>
    /// 阶段 A：在进入动作编译器之前，对原始 MIDI 音符时间轴做统一缩放
    /// </summary>
    public static IReadOnlyList<RawMidiNote> ScaleMidiNotes(
        IReadOnlyList<RawMidiNote> rawNotes,
        double speed,
        long minNoteHoldUs = 22_000)
    {
        var validated = PlaybackSpeedSetting.Validate(speed);
        double s = validated.Multiplier;

        return rawNotes
            .Select(n =>
            {
                long effStart = checked((long)Math.Round(n.StartUs / s));
                long effDur = Math.Max(minNoteHoldUs, checked((long)Math.Round(n.DurationUs / s)));
                return new RawMidiNote
                {
                    StartUs = effStart,
                    DurationUs = effDur,
                    NoteNumber = n.NoteNumber,
                    Velocity = n.Velocity,
                    Channel = n.Channel,
                    TrackId = n.TrackId,
                    Program = n.Program,
                    BarIndex = n.BarIndex,
                    BeatInBar = n.BeatInBar,
                    BeatsPerBar = n.BeatsPerBar,
                    BeatUnitDenominator = n.BeatUnitDenominator,
                    HasMeterPosition = n.HasMeterPosition,
                    OrigString = n.OrigString,
                    OrigFret = n.OrigFret,
                    VoiceIndex = n.VoiceIndex,
                    IsLetRing = n.IsLetRing,
                    IsPalmMute = n.IsPalmMute,
                    IsBrushChord = n.IsBrushChord,
                    IsBrushUp = n.IsBrushUp,
                    ExplicitChordName = n.ExplicitChordName,
                    SourceFormat = n.SourceFormat
                };
            })
            .OrderBy(n => n.StartUs)
            .ThenBy(n => n.NoteNumber)
            .ToArray();
    }

    /// <summary>
    /// 阶段 C：对吉他品格/和弦编译产物执行硬件物理可达性校验，并计算最高安全倍速
    /// </summary>
    public static ReachabilityReport ValidateGuitarTimeline(
        IReadOnlyList<CompiledGuitarNoteEvent> events,
        double currentSpeed,
        HardwareTimingConstraints? hw = null,
        int totalPages = 5)
    {
        hw ??= new HardwareTimingConstraints();
        var issues = new List<ReachabilityIssue>();
        double recommendedMaxSpeed = PlaybackSpeedSetting.MaxSpeed;

        if (events.Count <= 1)
        {
            return new ReachabilityReport(true, currentSpeed, currentSpeed, issues);
        }

        // 记录每根弦上一次释放的时间戳
        var lastStringReleaseUs = new Dictionary<int, long>();

        for (int i = 0; i < events.Count; i++)
        {
            var cur = events[i];

            // 检查 1：同弦连续拨弦的 ReleaseGap 冲突
            if (lastStringReleaseUs.TryGetValue(cur.PhysicalString, out long prevReleaseUs))
            {
                long gapUs = cur.EffectiveStartUs - prevReleaseUs;
                if (gapUs < hw.SameKeyReleaseGapUs)
                {
                    long prevStartUs = i > 0 ? events[i - 1].EffectiveStartUs : 0;
                    long availSpan = Math.Max(1, cur.EffectiveStartUs - prevStartUs);
                    long neededSpan = hw.StringMinHoldUs + hw.SameKeyReleaseGapUs;
                    double safeSpd = Math.Floor((currentSpeed * availSpan / neededSpan) * 20.0) / 20.0;
                    safeSpd = Math.Clamp(safeSpd, PlaybackSpeedSetting.MinSpeed, currentSpeed);
                    recommendedMaxSpeed = Math.Min(recommendedMaxSpeed, safeSpd);

                    issues.Add(new ReachabilityIssue(
                        cur.EffectiveStartUs,
                        "SameStringRetrigger",
                        $"物理 {cur.PhysicalString} 弦连续重触发间隔仅 {gapUs / 1000.0:F1}ms，小于防吞音最小释放间隙 {hw.SameKeyReleaseGapUs / 1000.0:F1}ms。",
                        safeSpd
                    ));
                }
            }

            // 计算当前音符的实际释放时间（若与后续同弦音符过近，自动截断尾音以腾出 ReleaseGap）
            long idealReleaseUs = cur.EffectiveStartUs + cur.EffectiveDurationUs;
            lastStringReleaseUs[cur.PhysicalString] = Math.Max(
                cur.EffectiveStartUs + hw.StringMinHoldUs,
                idealReleaseUs
            );

            if (i == 0) continue;
            var prev = events[i - 1];

            // 检查 2：跨页 (R/T) + 跨轮盘槽位 (Slot 1..6) 所需物理预备时间窗
            int pageDiff = Math.Abs(cur.TargetPage - prev.TargetPage);
            // 若支持环形翻页则取 min(diff, totalPages - diff)
            int pageSteps = Math.Min(pageDiff, Math.Max(0, totalPages - pageDiff));
            bool slotChanged = cur.TargetSlot != 0 && cur.TargetSlot != prev.TargetSlot;

            if (pageSteps == 0 && !slotChanged) continue;

            long requiredPrepUs = 0;
            if (pageSteps > 0)
            {
                requiredPrepUs += pageSteps * hw.PageKeyHoldUs + (pageSteps - 1) * hw.PageSwitchCooldownUs;
            }
            if (slotChanged)
            {
                requiredPrepUs += hw.SlotSelectSettleUs;
            }
            requiredPrepUs += hw.SafetyMarginUs;

            long availableWindowUs = cur.EffectiveStartUs - prev.EffectiveStartUs;
            if (availableWindowUs < requiredPrepUs)
            {
                double safeSpd = Math.Floor((currentSpeed * availableWindowUs / requiredPrepUs) * 20.0) / 20.0;
                safeSpd = Math.Clamp(safeSpd, PlaybackSpeedSetting.MinSpeed, currentSpeed);
                recommendedMaxSpeed = Math.Min(recommendedMaxSpeed, safeSpd);

                issues.Add(new ReachabilityIssue(
                    cur.EffectiveStartUs,
                    "PageOrSlotPrepOverflow",
                    $"在 {FormatUs(cur.EffectiveStartUs)} 处需完成 {pageSteps} 次翻页" +
                    (slotChanged ? $"并切换至 Slot {cur.TargetSlot}" : "") +
                    $"，需物理耗时 {requiredPrepUs / 1000.0:F1}ms，但距上一音仅 {availableWindowUs / 1000.0:F1}ms（建议使用 ≤ {safeSpd:0.00}x）。",
                    safeSpd
                ));
            }
        }

        return new ReachabilityReport(
            IsFullyReachable: issues.Count == 0,
            TestedSpeed: currentSpeed,
            RecommendedMaxSpeed: recommendedMaxSpeed,
            Issues: issues
        );
    }

    private static string FormatUs(long us)
    {
        long ms = Math.Max(0, us) / 1000;
        return $"{ms / 60000:00}:{(ms / 1000) % 60:00}.{ms % 1000:000}";
    }
}
