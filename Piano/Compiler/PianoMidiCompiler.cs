using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MidiAutoPlayer.Midi;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Piano.Mapping;
using MidiAutoPlayer.Piano.Models;

namespace MidiAutoPlayer.Piano.Compiler;

public static class PianoMidiCompiler
{
    public const long MinKeyHoldUs = 20_000;
    public const long MinRetriggerGapUs = 8_000;
    public const long ChordModifierStaggerStepUs = 16_000;
    public const long ModifierLeadInUs = 5_000;
    public const long ModifierPulseHoldUs = 10_000;
    private const long ChordClusterWindowUs = 15_000;
    private const long ModifierSafeGapUs = 5_000;
    private const long ModifierContextWindowUs = 45_000;
    private static readonly JsonSerializerOptions FingerprintJsonOptions = new(JsonSerializerDefaults.Web);

    public static PianoCompileResult Compile(
        IReadOnlyList<RawMidiNote> notes,
        PianoLayoutProfile profile,
        PianoCompileOptions options,
        string? requestId = null)
    {
        requestId ??= Guid.NewGuid().ToString("N");
        options.ModeId = "GamePiano";
        int speed = Math.Clamp(options.SpeedPercent, 25, 200);
        options.SpeedPercent = speed;
        var issues = new List<PianoCompileIssue>();
        foreach (string error in profile.ValidateStructure())
            issues.Add(Issue(PianoIssueCode.InvalidLayout, error, PianoIssueSeverity.Error));

        string fingerprint = CalculateFingerprint(notes, profile, options);
        if (issues.Any(issue => issue.Severity == PianoIssueSeverity.Error))
            return Finish(requestId, options, profile, fingerprint, notes.Count, 0, notes.Count, [], issues, options.TransposeSemitones, speed);

        int effectiveTranspose = profile.Kind == PianoLayoutKind.SevenKeyScale
            ? PianoPitchMapper.FindBestTwentyOneKeyTranspose(notes, profile, options.TransposeSemitones)
            : PianoPitchMapper.FindBestAutoOctaveTranspose(notes, options.TransposeSemitones);
        var transposedNotes = notes
            .Where(note => !options.FilterDrumChannel10 || note.Channel != 9)
            .Where(note => note.StartUs >= 0)
            .Select(note => PhraseOctaveFolder.CloneWithPitch(note,
                Math.Clamp(note.NoteNumber + effectiveTranspose, 0, 127)))
            .ToList();
        int phraseMinMidi = profile.Kind == PianoLayoutKind.SevenKeyScale
            ? profile.ScaleProfile!.TonicMidiPitchAtMiddleRow - 12
            : PianoPitchMapper.PlayableMinMidi;
        int phraseMaxMidi = profile.Kind == PianoLayoutKind.SevenKeyScale
            ? profile.ScaleProfile!.TonicMidiPitchAtMiddleRow + 23
            : PianoPitchMapper.PlayableMaxMidi;
        var phraseMappedNotes = PhraseOctaveFolder.Apply(transposedNotes, phraseMinMidi, phraseMaxMidi);
        int phraseRangeSkips = transposedNotes.Count - phraseMappedNotes.Count;
        var planned = new List<PianoPlannedNote>(phraseMappedNotes.Count);
        int filteredDrums = options.FilterDrumChannel10 ? notes.Count(note => note.Channel == 9) : 0;
        int foldedCount = phraseMappedNotes.Count(note => note.WasOctaveShifted);
        int skippedCount = phraseRangeSkips;
        PianoModifier? previousModifier = null;
        long? previousMappedStartUs = null;
        long? chordClusterStartUs = null;
        var occupiedSevenKeyNotes = new HashSet<(PianoRow Row, int Degree)>();

        foreach (RawMidiNote note in phraseMappedNotes.OrderBy(note => note.StartUs).ThenByDescending(note => note.NoteNumber))
        {
            int requestedPitch = note.NoteNumber;
            if (profile.Kind == PianoLayoutKind.SevenKeyScale)
            {
                if (chordClusterStartUs is null || note.StartUs - chordClusterStartUs.Value > ChordClusterWindowUs)
                {
                    chordClusterStartUs = note.StartUs;
                    occupiedSevenKeyNotes.Clear();
                }
            }
            bool wasAdapted = false;
            PianoModifier? preferredModifier = previousModifier is PianoModifier previous &&
                previousMappedStartUs is long previousStart &&
                ScaleAbsoluteTimeUs(note.StartUs - previousStart, speed) < ModifierContextWindowUs
                    ? previous
                    : null;
            PianoMapResult mapped = profile.Kind switch
            {
                PianoLayoutKind.TwelveKeyChromatic => PianoPitchMapper.TryMapTwelveKey(requestedPitch, profile, out wasAdapted, preferredModifier),
                PianoLayoutKind.SevenKeyScale => PianoPitchMapper.TryMapSevenKey(requestedPitch, profile, out wasAdapted, occupiedSevenKeyNotes),
                _ => new PianoMapResult(null, new PianoMapIssue(PianoIssueCode.InvalidLayout, "不支持的布局类型。"))
            };
            if (!mapped.IsMapped || mapped.MappedNote is null)
            {
                skippedCount++;
                continue;
            }
            if (wasAdapted) foldedCount++;

            try
            {
                long duration = Math.Max(MinKeyHoldUs, note.DurationUs);
                long startUs = ScaleAbsoluteTimeUs(note.StartUs, speed);
                long endUs = ScaleAbsoluteTimeUs(checked(note.StartUs + duration), speed);
                endUs = Math.Max(endUs, checked(startUs + MinKeyHoldUs));

                PianoModifierKey? modifier = GetModifier(profile, mapped.MappedNote.Modifier);
                ushort? modifierScan = mapped.MappedNote.Modifier == PianoModifier.None ? null : modifier?.ScanCode;
                if (mapped.MappedNote.Modifier != PianoModifier.None && modifierScan is null or 0)
                {
                    skippedCount++;
                    continue;
                }

                planned.Add(new PianoPlannedNote(
                    startUs,
                    endUs,
                    mapped.MappedNote.Binding.ScanCode!.Value,
                    mapped.MappedNote.Binding.Extended,
                    modifierScan,
                    modifier?.Extended ?? false,
                    mapped.MappedNote.Modifier,
                    mapped.MappedNote.MidiPitch,
                    note.TrackId));
                if (profile.Kind == PianoLayoutKind.SevenKeyScale)
                    occupiedSevenKeyNotes.Add((mapped.MappedNote.Row, mapped.MappedNote.Degree));
                previousModifier = mapped.MappedNote.Modifier;
                previousMappedStartUs = note.StartUs;
            }
            catch (OverflowException)
            {
                skippedCount++;
            }
        }

        var (resolved, resolvedChords, trimmedNotes) = AutoResolveChordAndKeyConflicts(planned, profile);
        if (effectiveTranspose != options.TransposeSemitones)
            issues.Add(Issue(PianoIssueCode.OutOfRegister, $"已自动调整整体音高 {effectiveTranspose:+#;-#;0} 半音。", PianoIssueSeverity.Info));
        if (foldedCount > 0)
            issues.Add(Issue(PianoIssueCode.OutOfRegister, $"已自动适配 {foldedCount} 个音符的音域或调式。", PianoIssueSeverity.Info));
        if (resolvedChords > 0)
            issues.Add(Issue(PianoIssueCode.ModifierConflict, $"已自动处理 {resolvedChords} 处和弦修饰键冲突。", PianoIssueSeverity.Info));
        if (trimmedNotes > 0)
            issues.Add(Issue(PianoIssueCode.PolyphonyLimitExceeded, $"已按键盘复音上限精简 {trimmedNotes} 个伴奏音。", PianoIssueSeverity.Info));
        if (filteredDrums > 0)
            issues.Add(Issue(PianoIssueCode.OutOfRegister, $"已过滤 {filteredDrums} 个鼓声道音符。", PianoIssueSeverity.Info));
        if (skippedCount > 0)
            issues.Add(Issue(PianoIssueCode.MissingBinding, $"已忽略 {skippedCount} 个无效 MIDI 事件。", PianoIssueSeverity.Warning));

        resolved = EnforceModifierStateGaps(resolved);
        resolved = ResolveSamePhysicalKeyOverlaps(resolved);
        IReadOnlyList<PianoAction> actions = CreateActionsWithPulseModifiers(resolved);
        return Finish(requestId, options, profile, fingerprint, notes.Count, resolved.Count, skippedCount, actions, issues, effectiveTranspose, speed);
    }

    private static (List<PianoPlannedNote> Resolved, int ConflictResolvedCount, int PolyphonyTrimmedCount)
        AutoResolveChordAndKeyConflicts(IReadOnlyList<PianoPlannedNote> notes, PianoLayoutProfile profile)
    {
        if (notes.Count == 0) return ([], 0, 0);
        int conflictResolvedCount = 0;
        int polyphonyTrimmedCount = 0;
        var sorted = notes.OrderBy(note => note.StartUs).ThenByDescending(note => note.MidiPitch).ToList();
        var staged = new List<PianoPlannedNote>(sorted.Count);

        for (int index = 0; index < sorted.Count;)
        {
            long clusterStart = sorted[index].StartUs;
            var cluster = new List<PianoPlannedNote>();
            while (index < sorted.Count && sorted[index].StartUs - clusterStart <= ChordClusterWindowUs)
                cluster.Add(sorted[index++]);

            cluster = cluster
                .GroupBy(note => (note.NoteScanCode, note.NoteExtended, note.Modifier))
                .Select(group => group.OrderByDescending(note => note.EndUs - note.StartUs).First())
                .OrderByDescending(note => note.MidiPitch)
                .ToList();

            if (profile.PolyphonyLimitIsVerified && profile.VerifiedPolyphonyLimit is int limit && limit > 0 && cluster.Count > limit)
            {
                polyphonyTrimmedCount += cluster.Count - limit;
                var kept = new List<PianoPlannedNote> { cluster[0] };
                if (limit >= 2 && cluster.Count >= 2) kept.Add(cluster[^1]);
                for (int i = 1; i < cluster.Count - 1 && kept.Count < limit; i++) kept.Add(cluster[i]);
                cluster = kept.OrderByDescending(note => note.MidiPitch).ToList();
            }

            if (cluster.Select(note => note.Modifier).Distinct().Count() > 1 && profile.Kind == PianoLayoutKind.TwelveKeyChromatic)
            {
                conflictResolvedCount++;
                if (cluster.Any(note => note.Modifier == PianoModifier.Shift) && cluster.Any(note => note.Modifier == PianoModifier.Control))
                {
                    for (int i = 0; i < cluster.Count; i++)
                    {
                        if (cluster[i].Modifier != PianoModifier.Control) continue;
                        PianoMapResult alternate = PianoPitchMapper.TryMapTwelveKey(cluster[i].MidiPitch, profile, out _, PianoModifier.Shift);
                        if (!alternate.IsMapped || alternate.MappedNote?.Modifier != PianoModifier.Shift) continue;
                        PianoModifierKey? shift = profile.ModifierProfile?.Shift;
                        cluster[i] = cluster[i] with
                        {
                            NoteScanCode = alternate.MappedNote.Binding.ScanCode!.Value,
                            NoteExtended = alternate.MappedNote.Binding.Extended,
                            Modifier = PianoModifier.Shift,
                            ModifierScanCode = shift?.ScanCode,
                            ModifierExtended = shift?.Extended ?? false
                        };
                    }
                }

                long clusterAnchorUs = cluster.Min(note => note.StartUs);
                PianoModifier leadModifier = cluster.OrderByDescending(note => note.MidiPitch).First().Modifier;
                var modifierOrder = cluster.Select(note => note.Modifier).Distinct()
                    .OrderBy(modifier => modifier == leadModifier ? 0 : 1)
                    .ThenBy(modifier => modifier)
                    .ToList();
                for (int groupIndex = 0; groupIndex < modifierOrder.Count; groupIndex++)
                {
                    PianoModifier modifier = modifierOrder[groupIndex];
                    long targetStartUs = checked(clusterAnchorUs + groupIndex * ChordModifierStaggerStepUs);
                    for (int i = 0; i < cluster.Count; i++)
                    {
                        if (cluster[i].Modifier != modifier) continue;
                        long shift = targetStartUs - cluster[i].StartUs;
                        long start = targetStartUs;
                        cluster[i] = cluster[i] with { StartUs = start, EndUs = Math.Max(checked(start + MinKeyHoldUs), checked(cluster[i].EndUs + shift)) };
                    }
                }
            }
            staged.AddRange(cluster);
        }

        return (ResolveSamePhysicalKeyOverlaps(staged), conflictResolvedCount, polyphonyTrimmedCount);
    }

    private static List<PianoPlannedNote> ResolveSamePhysicalKeyOverlaps(IEnumerable<PianoPlannedNote> notes)
    {
        var finalNotes = new List<PianoPlannedNote>();
        foreach (var keyGroup in notes.GroupBy(note => (note.NoteScanCode, note.NoteExtended)))
        {
            List<PianoPlannedNote> ordered = keyGroup.OrderBy(note => note.StartUs).ThenBy(note => note.EndUs).ToList();
            for (int i = 0; i < ordered.Count; i++)
            {
                PianoPlannedNote current = ordered[i];
                if (i + 1 < ordered.Count)
                {
                    PianoPlannedNote next = ordered[i + 1];
                    long maxEnd = next.StartUs - MinRetriggerGapUs;
                    if (current.EndUs > maxEnd)
                    {
                        long currentEnd = Math.Max(current.StartUs + MinKeyHoldUs, maxEnd);
                        if (next.StartUs < currentEnd + MinRetriggerGapUs)
                        {
                            long nextStart = currentEnd + MinRetriggerGapUs;
                            ordered[i + 1] = next with
                            {
                                StartUs = nextStart,
                                EndUs = Math.Max(nextStart + MinKeyHoldUs, next.EndUs + nextStart - next.StartUs)
                            };
                        }
                        current = current with { EndUs = currentEnd };
                    }
                }
                finalNotes.Add(current);
            }
        }
        return finalNotes.OrderBy(note => note.StartUs).ToList();
    }

    private static IReadOnlyList<PianoAction> CreateActionsWithPulseModifiers(IReadOnlyList<PianoPlannedNote> notes)
    {
        var actions = new List<PianoAction>(notes.Count * 4);
        foreach (PianoPlannedNote note in notes)
        {
            actions.Add(new PianoAction(note.StartUs, PianoActionType.KeyDown, note.NoteScanCode, note.NoteExtended, note.MidiPitch));
            actions.Add(new PianoAction(note.EndUs, PianoActionType.KeyUp, note.NoteScanCode, note.NoteExtended, note.MidiPitch));
        }

        var groups = notes.Where(note => note.Modifier != PianoModifier.None && note.ModifierScanCode is > 0)
            .GroupBy(note => (note.Modifier, ScanCode: note.ModifierScanCode!.Value, note.ModifierExtended));
        foreach (var group in groups)
        {
            List<PianoPlannedNote> ordered = group.OrderBy(note => note.StartUs).ToList();
            long pulseStart = Math.Max(0, ordered[0].StartUs - ModifierLeadInUs);
            long pulseEnd = GetModifierPulseEnd(ordered[0], notes);
            for (int i = 1; i < ordered.Count; i++)
            {
                long nextStart = Math.Max(0, ordered[i].StartUs - ModifierLeadInUs);
                long nextEnd = GetModifierPulseEnd(ordered[i], notes);
                if (nextStart <= pulseEnd + 4_000)
                {
                    pulseEnd = Math.Max(pulseEnd, nextEnd);
                    continue;
                }
                AddModifierPulse(actions, group.Key, pulseStart, pulseEnd, ordered[i - 1].MidiPitch);
                pulseStart = nextStart;
                pulseEnd = nextEnd;
            }
            pulseEnd = Math.Min(pulseEnd, GetModifierPulseEnd(ordered[^1], notes));
            AddModifierPulse(actions, group.Key, pulseStart, pulseEnd, ordered[^1].MidiPitch);
        }

        return actions.OrderBy(action => action.TimeUs)
            .ThenBy(action => EventPriority(action.Type))
            .ThenBy(action => action.ScanCode)
            .ToArray();
    }

    private static long GetModifierPulseEnd(PianoPlannedNote note, IReadOnlyList<PianoPlannedNote> notes)
    {
        long desiredEnd = checked(note.StartUs + ModifierPulseHoldUs);
        PianoPlannedNote? nextIncompatible = notes
            .Where(other => other.StartUs > note.StartUs && other.Modifier != note.Modifier)
            .OrderBy(other => other.StartUs)
            .FirstOrDefault();
        if (nextIncompatible is null) return desiredEnd;

        long deadline = nextIncompatible.StartUs - ModifierSafeGapUs -
            (nextIncompatible.Modifier == PianoModifier.None ? 0 : ModifierLeadInUs);
        return Math.Min(desiredEnd, deadline);
    }

    private static List<PianoPlannedNote> EnforceModifierStateGaps(IReadOnlyList<PianoPlannedNote> notes)
    {
        List<PianoPlannedNote> ordered = notes.OrderBy(note => note.StartUs).ThenByDescending(note => note.MidiPitch).ToList();
        for (int i = 0; i + 1 < ordered.Count; i++)
        {
            PianoPlannedNote previous = ordered[i];
            PianoPlannedNote next = ordered[i + 1];
            if (previous.Modifier == PianoModifier.None || previous.Modifier == next.Modifier) continue;

            long requiredGap = ModifierPulseHoldUs + ModifierSafeGapUs +
                (next.Modifier == PianoModifier.None ? 0 : ModifierLeadInUs);
            long minNextStart = checked(previous.StartUs + requiredGap);
            if (next.StartUs >= minNextStart) continue;

            long shift = minNextStart - next.StartUs;
            for (int j = i + 1; j < ordered.Count; j++)
            {
                PianoPlannedNote delayed = ordered[j];
                long shiftedStart = checked(delayed.StartUs + shift);
                ordered[j] = delayed with
                {
                    StartUs = shiftedStart,
                    EndUs = Math.Max(checked(shiftedStart + MinKeyHoldUs), checked(delayed.EndUs + shift))
                };
            }
        }
        return ordered;
    }

    private static void AddModifierPulse(
        ICollection<PianoAction> actions,
        (PianoModifier Modifier, ushort ScanCode, bool ModifierExtended) modifier,
        long start,
        long end,
        int midiPitch)
    {
        actions.Add(new PianoAction(start, PianoActionType.ModifierDown, modifier.ScanCode, modifier.ModifierExtended, midiPitch, modifier.Modifier));
        actions.Add(new PianoAction(end, PianoActionType.ModifierUp, modifier.ScanCode, modifier.ModifierExtended, midiPitch, modifier.Modifier));
    }

    private static int EventPriority(PianoActionType type) => type switch
    {
        PianoActionType.KeyUp => 0,
        PianoActionType.ModifierUp => 1,
        PianoActionType.ModifierDown => 2,
        PianoActionType.KeyDown => 3,
        _ => 4
    };

    public static long ScaleAbsoluteTimeUs(long timeUs, int speedPercent)
    {
        if (timeUs < 0) return 0;
        int speed = Math.Clamp(speedPercent, 25, 200);
        return checked((long)Math.Round((decimal)timeUs * 100m / speed, MidpointRounding.AwayFromZero));
    }

    private static PianoModifierKey? GetModifier(PianoLayoutProfile profile, PianoModifier modifier) => modifier switch
    {
        PianoModifier.Shift => profile.ModifierProfile?.Shift,
        PianoModifier.Control => profile.ModifierProfile?.Control,
        _ => null
    };

    private static PianoCompileIssue Issue(PianoIssueCode code, string message, PianoIssueSeverity severity) =>
        new(code, null, 0, null, message, severity);

    private static PianoCompileResult Finish(
        string requestId,
        PianoCompileOptions options,
        PianoLayoutProfile profile,
        string fingerprint,
        int sourceCount,
        int compiledCount,
        int skippedCount,
        IReadOnlyList<PianoAction> actions,
        IReadOnlyList<PianoCompileIssue> issues,
        int effectiveTranspose,
        int effectiveSpeed) =>
        new(requestId, options.ModeId, profile.Id, fingerprint, actions, issues, sourceCount, compiledCount, skippedCount,
            effectiveTranspose, effectiveSpeed, RequiresUserConfirmation: false,
            CanStart: actions.Count > 0 && issues.All(issue => issue.Severity != PianoIssueSeverity.Error));

    private static string CalculateFingerprint(
        IReadOnlyList<RawMidiNote> notes,
        PianoLayoutProfile profile,
        PianoCompileOptions options)
    {
        var canonical = new
        {
            Compiler = "game-piano-v3-fast-switch",
            SourceHash = options.SourceHash,
            Profile = profile,
            Speed = options.SpeedPercent,
            Transpose = options.TransposeSemitones,
            Notes = notes.Select(note => new { note.StartUs, note.DurationUs, note.NoteNumber, note.Velocity, note.Channel, note.TrackId, note.Program })
        };
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical, FingerprintJsonOptions));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
