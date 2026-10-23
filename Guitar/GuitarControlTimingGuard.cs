using System;
using System.Collections.Generic;

namespace MidiAutoPlayer.Guitar;

public sealed class GuitarControlTimingGuard
{
    private readonly long _stringHoldUs;
    private readonly long _navHoldUs;
    private readonly long _navGapUs;
    private readonly long _wheelSettleUs;
    private readonly long _stringReleaseGapUs;
    private readonly Dictionary<byte, long> _stringDownUs = new();
    private readonly Dictionary<byte, long> _stringUpUs = new();
    private long _lastStringUpUs = -1;
    private long _lastStrumUs = -1;
    private long _lastNavDownUs = -1;
    private long _lastNavUpUs = -1;
    private long _lastWheelUs = -1;

    public GuitarControlTimingGuard(long stringHoldUs = 20_000, long navHoldUs = 25_000,
        long navGapUs = 15_000, long wheelSettleUs = 55_000, long stringReleaseGapUs = 22_000,
        long minimumStringReleaseGapUs = 22_000)
    {
        _stringHoldUs = Math.Max(20_000, stringHoldUs);
        _navHoldUs = Math.Max(25_000, navHoldUs);
        _navGapUs = Math.Max(15_000, navGapUs);
        _wheelSettleUs = Math.Max(15_000, wheelSettleUs);
        _stringReleaseGapUs = Math.Max(minimumStringReleaseGapUs, stringReleaseGapUs);
    }

    public long EarliestExecutionUs(GuitarAction action, long nowUs)
    {
        long earliest = nowUs;
        switch (action.Type)
        {
            case GuitarActionType.StringUp:
                if (_stringDownUs.TryGetValue(action.Value, out long downUs))
                    earliest = Math.Max(earliest, downUs + _stringHoldUs);
                break;
            case GuitarActionType.StringDown:
                if (_stringUpUs.TryGetValue(action.Value, out long upUs))
                    earliest = Math.Max(earliest, upUs + _stringReleaseGapUs);
                if (_lastWheelUs >= 0) earliest = Math.Max(earliest, _lastWheelUs + _wheelSettleUs);
                break;
            case GuitarActionType.ModeSwitchDown:
            case GuitarActionType.PageNav:
                if (_lastStringUpUs >= 0) earliest = Math.Max(earliest, _lastStringUpUs + 6_000);
                if (_lastStrumUs >= 0) earliest = Math.Max(earliest, _lastStrumUs + 26_000);
                if (_lastNavUpUs >= 0) earliest = Math.Max(earliest, _lastNavUpUs + _navGapUs);
                if (_lastWheelUs >= 0) earliest = Math.Max(earliest, _lastWheelUs + _wheelSettleUs);
                break;
            case GuitarActionType.ModeSwitchUp:
            case GuitarActionType.PageNavUp:
                if (_lastNavDownUs >= 0) earliest = Math.Max(earliest, _lastNavDownUs + _navHoldUs);
                break;
            case GuitarActionType.WheelSelect:
                if (_lastStringUpUs >= 0) earliest = Math.Max(earliest, _lastStringUpUs + 6_000);
                if (_lastNavUpUs >= 0) earliest = Math.Max(earliest, _lastNavUpUs + _navGapUs);
                if (_lastWheelUs >= 0) earliest = Math.Max(earliest, _lastWheelUs + _wheelSettleUs);
                break;
            case GuitarActionType.StrumClick:
                if (_lastWheelUs >= 0) earliest = Math.Max(earliest, _lastWheelUs + _wheelSettleUs);
                break;
        }
        return earliest;
    }

    public void MarkExecuted(GuitarAction action, long actualUs)
    {
        switch (action.Type)
        {
            case GuitarActionType.StringDown:
                _stringDownUs[action.Value] = actualUs;
                break;
            case GuitarActionType.StringUp:
                _stringUpUs[action.Value] = actualUs;
                _lastStringUpUs = actualUs;
                break;
            case GuitarActionType.ModeSwitchDown:
            case GuitarActionType.PageNav:
                _lastNavDownUs = actualUs;
                break;
            case GuitarActionType.ModeSwitchUp:
            case GuitarActionType.PageNavUp:
                _lastNavUpUs = actualUs;
                break;
            case GuitarActionType.WheelSelect:
                _lastWheelUs = actualUs;
                break;
            case GuitarActionType.StrumClick:
                _lastStrumUs = actualUs;
                break;
        }
    }
}
