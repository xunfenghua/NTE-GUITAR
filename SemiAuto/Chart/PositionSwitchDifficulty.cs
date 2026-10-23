namespace Nte.GuitarChart.Core;

/// <summary>Estimates transition difficulty for chart planning; it is not a score weight.</summary>
public static class PositionSwitchDifficulty
{
    public static double CalculateWeight(GuitarPosition from, GuitarPosition to)
    {
        var pageDistance = Math.Min(Math.Abs(to.Page - from.Page), 3);
        var direct = Math.Abs(to.Slot - from.Slot);
        var slotDistance = Math.Min(direct, 6 - direct);
        return 1.0 + 0.25 * pageDistance + 0.10 * Math.Min(slotDistance, 3);
    }
}
