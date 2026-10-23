namespace Nte.GuitarChart.Core;

/// <summary>Monotonic MIDI playhead advanced only while the practice driver key is held.</summary>
internal sealed class PracticeTimeline
{
    private long _chartOffsetUs;
    private long _wallAnchorUs;
    private double _speedRatio = 1.0;

    public bool IsRunning { get; private set; }

    public void Start(long chartOffsetUs, long wallClockUs, int speedPercent)
    {
        _chartOffsetUs = Math.Max(0, chartOffsetUs);
        _wallAnchorUs = wallClockUs;
        _speedRatio = NormalizeSpeed(speedPercent);
        IsRunning = true;
    }

    public void Resume(long wallClockUs)
    {
        if (IsRunning) return;
        _wallAnchorUs = wallClockUs;
        IsRunning = true;
    }

    public void Pause(long wallClockUs)
    {
        if (!IsRunning) return;
        _chartOffsetUs = CurrentTimeUs(wallClockUs);
        IsRunning = false;
    }

    public long CurrentTimeUs(long wallClockUs) => IsRunning
        ? Math.Max(0, _chartOffsetUs + (long)Math.Max(0, (wallClockUs - _wallAnchorUs) * _speedRatio))
        : _chartOffsetUs;

    public void Seek(long chartTimeUs, long wallClockUs)
    {
        _chartOffsetUs = Math.Max(0, chartTimeUs);
        _wallAnchorUs = wallClockUs;
    }

    public void SetSpeed(int speedPercent, long wallClockUs)
    {
        _chartOffsetUs = CurrentTimeUs(wallClockUs);
        _wallAnchorUs = wallClockUs;
        _speedRatio = NormalizeSpeed(speedPercent);
    }

    private static double NormalizeSpeed(int speedPercent) => Math.Clamp(speedPercent, 25, 200) / 100d;
}
