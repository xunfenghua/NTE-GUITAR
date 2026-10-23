using System.Diagnostics;
using Nte.GuitarChart.Core;

namespace Nte.GuitarChart.WinForms;

/// <summary>Wall-clock driver used exclusively by the two rhythm modes.</summary>
internal sealed class RhythmSessionController
{
    private readonly Stopwatch _clock = new();
    private long _startTimestampUs;
    private long _startOffsetUs;
    private double _speedRatio = 1.0;
    public void Configure(int speedPercent)
    {
        _speedRatio = Math.Clamp(speedPercent / 100.0, 0.25, 2.0);
    }
    public HitGrade JudgeInput(long targetTimeUs, long actualTimeUs)
    {
        long error = HitJudgement.ToOriginalTimelineErrorUs(actualTimeUs - targetTimeUs, _speedRatio);
        return HitJudgement.Judge(error, 80_000, 160_000);
    }
    public void SetSpeedRatio(double speedRatio) => _speedRatio = Math.Clamp(speedRatio, 0.25, 2.0);
    public void Start(long offsetUs) { _startOffsetUs = offsetUs; _startTimestampUs = Stopwatch.GetTimestamp() * 1_000_000L / Stopwatch.Frequency; _clock.Restart(); }
    public long CurrentTimeUs(int speedPercent) => _startOffsetUs + (long)(_clock.ElapsedTicks * 1_000_000d / Stopwatch.Frequency * speedPercent / 100d);
    public long ToTimelineTimeUs(long monotonicTimeUs, int speedPercent) =>
        _startOffsetUs + (long)((monotonicTimeUs - _startTimestampUs) * speedPercent / 100d);
    public void Stop() => _clock.Stop();
}
