using System.Diagnostics;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Guitar;

internal readonly record struct DroppedGamepadNote(
    long TimeUs, byte StringNo, byte NoteNumber, byte Page, byte Slot, string Reason);

internal sealed record GamepadPlanResult(
    List<GuitarAction> Actions,
    List<DroppedGamepadNote> DroppedNotes);

/// <summary>Adapts device-independent guitar actions to the virtual gamepad timeline.</summary>
internal static class GamepadActionPlanner
{
    private sealed record StringPair(GuitarAction Down, GuitarAction Up, int SegmentId)
    {
        public byte StringNo => Down.Value;
        public byte NoteNumber => Down.NoteNumber;
        public long TimeUs => Down.TimeUs;
    }

    private sealed class OpenDown(GuitarAction action, int segmentId, bool emit = true)
    {
        public GuitarAction Action { get; } = action;
        public int SegmentId { get; } = segmentId;
        public bool Emit { get; } = emit;
    }

    private sealed class AttackCluster(StringPair first)
    {
        public List<StringPair> Pairs { get; } = [first];
        public int SegmentId => Pairs[0].SegmentId;
        public byte Page => Pairs[0].Down.RequiredPage;
        public byte Slot => Pairs[0].Down.RequiredSlot;
        public long TimeUs => Pairs[0].TimeUs;
    }

    public static List<GuitarAction> StaggerDpad(
        IReadOnlyList<GuitarAction> source,
        long wheelSettleUs = GamepadInputTiming.SlotSelectLeadUs)
    {
        var result = PlanGamepadTimeline(source, wheelSettleUs);
        if (result.DroppedNotes.Count > 0)
            Trace.TraceWarning($"Gamepad output planner dropped {result.DroppedNotes.Count} notes due to D-pad capacity or control barriers.");
        return result.Actions;
    }

    public static GamepadPlanResult PlanGamepadTimeline(
        IReadOnlyList<GuitarAction> source,
        long wheelSettleUs = GamepadInputTiming.SlotSelectLeadUs)
    {
        var dropped = new List<DroppedGamepadNote>();
        if (source.Count == 0) return new GamepadPlanResult([], dropped);

        var ordered = source.Select((action, index) => (action, index))
            .OrderBy(item => item.action.TimeUs)
            .ThenBy(item => Priority(item.action.Type))
            .ThenBy(item => item.index)
            .Select(item => item.action)
            .ToList();

        var pairs = new List<StringPair>();
        var controls = new List<GuitarAction>();
        var openByString = new Queue<OpenDown>[7];
        for (int i = 0; i < openByString.Length; i++) openByString[i] = new Queue<OpenDown>();
        var barriers = new List<long>();
        int segmentId = 0;

        void CloseOpenDowns(long barrierTimeUs)
        {
            long lastSafeUpUs = SaturatingSubtract(barrierTimeUs, 1_000);
            for (byte stringNo = 1; stringNo <= 6; stringNo++)
            {
                while (openByString[stringNo].Count > 0)
                {
                    var open = openByString[stringNo].Dequeue();
                    if (!open.Emit) continue;
                    long minUpUs = SaturatingAdd(open.Action.TimeUs, GamepadInputTiming.StringHoldUs);
                    if (minUpUs > lastSafeUpUs)
                    {
                        Drop(open.Action, "StringHoldCannotFitBeforeBarrier");
                        continue;
                    }
                    var syntheticUp = At(open.Action, GuitarActionType.StringUp, lastSafeUpUs);
                    pairs.Add(new StringPair(open.Action, syntheticUp, open.SegmentId));
                }
            }
        }

        void Drop(GuitarAction action, string reason) => dropped.Add(new DroppedGamepadNote(
            action.TimeUs, action.Type is GuitarActionType.StringDown or GuitarActionType.StringUp ? action.Value : (byte)0,
            action.NoteNumber, action.RequiredPage, action.RequiredSlot, reason));

        foreach (var sourceAction in ordered)
        {
            var action = sourceAction;
            if (IsBarrier(action.Type))
            {
                CloseOpenDowns(action.TimeUs);
                barriers.Add(action.TimeUs);
                controls.Add(action);
                segmentId++;
                continue;
            }

            if (action.Type == GuitarActionType.StringDown)
            {
                if (action.Value is < 1 or > 6)
                {
                    Drop(action, "InvalidStringNumber");
                    continue;
                }
                if (openByString[action.Value].Count > 0)
                {
                    // Keep the existing note/up association intact. The D-pad
                    // adapter will drop a retrigger that cannot fit safely.
                    Drop(action, "SameStringRetriggerBeforeRelease");
                    openByString[action.Value].Enqueue(new OpenDown(action, segmentId, emit: false));
                    continue;
                }
                openByString[action.Value].Enqueue(new OpenDown(action, segmentId));
                continue;
            }

            if (action.Type == GuitarActionType.StringUp)
            {
                if (action.Value is < 1 or > 6)
                {
                    Drop(action, "InvalidStringNumber");
                    continue;
                }
                if (openByString[action.Value].Count == 0)
                {
                    // Any Up remaining after a barrier/forced closure is stale.
                    continue;
                }

                var open = openByString[action.Value].Dequeue();
                if (!open.Emit) continue;
                long minUpUs = SaturatingAdd(open.Action.TimeUs, GamepadInputTiming.StringHoldUs);
                if (action.TimeUs < minUpUs)
                    action = At(action, GuitarActionType.StringUp, minUpUs);
                pairs.Add(new StringPair(open.Action, action, open.SegmentId));
                continue;
            }

            controls.Add(action);
        }

        // Do not leave final held notes open when a compiled timeline ends.
        for (byte stringNo = 1; stringNo <= 6; stringNo++)
        {
            while (openByString[stringNo].Count > 0)
            {
                var open = openByString[stringNo].Dequeue();
                if (!open.Emit) continue;
                pairs.Add(new StringPair(open.Action,
                    At(open.Action, GuitarActionType.StringUp,
                        SaturatingAdd(open.Action.TimeUs, GamepadInputTiming.StringHoldUs)),
                    open.SegmentId));
            }
        }

        var facePairs = pairs.Where(pair => pair.StringNo >= 5).ToList();
        var dpadPairs = pairs.Where(pair => pair.StringNo <= 4).ToList();
        ushort notePairId = 1;
        ushort controlGeneration = 1;
        var scheduledFace = ScheduleFacePairs(facePairs, barriers, dropped, ref notePairId);
        var scheduledDpad = ScheduleDpadPairs(dpadPairs, barriers, dropped, ref notePairId);
        var expandedControls = ExpandControlPulses(controls, ref controlGeneration);
        var timeline = expandedControls.Concat(scheduledFace).Concat(scheduledDpad)
            .OrderBy(action => action.TimeUs)
            .ThenBy(action => Priority(action.Type))
            .ToList();

        timeline = RestoreSlotsAfterClearingControls(timeline, dropped, wheelSettleUs, ref controlGeneration);
        return new GamepadPlanResult(timeline, dropped);
    }

    private static List<GuitarAction> ScheduleFacePairs(
        IReadOnlyList<StringPair> pairs,
        IReadOnlyList<long> barriers,
        List<DroppedGamepadNote> dropped,
        ref ushort pairId)
    {
        var result = new List<GuitarAction>(pairs.Count * 2);
        foreach (var pair in pairs.OrderBy(pair => pair.TimeUs))
        {
            long barrier = FindNextBarrier(barriers, pair.TimeUs);
            long minUp = SaturatingAdd(pair.TimeUs, GamepadInputTiming.StringHoldUs);
            long latestUp = SaturatingSubtract(barrier, 1_000);
            if (minUp > latestUp)
            {
                dropped.Add(ToDropped(pair, "FaceStringBlockedByBarrier"));
                continue;
            }
            long upTime = Math.Clamp(pair.Up.TimeUs, minUp, latestUp);
            ushort id = NextId(ref pairId);
            result.Add(AtWithScanCode(pair.Down, pair.Down.TimeUs, id));
            result.Add(AtWithScanCode(pair.Up, upTime, id));
        }
        return result;
    }

    private static List<GuitarAction> ScheduleDpadPairs(
        IReadOnlyList<StringPair> pairs,
        IReadOnlyList<long> barriers,
        List<DroppedGamepadNote> dropped,
        ref ushort pairId)
    {
        var clusters = new List<AttackCluster>();
        foreach (var pair in pairs.OrderBy(pair => pair.TimeUs))
        {
            if (clusters.Count == 0)
            {
                clusters.Add(new AttackCluster(pair));
                continue;
            }
            var last = clusters[^1];
            if (pair.SegmentId == last.SegmentId && pair.Down.RequiredPage == last.Page &&
                pair.Down.RequiredSlot == last.Slot && pair.TimeUs - last.TimeUs <= 5_000)
                last.Pairs.Add(pair);
            else
                clusters.Add(new AttackCluster(pair));
        }

        var result = new List<GuitarAction>(pairs.Count * 2);
        long lastUpUs = long.MinValue / 4;
        byte lastString = 0;
        for (int clusterIndex = 0; clusterIndex < clusters.Count; clusterIndex++)
        {
            var cluster = clusters[clusterIndex];
            long nextBarrier = FindNextBarrier(barriers, cluster.TimeUs);
            long hardBarrierLimit = SaturatingSubtract(nextBarrier, 1_000);
            long nextCluster = clusterIndex + 1 < clusters.Count
                ? clusters[clusterIndex + 1].TimeUs : long.MaxValue;
            var voices = cluster.Pairs.OrderByDescending(pair => pair.NoteNumber)
                .ThenBy(pair => pair.StringNo).ToArray();
            var kept = new List<(StringPair Pair, long DownUs, long MinUpUs)>();
            bool leadBlocked = false;

            foreach (int voiceIndex in Enumerable.Range(0, voices.Length))
            {
                var pair = voices[voiceIndex];
                bool isLead = voiceIndex == 0;
                long requiredGap = lastString == pair.StringNo
                    ? GamepadInputTiming.SameStringReleaseUs
                    : GamepadInputTiming.DpadNeutralUs;
                long earliestDown = lastUpUs < long.MinValue / 8
                    ? cluster.TimeUs
                    : Math.Max(cluster.TimeUs, SaturatingAdd(lastUpUs, requiredGap));
                long minUp = SaturatingAdd(earliestDown, GamepadInputTiming.StringHoldUs);
                long latestDown = isLead
                    ? SaturatingAdd(cluster.TimeUs, GamepadInputTiming.MaxLeadLatenessUs)
                    : Math.Min(SaturatingAdd(cluster.TimeUs, GamepadInputTiming.MaxArpeggioSpreadUs),
                        SaturatingSubtract(nextCluster, GamepadInputTiming.StringHoldUs + requiredGap));

                if (earliestDown > latestDown || minUp > hardBarrierLimit)
                {
                    if (isLead)
                    {
                        leadBlocked = true;
                        dropped.Add(ToDropped(pair, "GamepadLeadBlockedByCapacity"));
                        for (int remaining = voiceIndex + 1; remaining < voices.Length; remaining++)
                            dropped.Add(ToDropped(voices[remaining], "AttackClusterDroppedWithLead"));
                        break;
                    }
                    dropped.Add(ToDropped(pair, "GamepadInnerVoiceDropped"));
                    continue;
                }

                kept.Add((pair, earliestDown, minUp));
                lastUpUs = minUp;
                lastString = pair.StringNo;
            }

            if (leadBlocked) continue;
            for (int i = 0; i < kept.Count; i++)
            {
                var (pair, downUs, minUp) = kept[i];
                long upUs;
                if (i + 1 < kept.Count)
                {
                    upUs = minUp;
                }
                else
                {
                    long releaseGap = lastString == pair.StringNo
                        ? GamepadInputTiming.SameStringReleaseUs
                        : GamepadInputTiming.DpadNeutralUs;
                    long latestUp = Math.Min(hardBarrierLimit, SaturatingSubtract(nextCluster, releaseGap));
                    if (latestUp < minUp) latestUp = minUp;
                    upUs = Math.Clamp(pair.Up.TimeUs, minUp, latestUp);
                }

                ushort id = NextId(ref pairId);
                result.Add(AtWithScanCode(pair.Down, downUs, id));
                result.Add(AtWithScanCode(pair.Up, upUs, id));
                lastUpUs = upUs;
                lastString = pair.StringNo;
            }
        }
        return result;
    }

    private static List<GuitarAction> ExpandControlPulses(
        IReadOnlyList<GuitarAction> controls,
        ref ushort generation)
    {
        var result = new List<GuitarAction>(controls.Count + 32);
        foreach (var action in controls)
        {
            if (action.Type == GuitarActionType.WheelSelect)
            {
                ushort id = NextId(ref generation);
                var select = AtWithScanCode(action, action.TimeUs, id);
                result.Add(select);
                long nextControl = controls.Where(candidate => IsBarrier(candidate.Type) &&
                        candidate.TimeUs > action.TimeUs)
                    .Select(candidate => candidate.TimeUs).DefaultIfEmpty(long.MaxValue).Min();
                long releaseUs = SaturatingSubtract(nextControl, 1_000);
                if (nextControl != long.MaxValue && releaseUs > action.TimeUs + 5_000)
                    result.Add(new GuitarAction(
                    releaseUs,
                    GuitarActionType.WheelSelectUp,
                    action.Value,
                    id,
                    action.MouseButton,
                    SaturatingAdd(releaseUs, 60_000),
                    action.RequiredPage,
                    action.RequiredSlot,
                    action.Resource,
                    action.FallbackPolicy,
                    action.NoteNumber));
            }
            else if (action.Type == GuitarActionType.StrumClick)
            {
                result.Add(action);
                result.Add(At(action, GuitarActionType.StrumUp,
                    SaturatingAdd(action.TimeUs, GamepadInputTiming.StrumHoldMs * 1_000L)));
            }
            else
            {
                result.Add(action);
            }
        }

        // A stale neutral event must not cross a later slot/page/mode change.
        var barrierTimes = result.Where(action => IsBarrier(action.Type)).Select(action => action.TimeUs).ToArray();
        for (int i = result.Count - 1; i >= 0; i--)
        {
            var action = result[i];
            if (action.Type != GuitarActionType.WheelSelectUp) continue;
            var selectDown = result.FirstOrDefault(candidate => candidate.Type == GuitarActionType.WheelSelect &&
                candidate.ScanCode == action.ScanCode && candidate.TimeUs <= action.TimeUs);
            if (selectDown.Type != GuitarActionType.WheelSelect)
            {
                result.RemoveAt(i);
                continue;
            }
            long nextBarrier = FindNextBarrier(barrierTimes, selectDown.TimeUs);
            long latest = SaturatingSubtract(nextBarrier, 1_000);
            if (latest <= selectDown.TimeUs + 5_000)
            {
                result.RemoveAt(i);
                continue;
            }
            if (action.TimeUs > latest)
                result[i] = At(action, GuitarActionType.WheelSelectUp, latest);
        }
        return result;
    }

    private static List<GuitarAction> RestoreSlotsAfterClearingControls(
        List<GuitarAction> actions,
        List<DroppedGamepadNote> dropped,
        long wheelSettleUs,
        ref ushort generation)
    {
        // Compilers normally emit a WheelSelect after page/mode changes. Repair
        // only missing selections and keep them after the release of the control.
        var output = new List<GuitarAction>(actions.Count + 8);
        byte plannedSlot = 1;
        long lastBarrierReleaseUs = long.MinValue / 4;
        foreach (var action in actions)
        {
            if (action.Type is GuitarActionType.PageNav or GuitarActionType.ModeSwitchDown)
            {
                plannedSlot = 0;
            }
            else if (action.Type is GuitarActionType.PageNavUp or GuitarActionType.ModeSwitchUp)
            {
                lastBarrierReleaseUs = Math.Max(lastBarrierReleaseUs, action.TimeUs);
            }
            else if (action.Type == GuitarActionType.WheelSelect)
            {
                plannedSlot = action.Value;
            }
            else if ((action.Type is GuitarActionType.StringDown or GuitarActionType.StrumClick) &&
                     action.RequiredSlot > 0 && plannedSlot != action.RequiredSlot)
            {
                long selectUs = Math.Max(
                    SaturatingAdd(lastBarrierReleaseUs, 1_000),
                    SaturatingSubtract(action.TimeUs, Math.Max(GamepadInputTiming.SlotSelectLeadUs, wheelSettleUs)));
                long maxSelectUs = SaturatingAdd(action.TimeUs, GamepadInputTiming.MaxLeadLatenessUs);
                if (selectUs > maxSelectUs)
                {
                    dropped.Add(new DroppedGamepadNote(action.TimeUs, action.Type == GuitarActionType.StringDown ? action.Value : (byte)0,
                        action.NoteNumber, action.RequiredPage, action.RequiredSlot, "PostBarrierSlotUnreachable"));
                    continue;
                }
                ushort id = NextId(ref generation);
                long nextBarrier = actions.Where(candidate => IsBarrier(candidate.Type) &&
                        candidate.TimeUs > action.TimeUs)
                    .Select(candidate => candidate.TimeUs).DefaultIfEmpty(long.MaxValue).Min();
                output.Add(new GuitarAction(selectUs, GuitarActionType.WheelSelect, action.RequiredSlot,
                    id, action.MouseButton, SaturatingAdd(selectUs, 60_000), action.RequiredPage,
                    action.RequiredSlot, GameActionResource.Wheel, action.FallbackPolicy));
                long releaseUs = SaturatingSubtract(nextBarrier, 1_000);
                if (nextBarrier != long.MaxValue && releaseUs > selectUs + 5_000)
                    output.Add(new GuitarAction(releaseUs,
                        GuitarActionType.WheelSelectUp, action.RequiredSlot, id, action.MouseButton,
                        SaturatingAdd(releaseUs, 60_000), action.RequiredPage,
                        action.RequiredSlot, GameActionResource.Wheel, action.FallbackPolicy));
                plannedSlot = action.RequiredSlot;
            }
            output.Add(action);
        }
        return output.OrderBy(action => action.TimeUs).ThenBy(action => Priority(action.Type)).ToList();
    }

    private static DroppedGamepadNote ToDropped(StringPair pair, string reason) => new(
        pair.TimeUs, pair.StringNo, pair.NoteNumber,
        pair.Down.RequiredPage, pair.Down.RequiredSlot, reason);

    private static bool IsBarrier(GuitarActionType type) => type is
        GuitarActionType.PageNav or GuitarActionType.PageNavUp or
        GuitarActionType.ModeSwitchDown or GuitarActionType.ModeSwitchUp or
        GuitarActionType.WheelSelect;

    private static long FindNextBarrier(IReadOnlyList<long> barriers, long afterUs)
    {
        for (int i = 0; i < barriers.Count; i++)
            if (barriers[i] > afterUs) return barriers[i];
        return long.MaxValue;
    }

    private static int Priority(GuitarActionType type) => type switch
    {
        GuitarActionType.StringUp or GuitarActionType.StrumUp or GuitarActionType.WheelSelectUp or
            GuitarActionType.PageNavUp or GuitarActionType.ModeSwitchUp => 0,
        GuitarActionType.PageNav or GuitarActionType.ModeSwitchDown => 1,
        GuitarActionType.WheelSelect => 2,
        GuitarActionType.StrumClick => 3,
        GuitarActionType.StringDown => 4,
        _ => 5
    };

    private static GuitarAction At(GuitarAction action, GuitarActionType type, long timeUs) => new(
        timeUs, type, action.Value, action.ScanCode, action.MouseButton,
        SaturatingAdd(timeUs, Math.Max(1_000, action.DeadlineUs - action.TimeUs)),
        action.RequiredPage, action.RequiredSlot, action.Resource, action.FallbackPolicy, action.NoteNumber);

    private static GuitarAction AtWithScanCode(GuitarAction action, long timeUs, ushort scanCode) => new(
        timeUs, action.Type, action.Value, scanCode, action.MouseButton,
        SaturatingAdd(timeUs, Math.Max(1_000, action.DeadlineUs - action.TimeUs)),
        action.RequiredPage, action.RequiredSlot, action.Resource, action.FallbackPolicy, action.NoteNumber);

    private static ushort NextId(ref ushort value)
    {
        ushort current = value;
        value++;
        if (value == 0) value = 1;
        return current == 0 ? NextId(ref value) : current;
    }

    private static long SaturatingAdd(long a, long b) =>
        b > 0 && a > long.MaxValue - b ? long.MaxValue : a + b;

    private static long SaturatingSubtract(long a, long b) =>
        b > 0 && a < long.MinValue + b ? long.MinValue : a - b;
}
