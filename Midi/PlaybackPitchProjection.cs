using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Midi;

/// <summary>
/// Converts the pitch-bearing events produced by each playback compiler into MIDI
/// notes for audition, so the preview follows the same pitch decisions as playback.
/// </summary>
public static class PlaybackPitchProjection
{
    public static List<RawMidiNote> FromPianoEvents(IEnumerable<GameNoteEvent> events) =>
        events.Select(note => new RawMidiNote
        {
            StartUs = note.TimeUs,
            DurationUs = note.DurationUs,
            NoteNumber = checked((byte)(MidiConverter.GameMinMidi + note.NoteIndex)),
            Velocity = 100,
            Channel = 0
        }).ToList();

    public static List<RawMidiNote> FromFretEvents(IEnumerable<FretPlayEvent> events) =>
        events.Where(note => !note.IsSkipped)
            .Select(note => new RawMidiNote
            {
                StartUs = note.TimeUs,
                DurationUs = note.DurationUs,
                NoteNumber = note.NoteNumber,
                Velocity = note.Velocity,
                Channel = 0
            })
            .ToList();

    public static List<RawMidiNote> FromChordEvents(
        IEnumerable<ChordEvent> events,
        ChordVoicingProfile voicing,
        GuitarPlayStyle playStyle)
    {
        var result = new List<RawMidiNote>();
        foreach (var chord in events)
        {
            if (chord.Chord == ChordId.Unknown) continue;
            var attacks = chord.AttackNotes ?? new List<RawMidiNote>();

            if (attacks.Any(note => note.OrigString is >= 1 and <= 6))
            {
                foreach (var note in attacks.Where(note => note.OrigString is >= 1 and <= 6))
                {
                    result.Add(new RawMidiNote
                    {
                        StartUs = note.StartUs,
                        DurationUs = note.DurationUs,
                        NoteNumber = note.NoteNumber,
                        Velocity = note.Velocity,
                        Channel = 0,
                        OrigString = note.OrigString
                    });
                }
                continue;
            }

            ChordArticulation articulation = chord.Articulation;
            if (playStyle == GuitarPlayStyle.BlockOnly) articulation = ChordArticulation.Block;
            else if (playStyle == GuitarPlayStyle.StrumOnly) articulation = ChordArticulation.Strum;
            else if (playStyle == GuitarPlayStyle.PluckOnly) articulation = ChordArticulation.Arpeggio;

            var pitches = voicing.GetVoicing(chord.Chord);
            switch (articulation)
            {
                case ChordArticulation.Strum:
                    AddVoicingAt(chord.StartUs, attacks, pitches, result);
                    break;
                case ChordArticulation.Block:
                {
                    var targetStrings = new List<byte>();
                    var occupied = new HashSet<byte>();
                    foreach (var note in attacks.OrderBy(note => note.NoteNumber))
                    {
                        byte stringNo = voicing.MatchNoteToString(note.NoteNumber, chord.Chord, occupied, out _);
                        if (stringNo is >= 1 and <= 6 && occupied.Add(stringNo)) targetStrings.Add(stringNo);
                    }
                    if (targetStrings.Count == 0) targetStrings.AddRange([1, 2, 3, 4]);
                    foreach (byte stringNo in targetStrings)
                    {
                        if (pitches[stringNo - 1] is byte pitch)
                            result.Add(CreateNote(chord.StartUs, attacks, pitch));
                    }
                    break;
                }
                default:
                {
                    var notes = attacks.Count > 0 ? attacks : chord.SourceNotes;
                    var occupied = new HashSet<byte>();
                    foreach (var note in notes.OrderBy(note => note.StartUs))
                    {
                        byte stringNo = voicing.MatchNoteToString(note.NoteNumber, chord.Chord, occupied, out _);
                        occupied.Add(stringNo);
                        if (stringNo is >= 1 and <= 6 && pitches[stringNo - 1] is byte pitch)
                            result.Add(new RawMidiNote
                            {
                                StartUs = note.StartUs,
                                DurationUs = note.DurationUs,
                                NoteNumber = pitch,
                                Velocity = note.Velocity,
                                Channel = 0
                            });
                    }
                    break;
                }
            }
        }

        return result.OrderBy(note => note.StartUs).ThenBy(note => note.NoteNumber).ToList();
    }

    private static void AddVoicingAt(long startUs, IReadOnlyList<RawMidiNote> attacks, byte?[] pitches, List<RawMidiNote> result)
    {
        long durationUs = attacks.Count > 0 ? attacks.Max(note => note.DurationUs) : 300_000;
        byte velocity = attacks.Count > 0 ? attacks.Max(note => note.Velocity) : (byte)100;
        foreach (byte? pitch in pitches)
        {
            if (pitch.HasValue)
                result.Add(new RawMidiNote
                {
                    StartUs = startUs,
                    DurationUs = durationUs,
                    NoteNumber = pitch.Value,
                    Velocity = velocity,
                    Channel = 0
                });
        }
    }

    private static RawMidiNote CreateNote(long startUs, IReadOnlyList<RawMidiNote> attacks, byte pitch) => new()
    {
        StartUs = startUs,
        DurationUs = attacks.Count > 0 ? attacks.Max(note => note.DurationUs) : 300_000,
        NoteNumber = pitch,
        Velocity = attacks.Count > 0 ? attacks.Max(note => note.Velocity) : (byte)100,
        Channel = 0
    };
}
