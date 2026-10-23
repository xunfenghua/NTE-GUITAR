using System.Collections.Concurrent;
using Nte.GuitarChart.Core;

namespace Nte.GuitarChart.WinForms;
using GuitarChartModel = Nte.GuitarChart.Core.GuitarChart;

/// <summary>Time-aligned practice targets. Judging a note never edits or reorders the source chart.</summary>
internal sealed class PracticeSessionController
{
    public const long SimultaneousToleranceUs = 20_000;
    private readonly IReadOnlyList<ChartNote> _targets;
    private readonly ConcurrentDictionary<Guid, byte> _judged = new();

    public PracticeEngine Engine { get; } = new();
    public int JudgedCount => _judged.Count;
    public int TotalTargets => _targets.Count;
    public bool IsCompleted => _judged.Count >= _targets.Count;
    public ChartNote? Current => GetUpcomingTargets(0).FirstOrDefault();
    public IReadOnlyList<ChartNote> Upcoming => GetUpcomingTargets(Current?.StartUs ?? 0).Skip(1).Take(3).ToArray();

    public PracticeSessionController(GuitarChartModel chart, int startIndex = 0)
    {
        _targets = chart.Notes
            .Where(n => n.GameKey is >= 1 and <= 6 && n.Position.HasValue && n.Status != FeasibilityStatus.Unplayable)
            .OrderBy(n => n.StartUs)
            .ThenBy(n => n.GameKey)
            .ToArray();

        if (startIndex > 0)
        {
            foreach (var note in _targets.Take(Math.Clamp(startIndex, 0, _targets.Count)))
                _judged.TryAdd(note.Id, 0);
        }
    }

    public IReadOnlyList<ChartNote> GetTargetsAt(long timeUs, long toleranceUs = 80_000) =>
        _targets.Where(n => !_judged.ContainsKey(n.Id)
                && Math.Abs(n.StartUs - timeUs) <= Math.Max(0, toleranceUs))
            .OrderBy(n => Math.Abs(n.StartUs - timeUs))
            .ThenBy(n => n.GameKey)
            .ToArray();

    public IReadOnlyList<ChartNote> GetUpcomingTargets(long timeUs)
    {
        var next = _targets.FirstOrDefault(n => !_judged.ContainsKey(n.Id) && n.StartUs >= timeUs);
        if (next == null) return Array.Empty<ChartNote>();
        return _targets.Where(n => !_judged.ContainsKey(n.Id)
                && Math.Abs(n.StartUs - next.StartUs) <= SimultaneousToleranceUs)
            .OrderBy(n => n.GameKey)
            .ToArray();
    }

    public long? GetTailRecoveryTimeUs(long currentTimeUs, long durationUs)
    {
        if (currentTimeUs < durationUs) return null;
        return _targets.FirstOrDefault(note => !_judged.ContainsKey(note.Id))?.StartUs;
    }

    public PracticeFeedback EvaluateInput(int gameKey, long actualTimeUs, long toleranceUs = 80_000)
    {
        var active = GetTargetsAt(actualTimeUs, toleranceUs);
        if (active.Count == 0)
            return new PracticeFeedback("early", "当前时间附近没有待练音符。");

        var match = active.FirstOrDefault(n => n.GameKey == gameKey);
        var expected = match ?? active[0];
        var feedback = Engine.EvaluateKey(expected.GameKey!.Value, gameKey, expected.Id);
        if (feedback.Kind == "correct") _judged.TryAdd(expected.Id, 0);
        return feedback;
    }
}
