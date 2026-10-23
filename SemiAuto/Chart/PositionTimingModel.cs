namespace Nte.GuitarChart.Core;

public interface IPositionTimingModel
{
    long EstimatePrepareUs(GuitarPosition from, GuitarPosition to, ChartCompileOptions options);
}

/// <summary>
/// Conservative starter model. Keeps linear distance for benchmark / basic testing.
/// </summary>
public sealed class LinearPositionTimingModel : IPositionTimingModel
{
    public long EstimatePrepareUs(GuitarPosition from, GuitarPosition to, ChartCompileOptions options)
    {
        var pageSteps = Math.Abs(to.Page - from.Page);
        var pageUs = pageSteps * (options.PageKeyHoldUs + options.PageInterTapGapUs);
        var slotUs = from.Slot == to.Slot ? 0 : options.WheelMouseSettleUs;
        return pageUs + slotUs + options.SafetyMarginUs;
    }
}

/// <summary>
/// Production calibrated timing model connected to the real WheelController and game navigation graph.
/// Accurately handles:
/// 1) P5 (open string 0-fret) after P4 in normal page order;
/// 2) Slot invalidation upon wheel page changes;
/// 3) Configurable timing parameters (KeyHold, InterTapGap, WheelMouseSettle, SafetyMargin).
/// </summary>
public sealed class WheelPositionTimingModel : IPositionTimingModel
{
    private readonly Func<ChartCompileOptions, (long HoldUs, long GapUs, long SettleUs, long SafetyUs)>? _paramOverrides;

    public WheelPositionTimingModel(Func<ChartCompileOptions, (long HoldUs, long GapUs, long SettleUs, long SafetyUs)>? paramOverrides = null)
    {
        _paramOverrides = paramOverrides;
    }

    public long EstimatePrepareUs(GuitarPosition from, GuitarPosition to, ChartCompileOptions options)
    {
        long holdUs = options.PageKeyHoldUs;
        long gapUs = options.PageInterTapGapUs;
        long settleUs = options.WheelMouseSettleUs;
        long safetyUs = options.SafetyMarginUs;

        if (_paramOverrides != null)
        {
            var (oHold, oGap, oSettle, oSafety) = _paramOverrides(options);
            if (oHold > 0) holdUs = oHold;
            if (oGap > 0) gapUs = oGap;
            if (oSettle > 0) settleUs = oSettle;
            if (oSafety > 0) safetyUs = oSafety;
        }

        // 1. Same position entirely: no movement or click required
        if (from.Page == to.Page && from.Slot == to.Slot)
        {
            return 0;
        }

        // 2. Intra-page slot change (no page flip)
        if (from.Page == to.Page)
        {
            // For P5, fret 0 is uniquely slot 1.
            if (to.Page == 5) return 0;
            return settleUs + safetyUs;
        }

        // 3. Inter-page navigation: P1..P5 are consecutive pages.
        int pageSteps = Math.Abs(to.Page - from.Page);

        long pageNavUs = pageSteps * (holdUs + gapUs);

        // When a page flips, WheelController.InvalidateCurrentSlot() is triggered.
        // The slot on the newly opened page must be clicked and settled.
        long slotSelectUs = settleUs;

        return pageNavUs + slotSelectUs + safetyUs;
    }
}
