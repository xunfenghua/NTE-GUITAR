using System.Security.Cryptography;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Midi;

public static class MidiLoader
{
    public static (string Sha256, List<RawMidiNote> Notes, List<int> TrackIds, List<TrackMetadata> Tracks) Load(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("MIDI file not found", filePath);

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
        string sha256 = ComputeStreamSha256(fs);
        fs.Position = 0;

        var readingSettings = new ReadingSettings
        {
            InvalidChunkSizePolicy = InvalidChunkSizePolicy.Ignore,
            NotEnoughBytesPolicy = NotEnoughBytesPolicy.Ignore,
            NoHeaderChunkPolicy = NoHeaderChunkPolicy.Ignore,
            InvalidChannelEventParameterValuePolicy = InvalidChannelEventParameterValuePolicy.ReadValid,
            InvalidMetaEventParameterValuePolicy = InvalidMetaEventParameterValuePolicy.SnapToLimits,
            MissedEndOfTrackPolicy = MissedEndOfTrackPolicy.Ignore,
            SilentNoteOnPolicy = SilentNoteOnPolicy.NoteOff,
            ExtraTrackChunkPolicy = ExtraTrackChunkPolicy.Read,
            UnexpectedTrackChunksCountPolicy = UnexpectedTrackChunksCountPolicy.Ignore,
        };

        var midiFile = MidiFile.Read(fs, readingSettings);

        TempoMap tempoMap = midiFile.GetTempoMap();
        var rawNotes = new List<RawMidiNote>();
        var trackIds = new HashSet<int>();
        var trackMetas = new List<TrackMetadata>();

        var trackChunks = midiFile.GetTrackChunks().ToList();
        for (int i = 0; i < trackChunks.Count; i++)
        {
            var chunk = trackChunks[i];
            int trackId = i;

            string trackName = chunk.Events.OfType<SequenceTrackNameEvent>().FirstOrDefault()?.Text?.Trim() ?? "";
            int programNumber = -1;
            var progEvent = chunk.Events.OfType<ProgramChangeEvent>().FirstOrDefault();
            if (progEvent != null)
            {
                programNumber = progEvent.ProgramNumber;
            }

            var trackMeta = new TrackMetadata
            {
                TrackId = trackId,
                Name = trackName,
                Program = programNumber
            };

            var notes = chunk.GetNotes();
            foreach (var note in notes)
            {
                trackIds.Add(trackId);
                trackMeta.NoteCount++;
                trackMeta.Channels.Add((byte)note.Channel);

                int pitch = note.NoteNumber;
                if (trackMeta.MinPitch < 0 || pitch < trackMeta.MinPitch) trackMeta.MinPitch = pitch;
                if (trackMeta.MaxPitch < 0 || pitch > trackMeta.MaxPitch) trackMeta.MaxPitch = pitch;

                long startUs = note.TimeAs<MetricTimeSpan>(tempoMap).TotalMicroseconds;
                long durationUs = note.LengthAs<MetricTimeSpan>(tempoMap).TotalMicroseconds;
                var barBeat = note.TimeAs<BarBeatFractionTimeSpan>(tempoMap);
                var timeSignature = tempoMap.GetTimeSignatureAtTime(new MidiTimeSpan(note.Time));

                rawNotes.Add(new RawMidiNote
                {
                    StartUs = startUs,
                    DurationUs = durationUs,
                    NoteNumber = (byte)note.NoteNumber,
                    Velocity = (byte)note.Velocity,
                    Channel = (byte)note.Channel,
                    TrackId = trackId,
                    Program = programNumber,
                    BarIndex = barBeat.Bars,
                    BeatInBar = barBeat.Beats,
                    BeatsPerBar = (byte)Math.Clamp(timeSignature.Numerator, 1, 255),
                    BeatUnitDenominator = (byte)Math.Clamp(timeSignature.Denominator, 1, 255),
                    HasMeterPosition = true
                });
            }

            trackMetas.Add(trackMeta);
        }

        rawNotes.Sort((a, b) =>
        {
            int cmp = a.StartUs.CompareTo(b.StartUs);
            return cmp != 0 ? cmp : a.NoteNumber.CompareTo(b.NoteNumber);
        });

        MidiRawCache.Save(sha256, rawNotes);

        return (sha256, rawNotes, trackIds.OrderBy(t => t).ToList(), trackMetas);
    }

    public static string ComputeFileSha256(string filePath)
    {
        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 65536);
        return ComputeStreamSha256(fs);
    }

    public static string ComputeStreamSha256(Stream stream)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static string ComputeSha256(byte[] data)
    {
        using var sha = SHA256.Create();
        byte[] hash = sha.ComputeHash(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
