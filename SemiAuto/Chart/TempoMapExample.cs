namespace Nte.GuitarChart.Core;

/// <summary>
/// Reference algorithm only. If the existing MidiLoader already produces StartUs,
/// keep using it instead of converting ticks a second time.
/// </summary>
public sealed record TempoChange(long Tick, int MicrosecondsPerQuarterNote);

public static class TempoMapExample
{
    public static long TickToMicroseconds(
        long targetTick,
        int ticksPerQuarterNote,
        IReadOnlyList<TempoChange> tempoChanges)
    {
        if (ticksPerQuarterNote <= 0)
            throw new ArgumentOutOfRangeException(nameof(ticksPerQuarterNote));
        if (targetTick < 0)
            throw new ArgumentOutOfRangeException(nameof(targetTick));

        // Standard MIDI default tempo: 500,000 microseconds per quarter note.
        long cursorTick = 0;
        int tempo = 500_000;
        decimal elapsedUs = 0m;

        foreach (var change in tempoChanges.OrderBy(t => t.Tick))
        {
            if (change.Tick > targetTick)
                break;

            var effectiveTick = Math.Max(cursorTick, change.Tick);
            elapsedUs += (decimal)(effectiveTick - cursorTick) * tempo / ticksPerQuarterNote;
            cursorTick = effectiveTick;
            tempo = change.MicrosecondsPerQuarterNote;
        }

        elapsedUs += (decimal)(targetTick - cursorTick) * tempo / ticksPerQuarterNote;
        return (long)decimal.Round(elapsedUs, 0, MidpointRounding.AwayFromZero);
    }
}
