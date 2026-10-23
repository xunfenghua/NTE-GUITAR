namespace Nte.GuitarChart.Core;

public enum HitGrade { Perfect, Good, Miss }

/// <summary>Timing classification used only to color note feedback.</summary>
public static class HitJudgement
{
    public static HitGrade Judge(long errorUs, long perfectWindowUs, long goodWindowUs)
    {
        var absolute = Math.Abs(errorUs);
        if (absolute <= perfectWindowUs) return HitGrade.Perfect;
        if (absolute <= goodWindowUs) return HitGrade.Good;
        return HitGrade.Miss;
    }

    public static long ToOriginalTimelineErrorUs(long wallClockErrorUs, double speedRatio)
    {
        if (speedRatio <= 0) throw new ArgumentOutOfRangeException(nameof(speedRatio));
        return (long)Math.Round(wallClockErrorUs * speedRatio);
    }

    public static long ClampWindowToAvoidDoubleHit(long requestedWindowUs, long nearestOtherTargetGapUs)
    {
        var safeHalfGap = Math.Max(0, nearestOtherTargetGapUs / 2 - 1);
        return Math.Min(requestedWindowUs, safeHalfGap);
    }
}
