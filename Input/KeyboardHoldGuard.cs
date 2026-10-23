using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Input;

/// <summary>
/// Uses actual dispatch timestamps so a late scheduler wake-up cannot turn a
/// planned minimum-duration key press into a shorter physical press.
/// </summary>
public sealed class KeyboardHoldGuard
{
    private readonly long _minHoldTicks;
    private readonly long _releaseGapTicks;
    private readonly Dictionary<(ushort ScanCode, bool Extended), long> _downAt = new();
    private readonly Dictionary<(ushort ScanCode, bool Extended), long> _upAt = new();

    public KeyboardHoldGuard(long ticksPerSecond)
    {
        _minHoldTicks = Math.Max(1, (5_000 * ticksPerSecond + 999_999) / 1_000_000);
        _releaseGapTicks = Math.Max(1, (2_000 * ticksPerSecond + 999_999) / 1_000_000);
    }

    public long EarliestDispatchTicks(IReadOnlyList<KeyAction> actions, long scheduledTicks)
    {
        long earliest = scheduledTicks;
        foreach (KeyAction action in actions)
        {
            var key = (action.ScanCode, action.Extended);
            if (action.IsDown && _upAt.TryGetValue(key, out long lastUp))
                earliest = Math.Max(earliest, lastUp + _releaseGapTicks);
            else if (!action.IsDown && _downAt.TryGetValue(key, out long lastDown))
                earliest = Math.Max(earliest, lastDown + _minHoldTicks);
        }
        return earliest;
    }

    public void RecordSent(IReadOnlyList<KeyAction> actions, long sentTicks)
    {
        foreach (KeyAction action in actions)
        {
            var key = (action.ScanCode, action.Extended);
            if (action.IsDown)
            {
                _downAt[key] = sentTicks;
                _upAt.Remove(key);
            }
            else
            {
                _downAt.Remove(key);
                _upAt[key] = sentTicks;
            }
        }
    }

    public void Reset()
    {
        _downAt.Clear();
        _upAt.Clear();
    }
}
