using MidiAutoPlayer.Guitar;
using Nte.GuitarChart.Core;

namespace Nte.GuitarChart.Core;

public readonly record struct AutoShowcaseInputEvent(long TimeUs, ushort ScanCode, bool IsDown, Guid NoteId, int GameKey, FeasibilityStatus Status);

/// <summary>Creates deterministic key press/release events from playable chart notes.</summary>
public static class AutoShowcaseInputPlan
{
    public static IReadOnlyList<AutoShowcaseInputEvent> Create(
        GuitarChart chart, IReadOnlyList<ushort>? configuredStringScanCodes = null,
        IReadOnlyList<PhysicalKeyBinding>? configuredGameLaneBindings = null)
    {
        ArgumentNullException.ThrowIfNull(chart);
        var events = new List<AutoShowcaseInputEvent>();
        foreach (var note in chart.Notes)
        {
            if (note.GameKey is not (>= 1 and <= 6) || note.PhysicalStringNo is not (>= 1 and <= 6)
                || note.Position is null || note.Status == FeasibilityStatus.Unplayable)
                continue;

            int stringIndex = note.PhysicalStringNo.Value - 1;
            ushort scanCode = configuredStringScanCodes is { Count: >= 6 }
                              && configuredStringScanCodes[stringIndex] != 0
                ? configuredStringScanCodes[stringIndex]
                : GameScanCodes.ForGuitarString(note.PhysicalStringNo.Value);
            long durationUs = Math.Clamp(note.DurationUs, 25_000L, 250_000L);
            events.Add(new(note.StartUs, scanCode, true, note.Id, note.GameKey.Value, note.Status));
            events.Add(new(note.StartUs + durationUs, scanCode, false, note.Id, note.GameKey.Value, note.Status));
        }

        // Release held keys before pressing another note at the same timestamp.
        return events.OrderBy(e => e.TimeUs).ThenBy(e => e.IsDown ? 1 : 0).ThenBy(e => e.GameKey).ToArray();
    }
}
