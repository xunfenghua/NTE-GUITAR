using System.Security.Cryptography;
using System.Text;
using Nte.GuitarChart.Core;

namespace MidiAutoPlayer.SemiAuto.Chart;

public sealed record StoredGuitarChart(
    int FormatVersion,
    string SongId,
    string SourceHash,
    string EffectiveNotesFingerprint,
    DateTime CompiledAt,
    ChartCompileOptions CompileOptions,
    GuitarChart Chart);

public sealed record GuitarChartLoadResult(StoredGuitarChart? Stored, bool IsStale, string? Error);

public static class EffectiveNotesFingerprint
{
    public static string Compute(IEnumerable<MidiAutoPlayer.Models.RawMidiNote> notes)
    {
        var canonical = string.Join("\n", notes
            .OrderBy(n => n.StartUs).ThenBy(n => n.NoteNumber).ThenBy(n => n.TrackId)
            .Select(n => string.Join(',', n.StartUs, n.DurationUs, n.NoteNumber, n.Velocity,
                n.Channel, n.TrackId, n.Program)));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }
}
