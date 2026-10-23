namespace Nte.GuitarChart.Core;

public static class RawMidiNoteAdapter
{
    public static RawMidiNote FromExisting(
        MidiAutoPlayer.Models.RawMidiNote source,
        string? trackName = null)
    {
        return new RawMidiNote(
            source.StartUs,
            source.DurationUs,
            source.NoteNumber,
            source.Velocity,
            source.Channel,
            source.Program,
            source.TrackId,
            trackName
        );
    }

    public static List<RawMidiNote> FromExistingList(
        IEnumerable<MidiAutoPlayer.Models.RawMidiNote> sources,
        IReadOnlyDictionary<int, string>? trackNames = null)
    {
        return sources.Select(s =>
        {
            string? name = null;
            if (trackNames != null && trackNames.TryGetValue(s.TrackId, out var tn))
                name = tn;
            return FromExisting(s, name);
        }).ToList();
    }
}
