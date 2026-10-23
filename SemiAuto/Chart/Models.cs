namespace Nte.GuitarChart.Core;

public enum PlayMode { Practice, RhythmNormal, RhythmHell, AutoShowcase, Simulated }
public enum PositionExecutionMode { FullManual, SemiAuto }
public enum FeasibilityStatus { Playable, AtRisk, Unplayable, Transformed, Excluded }
public enum IssueSeverity { Info, Warning, Error }
public enum PositionEventStatus { Planned, AtRisk, Missed, Completed, Resynchronized }

/// <summary>Adapter contract for existing MidiLoader output. Times are absolute microseconds.</summary>
public sealed record RawMidiNote(
    long StartUs, long DurationUs, byte NoteNumber, byte Velocity,
    byte Channel, int Program, int TrackIndex, string? TrackName = null);

public readonly record struct GuitarPosition(int Page, int Slot, int Fret)
{
    public static GuitarPosition FromFret(int fret)
    {
        if (fret is < 0 or > 24) throw new ArgumentOutOfRangeException(nameof(fret));
        if (fret == 0) return new GuitarPosition(5, 1, 0);
        return new GuitarPosition(((fret - 1) / 6) + 1, ((fret - 1) % 6) + 1, fret);
    }
    public override string ToString() => $"P{Page} · S{Slot} · {Fret}品";
}

public sealed record ChartNote(
    Guid Id, long StartUs, long DurationUs, byte NoteNumber, byte Velocity,
    byte Channel, int Program, int TrackIndex, string? TrackName,
    int? PhysicalStringNo, int? GameKey, GuitarPosition? Position,
    FeasibilityStatus Status, string? IssueCode = null, string? IssueMessage = null);

public sealed record PositionEvent(
    Guid Id, long TargetTimeUs, long PromptTimeUs, long DeadlineTimeUs,
    GuitarPosition From, GuitarPosition To, double DifficultyWeight,
    PositionEventStatus Status, string? IssueCode = null);

public sealed record FeasibilityIssue(
    string Code, IssueSeverity Severity, string Message,
    IReadOnlyList<Guid> RelatedNoteIds, long? TimeUs = null);

public sealed class GuitarChart
{
    public required string ChartId { get; init; }
    public required string SourceName { get; init; }
    public required IReadOnlyList<ChartNote> Notes { get; init; }
    public required IReadOnlyList<PositionEvent> PositionEvents { get; init; }
    public required IReadOnlyList<FeasibilityIssue> Issues { get; init; }
    public required long DurationUs { get; init; }
    public required int SourceNoteCount { get; init; }
    public required int PlayableNoteCount { get; init; }
    public required int AtRiskNoteCount { get; init; }
    public required int UnplayableNoteCount { get; init; }
    public double PlayabilityRate => SourceNoteCount == 0 ? 1.0 : (double)PlayableNoteCount / SourceNoteCount;
}

public sealed record ChartCompileOptions
{
    public long SimultaneousToleranceUs { get; init; } = 20_000;
    public long PageKeyHoldUs { get; init; } = 25_000;
    public long PageInterTapGapUs { get; init; } = 12_000;
    public long WheelMouseSettleUs { get; init; } = 55_000;
    public long SafetyMarginUs { get; init; } = 8_000;
    public double PageChangeCost { get; init; } = 8.0;
    public double SlotChangeCost { get; init; } = 2.0;
    public double FretDistanceCost { get; init; } = 0.25;
    public double HighFretPenaltyPerFret { get; init; } = 0.08;
    public double InfeasibleTransitionPenalty { get; init; } = 10_000.0;
    public bool UseViterbi { get; init; } = true;
    /// <summary>Legacy cache option retained for migration. Same-onset notes are always grouped to prevent conflicting positions.</summary>
    public bool SequentializeSimultaneousNotes { get; init; }
    public string SourceName { get; init; } = "Imported MIDI";
}

