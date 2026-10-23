using MidiAutoPlayer.Models;
using MidiAutoPlayer.Library;
using MidiAutoPlayer.Recognition;

namespace MidiAutoPlayer.Midi;

/// <summary>
/// Reconstruct the selected, converted MIDI stream saved in ConvertedSong. RawNotes is intentionally
/// preserved as the full source MIDI and must not be used as the playback stream after import filters.
/// </summary>
public static class EffectiveSongNotes
{
    public static List<RawMidiNote> ToGuitarOverlayNotes(ConvertedSong song)
    {
        var notes = ToRawNotes(song);
        if (SongLibrary.ResolveInstrument(song) != LibraryInstrument.Guitar)
            return notes;

        string recognitionMode = string.IsNullOrWhiteSpace(song.RecognitionModeName)
            ? song.Options?.RecognitionModeName ?? string.Empty
            : song.RecognitionModeName;
        if (!Enum.TryParse(recognitionMode, ignoreCase: true, out RecognitionMode mode)
            || mode != RecognitionMode.FullSong)
            return notes;

        // FullSong on a single source track is already the user's complete selection. Running
        // a lead extractor here can discard notes or change their onset, making the overlay
        // diverge from the imported song even though there are no accompaniment tracks to remove.
        if (notes.Select(note => note.TrackId).Distinct().Take(2).Count() <= 1)
            return notes;

        var lead = RecognitionService.GenerateAllCandidates(notes, tracks: null)[RecognitionMode.AutoLeadFusion].Notes;
        return lead.Count > 0 ? lead : notes;
    }

    public static List<RawMidiNote> ToRawNotes(ConvertedSong song)
    {
        if (SongLibrary.ResolveInstrument(song) == 1)
        {
            var guitarNotes = GetSelectedGuitarSourceNotes(song);
            if (guitarNotes != null)
                return guitarNotes;
        }

        var effective = song.Notes
            .OrderBy(note => note.TimeUs)
            .ThenBy(note => note.NoteIndex)
            .Select(note => new RawMidiNote
            {
                StartUs = note.TimeUs,
                DurationUs = note.DurationUs,
                NoteNumber = checked((byte)(MidiConverter.GameMinMidi + note.NoteIndex)),
                Velocity = 100,
                Channel = 0,
                TrackId = 0,
                Program = 0
            })
            .ToList();

        if (effective.Count > 0 || song.RawNotes is not { Count: > 0 } sourceNotes)
            return effective;

        // Older or range-filtered library entries can have an empty song.bin while retaining
        // the original MIDI. Reconstruct the configured stream so the song remains loadable.
        var options = song.Options;
        var selectedTracks = options?.EnabledTracks is { Count: > 0 }
            ? options.EnabledTracks.ToHashSet()
            : null;
        var fallback = new List<RawMidiNote>();
        var seen = new HashSet<(long TimeUs, int NoteNumber)>();
        var selectedSources = sourceNotes
            .Where(source => !((options?.IgnoreDrumChannel ?? true) && source.Channel == 9))
            .Where(source => selectedTracks == null || selectedTracks.Contains(source.TrackId))
            .Select(source => PhraseOctaveFolder.CloneWithPitch(
                source, Math.Clamp(source.NoteNumber + (options?.Transpose ?? 0), 0, 127)))
            .OrderBy(note => note.StartUs)
            .ThenBy(note => note.NoteNumber)
            .ToList();
        bool foldPhrases = string.Equals(options?.OutOfRangeMode, "OctaveFold", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<RawMidiNote> mappedSources = foldPhrases
            ? PhraseOctaveFolder.Apply(selectedSources, MidiConverter.GameMinMidi, MidiConverter.GameMaxMidi)
            : selectedSources.Where(note => note.NoteNumber >= MidiConverter.GameMinMidi && note.NoteNumber <= MidiConverter.GameMaxMidi).ToList();

        foreach (var source in mappedSources)
        {
            int pitch = source.NoteNumber;
            if (!seen.Add((source.StartUs, pitch))) continue;
            fallback.Add(new RawMidiNote
            {
                StartUs = source.StartUs,
                DurationUs = source.DurationUs,
                NoteNumber = (byte)pitch,
                Velocity = source.Velocity,
                Channel = source.Channel,
                TrackId = source.TrackId,
                Program = source.Program,
                OrigString = source.OrigString,
                OrigFret = source.OrigFret,
                VoiceIndex = source.VoiceIndex,
                IsLetRing = source.IsLetRing,
                IsPalmMute = source.IsPalmMute,
                IsBrushChord = source.IsBrushChord,
                IsBrushUp = source.IsBrushUp,
                ExplicitChordName = source.ExplicitChordName,
                SourceFormat = source.SourceFormat
            });
        }

        return fallback;
    }

    private static List<RawMidiNote>? GetSelectedGuitarSourceNotes(ConvertedSong song)
    {
        IReadOnlyList<RawMidiNote>? selected = song.SelectedRawNotes;
        if (selected == null)
        {
            if (song.RawNotes is not { Count: > 0 } sourceNotes)
                return null;

            var options = song.Options;
            var tracks = options.EnabledTracks is { Count: > 0 }
                ? options.EnabledTracks.ToHashSet()
                : null;
            var filtered = sourceNotes
                .Where(note => !options.IgnoreDrumChannel || note.Channel != 9)
                .Where(note => tracks == null || tracks.Contains(note.TrackId))
                .OrderBy(note => note.StartUs)
                .ThenBy(note => note.NoteNumber)
                .ToList();

            string strategy = song.RecognitionModeName ?? options.RecognitionModeName;
            if (strategy is not "FullSong" and not "ManualTracks" && Enum.TryParse<RecognitionMode>(strategy, out var mode))
            {
                var candidates = RecognitionService.GenerateAllCandidates(filtered, tracks: null);
                if (candidates.TryGetValue(mode, out var chosen) && chosen.Notes.Count > 0)
                    filtered = chosen.Notes;
            }
            selected = filtered;
        }

        int transpose = song.Options?.Transpose ?? 0;
        return selected.Select(note => new RawMidiNote
        {
            StartUs = note.StartUs,
            DurationUs = note.DurationUs,
            NoteNumber = (byte)Math.Clamp(note.NoteNumber + transpose, 0, 127),
            Velocity = note.Velocity,
            Channel = note.Channel,
            TrackId = note.TrackId,
            Program = note.Program,
            BarIndex = note.BarIndex,
            BeatInBar = note.BeatInBar,
            BeatsPerBar = note.BeatsPerBar,
            BeatUnitDenominator = note.BeatUnitDenominator,
            HasMeterPosition = note.HasMeterPosition,
            OrigString = note.OrigString,
            OrigFret = note.OrigFret,
            VoiceIndex = note.VoiceIndex,
            IsLetRing = note.IsLetRing,
            IsPalmMute = note.IsPalmMute,
            IsBrushChord = note.IsBrushChord,
            IsBrushUp = note.IsBrushUp,
            ExplicitChordName = note.ExplicitChordName,
            SourceFormat = note.SourceFormat
        }).OrderBy(note => note.StartUs).ThenBy(note => note.NoteNumber).ToList();
    }
}
