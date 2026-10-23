using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Guitar;

/// <summary>
/// Game chord-wheel pitches and safe-string masks recovered from chord.json.
/// String arrays are ordered by physical string number: 1 (highest) through 6 (lowest).
/// This table is shared by hybrid and detailed chord accompaniment compilation.
/// </summary>
public static class GameChordGroundTruth
{
    public static readonly byte[] OpenStringMidi1To6 = [64, 59, 55, 50, 45, 40];

    public sealed record ChordGroundTruthInfo(
        ChordId Id,
        string Name,
        byte Page,
        byte Slot,
        sbyte[] Offsets1To6,
        byte[] ActualMidi1To6,
        byte[] SafeCleanStrings,
        bool CanUseFullMouseStrum);

    public static readonly IReadOnlyDictionary<ChordId, ChordGroundTruthInfo> Table;

    static GameChordGroundTruth()
    {
        var definitions = new (ChordId Id, string Name, byte Page, byte Slot, sbyte[] Offsets, byte[] SafeStrings, bool FullStrum)[]
        {
            (ChordId.G, "G", 1, 1, [3, 0, 0, 0, 2, 3], [1, 2, 3, 4, 5, 6], true),
            (ChordId.Am, "Am", 1, 2, [0, 0, 2, 2, 1, 0], [1, 2, 3, 4, 6], false),
            (ChordId.Bm, "Bm", 1, 3, [2, 2, 4, 4, 3, 2], [1, 2, 3, 4, 6], false),
            (ChordId.C, "C", 1, 4, [0, 1, 0, 2, 8, 0], [1, 2, 3, 4], false),
            (ChordId.D, "D", 1, 5, [2, 4, 2, 0, 0, 0], [1, 3, 4, 5], false),
            (ChordId.Em, "Em", 1, 6, [0, 2, 2, 0, 0, 0], [1, 4, 6], false),

            (ChordId.Cmaj7, "Cmaj7", 2, 1, [0, 0, 0, 2, 8, 0], [1, 2, 3, 4], false),
            (ChordId.C7, "C7", 2, 2, [0, 1, 3, 2, 8, 0], [1, 2, 3, 4], false),
            (ChordId.Cm7, "Cm7", 2, 3, [4, 4, 3, 10, 8, 4], [2, 3, 4], false),
            (ChordId.G7, "G7", 2, 4, [1, 0, 0, 0, 2, 3], [1, 2, 3, 4, 5, 6], true),
            (ChordId.C9, "C9", 2, 5, [4, 4, 3, 2, 8, 4], [3, 4], false),
            (ChordId.Cm9, "Cm9", 2, 6, [4, 4, 3, 1, 8, 4], [2, 3, 4], false),

            (ChordId.C5, "C5", 3, 1, [-11, 5, 4, 15, 15, 13], [1, 4, 5, 6], false),
            (ChordId.D5, "D5", 3, 2, [2, 4, 2, 0, 0, 2], [1, 3, 4, 5, 6], false),
            (ChordId.G5, "G5", 3, 3, [-21, 8, 4, 5, 5, 3], [1, 2, 3, 4, 5, 6], true),
            (ChordId.A5, "A5", 3, 4, [5, 5, 2, 2, 0, 1], [1, 2, 3, 4, 5], false),
            (ChordId.Cmaj9, "Cmaj9", 3, 5, [0, 3, 4, 5, 7, 13], [1, 2, 3, 4, 5], false),
            (ChordId.Cadd9, "Cadd9", 3, 6, [-11, 9, 8, 5, 7, 13], [4, 5], false),

            (ChordId.G_B, "G/B", 4, 1, [3, 0, 0, 0, 2, 3], [1, 2, 3, 4, 5, 6], true),
            (ChordId.Am7_G, "Am7/G", 4, 2, [0, 1, 2, 2, 0, 3], [1, 2, 3, 4, 5, 6], true),
            (ChordId.D_Fsharp, "D/F#", 4, 3, [2, 4, 2, 0, -3, 2], [1, 3, 4, 5, 6], false),
            (ChordId.F_A, "F/A", 4, 4, [1, 1, 2, 3, 0, 1], [1, 2, 3, 4, 5, 6], true),
            (ChordId.A, "A", 4, 5, [0, 3, 2, 2, 0, 1], [1, 2, 3, 4, 5], false),
            (ChordId.Dm, "Dm", 4, 6, [1, 4, 2, 0, 0, 4], [1, 3, 4, 5], false)
        };

        var table = new Dictionary<ChordId, ChordGroundTruthInfo>(definitions.Length);
        foreach (var definition in definitions)
        {
            var actualMidi = new byte[6];
            var pitches6To1 = ChordAnalyzerV2.ChordStringPitches[definition.Id];
            for (int i = 0; i < actualMidi.Length; i++)
                actualMidi[i] = pitches6To1[5 - i];
            var offsets1To6 = actualMidi.Select((pitch, i) =>
                (sbyte)(pitch - OpenStringMidi1To6[i])).ToArray();

            table.Add(definition.Id, new ChordGroundTruthInfo(
                definition.Id, definition.Name, definition.Page, definition.Slot,
                offsets1To6, actualMidi, definition.SafeStrings, definition.FullStrum));
        }

        Table = table;
    }

    public static byte FindExactCleanString(ChordId chord, byte targetMidiPitch, IReadOnlySet<byte>? occupiedStrings = null)
    {
        if (!Table.TryGetValue(chord, out var info)) return 0;
        foreach (byte stringNo in info.SafeCleanStrings)
        {
            if ((occupiedStrings == null || !occupiedStrings.Contains(stringNo)) &&
                info.ActualMidi1To6[stringNo - 1] == targetMidiPitch)
                return stringNo;
        }
        return 0;
    }

    /// <summary>Finds the closest safe string with the same pitch class, or zero when none exists.</summary>
    public static byte FindPitchClassCompatibleString(ChordId chord, byte targetMidiPitch, IReadOnlySet<byte>? occupiedStrings = null)
    {
        byte exact = FindExactCleanString(chord, targetMidiPitch, occupiedStrings);
        if (exact != 0) return exact;
        if (!Table.TryGetValue(chord, out var info)) return 0;

        byte bestString = 0;
        int bestDistance = int.MaxValue;
        foreach (byte stringNo in info.SafeCleanStrings)
        {
            if (occupiedStrings != null && occupiedStrings.Contains(stringNo)) continue;
            byte actualPitch = info.ActualMidi1To6[stringNo - 1];
            if (actualPitch % 12 != targetMidiPitch % 12) continue;
            int distance = Math.Abs(actualPitch - targetMidiPitch);
            if (distance < bestDistance)
            {
                bestString = stringNo;
                bestDistance = distance;
            }
        }
        return bestString;
    }
}
