namespace Nte.GuitarChart.Core;

/// <summary>
/// First-pass reference compiler.
/// Input must already have:
/// 1) MIDI ticks converted to absolute microseconds by the existing Tempo Map loader;
/// 2) the user's chosen melody/track filter applied.
/// This compiler does not delete selected notes. Impossible groups remain as Unplayable chart events.
/// </summary>
public sealed class GuitarChartCompiler
{
    private static readonly int[] OpenMidiByPhysicalString = { 0, 64, 59, 55, 50, 45, 40 };

    private sealed record OnsetGroup(IReadOnlyList<ChartNote> Notes, long StartUs);
    private sealed record PositionOption(
        GuitarPosition Position,
        IReadOnlyDictionary<Guid, int> PhysicalStringByNoteId,
        double LocalCost);
    private sealed record PlannedGroup(OnsetGroup Group, IReadOnlyList<PositionOption> Options);

    private readonly IPositionTimingModel _timingModel;

    public GuitarChartCompiler(IPositionTimingModel? timingModel = null)
        => _timingModel = timingModel ?? new WheelPositionTimingModel();

    public GuitarChart Compile(
        IReadOnlyList<RawMidiNote> selectedTargetNotes,
        ChartCompileOptions? options = null)
    {
        options ??= new ChartCompileOptions();

        // Do not silently filter selected target notes.
        var notes = selectedTargetNotes
            .OrderBy(n => n.StartUs)
            .ThenBy(n => n.NoteNumber)
            .Select(n => new ChartNote(
                Guid.NewGuid(),
                Math.Max(0, n.StartUs),
                Math.Max(0, n.DurationUs),
                n.NoteNumber,
                n.Velocity,
                n.Channel,
                n.Program,
                n.TrackIndex,
                n.TrackName,
                null, null, null,
                FeasibilityStatus.Playable))
            .ToList();

        var issues = new List<FeasibilityIssue>();
        foreach (var note in notes.Where(n => n.DurationUs == 0))
        {
            issues.Add(new FeasibilityIssue(
                "ZERO_DURATION",
                IssueSeverity.Warning,
                "音符时值为 0；保留事件，但需检查 MIDI NoteOff 配对。",
                new[] { note.Id },
                note.StartUs));
        }

        var groups = BuildOnsetGroups(notes, options.SimultaneousToleranceUs);
        var planned = groups.Select(g => new PlannedGroup(g, BuildPositionOptions(g, options))).ToList();
        var chosen = SelectGlobalPath(planned, options);
        var resultById = notes.ToDictionary(n => n.Id);
        var selectedGroups = new List<(OnsetGroup Group, PositionOption Option)>();

        for (var i = 0; i < planned.Count; i++)
        {
            var pg = planned[i];
            if (pg.Options.Count == 0 || chosen[i] is null)
            {
                foreach (var note in pg.Group.Notes)
                {
                    resultById[note.Id] = note with
                    {
                        Status = FeasibilityStatus.Unplayable,
                        IssueCode = "NO_COMMON_FRET",
                        IssueMessage = "同一并发组找不到能由当前吉他位置共同演奏的指法；目标音符保留在谱面中。"
                    };
                }

                issues.Add(new FeasibilityIssue(
                    "NO_COMMON_FRET",
                    IssueSeverity.Error,
                    "并发音符无法共用同一品位位置。可尝试分解和弦改编，但必须由用户确认。",
                    pg.Group.Notes.Select(n => n.Id).ToArray(),
                    pg.Group.StartUs));
                continue;
            }

            var option = chosen[i]!;
            selectedGroups.Add((pg.Group, option));

            foreach (var note in pg.Group.Notes)
            {
                var physicalString = option.PhysicalStringByNoteId[note.Id];
                resultById[note.Id] = note with
                {
                    PhysicalStringNo = physicalString,
                    // The game's lane order is opposite physical guitar-string numbering:
                    // physical string 1 maps to game key 6, physical string 6 to game key 1.
                    GameKey = 7 - physicalString,
                    Position = option.Position,
                    Status = FeasibilityStatus.Playable,
                    IssueCode = null,
                    IssueMessage = null
                };
            }
        }

        var positionEvents = BuildPositionEvents(selectedGroups, options, issues);
        foreach (var posEvent in positionEvents.Where(e => e.Status == PositionEventStatus.AtRisk))
        {
            foreach (var note in resultById.Values.Where(n =>
                         n.Position == posEvent.To &&
                         Math.Abs(n.StartUs - posEvent.TargetTimeUs) <= options.SimultaneousToleranceUs))
            {
                if (note.Status == FeasibilityStatus.Playable)
                {
                    resultById[note.Id] = note with
                    {
                        Status = FeasibilityStatus.AtRisk,
                        IssueCode = "POSITION_PREPARE_TIME",
                        IssueMessage = "当前估算的翻页/选槽准备时间不足，需通过真实游戏校准或重新规划验证。"
                    };
                }
            }
        }

        var finalNotes = resultById.Values.OrderBy(n => n.StartUs).ThenBy(n => n.NoteNumber).ToArray();
        return new GuitarChart
        {
            ChartId = Guid.NewGuid().ToString("N"),
            SourceName = options.SourceName,
            Notes = finalNotes,
            PositionEvents = positionEvents,
            Issues = issues,
            DurationUs = finalNotes.Length == 0 ? 0 : finalNotes.Max(n => n.StartUs + n.DurationUs),
            SourceNoteCount = finalNotes.Length,
            PlayableNoteCount = finalNotes.Count(n => n.Status == FeasibilityStatus.Playable),
            AtRiskNoteCount = finalNotes.Count(n => n.Status == FeasibilityStatus.AtRisk),
            UnplayableNoteCount = finalNotes.Count(n => n.Status == FeasibilityStatus.Unplayable)
        };
    }

    private static List<OnsetGroup> BuildOnsetGroups(List<ChartNote> notes, long toleranceUs)
    {
        var groups = new List<OnsetGroup>();
        var cursor = 0;
        while (cursor < notes.Count)
        {
            var firstStart = notes[cursor].StartUs;
            var batch = new List<ChartNote> { notes[cursor] };
            cursor++;
            while (cursor < notes.Count && notes[cursor].StartUs - firstStart <= toleranceUs)
            {
                batch.Add(notes[cursor]);
                cursor++;
            }
            groups.Add(new OnsetGroup(batch, firstStart));
        }
        return groups;
    }

    private static IReadOnlyList<PositionOption> BuildPositionOptions(OnsetGroup group, ChartCompileOptions options)
    {
        var result = new List<PositionOption>();
        for (var fret = 0; fret <= 24; fret++)
        {
            if (!TryAssignDistinctStrings(group.Notes, fret, out var stringMap))
                continue;

            var position = GuitarPosition.FromFret(fret);
            var localCost = Math.Max(0, fret - 12) * options.HighFretPenaltyPerFret;
            foreach (var note in group.Notes)
            {
                var physicalString = stringMap[note.Id];
                if (note.NoteNumber >= 64 && physicalString >= 4) localCost += 0.4;
                if (note.NoteNumber <= 50 && physicalString <= 2) localCost += 0.4;
            }
            result.Add(new PositionOption(position, stringMap, localCost));
        }
        return result;
    }

    private static bool TryAssignDistinctStrings(
        IReadOnlyList<ChartNote> notes, int fret,
        out IReadOnlyDictionary<Guid, int> assignment)
    {
        var ordered = notes.Select(n => new
        {
            Note = n,
            Strings = Enumerable.Range(1, 6)
                .Where(s => OpenMidiByPhysicalString[s] + fret == n.NoteNumber)
                .OrderBy(s => s).ToArray()
        }).OrderBy(x => x.Strings.Length).ToArray();

        var result = new Dictionary<Guid, int>();
        var used = new HashSet<int>();

        bool Search(int index)
        {
            if (index >= ordered.Length) return true;
            var item = ordered[index];
            foreach (var stringNo in item.Strings)
            {
                if (!used.Add(stringNo)) continue;
                result[item.Note.Id] = stringNo;
                if (Search(index + 1)) return true;
                result.Remove(item.Note.Id);
                used.Remove(stringNo);
            }
            return false;
        }

        var success = Search(0);
        assignment = result;
        return success;
    }

    private IReadOnlyList<PositionOption?> SelectGlobalPath(
        IReadOnlyList<PlannedGroup> groups, ChartCompileOptions options)
    {
        var selected = new PositionOption?[groups.Count];
        var activeIndexes = Enumerable.Range(0, groups.Count).Where(i => groups[i].Options.Count > 0).ToArray();
        if (activeIndexes.Length == 0) return selected;

        if (!options.UseViterbi)
        {
            foreach (var i in activeIndexes)
                selected[i] = groups[i].Options.OrderBy(o => o.LocalCost).First();
            return selected;
        }

        var dp = new double[activeIndexes.Length][];
        var back = new int[activeIndexes.Length][];

        for (var layer = 0; layer < activeIndexes.Length; layer++)
        {
            var groupIndex = activeIndexes[layer];
            var candidates = groups[groupIndex].Options;
            dp[layer] = Enumerable.Repeat(double.PositiveInfinity, candidates.Count).ToArray();
            back[layer] = Enumerable.Repeat(-1, candidates.Count).ToArray();

            if (layer == 0)
            {
                for (var j = 0; j < candidates.Count; j++) dp[layer][j] = candidates[j].LocalCost;
                continue;
            }

            var previousGroupIndex = activeIndexes[layer - 1];
            var previousGroup = groups[previousGroupIndex].Group;
            var currentGroup = groups[groupIndex].Group;
            var deltaUs = Math.Max(0, currentGroup.StartUs - previousGroup.StartUs);

            for (var j = 0; j < candidates.Count; j++)
            {
                for (var k = 0; k < groups[previousGroupIndex].Options.Count; k++)
                {
                    var previous = groups[previousGroupIndex].Options[k];
                    var current = candidates[j];
                    var prepareUs = _timingModel.EstimatePrepareUs(previous.Position, current.Position, options);
                    var timingPenalty = deltaUs < prepareUs
                        ? options.InfeasibleTransitionPenalty + (prepareUs - deltaUs) / 1000.0
                        : 0.0;

                    var cost = dp[layer - 1][k]
                               + TransitionCost(previous.Position, current.Position, options)
                               + timingPenalty + current.LocalCost;
                    if (cost < dp[layer][j])
                    {
                        dp[layer][j] = cost;
                        back[layer][j] = k;
                    }
                }
            }
        }

        var lastLayer = activeIndexes.Length - 1;
        var best = Array.IndexOf(dp[lastLayer], dp[lastLayer].Min());
        for (var layer = lastLayer; layer >= 0; layer--)
        {
            selected[activeIndexes[layer]] = groups[activeIndexes[layer]].Options[best];
            best = back[layer][best];
            if (layer > 0 && best < 0) best = 0;
        }
        return selected;
    }

    private static double TransitionCost(GuitarPosition from, GuitarPosition to, ChartCompileOptions options)
    {
        var pageDistance = Math.Abs(to.Page - from.Page);
        var slotDistance = CircularSlotDistance(from.Slot, to.Slot);
        var fretDistance = Math.Abs(to.Fret - from.Fret);
        return pageDistance * options.PageChangeCost
               + slotDistance * options.SlotChangeCost
               + fretDistance * options.FretDistanceCost;
    }

    private static int CircularSlotDistance(int a, int b)
    {
        var direct = Math.Abs(a - b);
        return Math.Min(direct, 6 - direct);
    }

    private IReadOnlyList<PositionEvent> BuildPositionEvents(
        IReadOnlyList<(OnsetGroup Group, PositionOption Option)> groups,
        ChartCompileOptions options,
        List<FeasibilityIssue> issues)
    {
        var events = new List<PositionEvent>();
        if (groups.Count < 2) return events;

        var ordered = groups.OrderBy(g => g.Group.StartUs).ToArray();
        for (var i = 1; i < ordered.Length; i++)
        {
            var previous = ordered[i - 1];
            var current = ordered[i];
            var from = previous.Option.Position;
            var to = current.Option.Position;
            if (from.Page == to.Page && from.Slot == to.Slot) continue;

            var prepareUs = _timingModel.EstimatePrepareUs(from, to, options);
            var availableUs = Math.Max(0, current.Group.StartUs - previous.Group.StartUs);
            var atRisk = availableUs < prepareUs;
            var promptUs = Math.Max(0, current.Group.StartUs - prepareUs);

            events.Add(new PositionEvent(
                Guid.NewGuid(), current.Group.StartUs, promptUs, current.Group.StartUs,
                from, to, PositionSwitchDifficulty.CalculateWeight(from, to),
                atRisk ? PositionEventStatus.AtRisk : PositionEventStatus.Planned,
                atRisk ? "POSITION_PREPARE_TIME" : null));

            if (atRisk)
            {
                issues.Add(new FeasibilityIssue(
                    "POSITION_PREPARE_TIME", IssueSeverity.Warning,
                    $"位置切换 {from} -> {to} 的估算准备时间为 {prepareUs / 1000.0:F0}ms，粗略可用时间为 {availableUs / 1000.0:F0}ms。此为风险提示，不删除音符。",
                    current.Group.Notes.Select(n => n.Id).ToArray(), current.Group.StartUs));
            }
        }
        return events;
    }
}
