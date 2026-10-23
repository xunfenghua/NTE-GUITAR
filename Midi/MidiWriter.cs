using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Common;
using Melanchall.DryWetMidi.Interaction;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Midi;

/// <summary>Writes editor notes while retaining timing and metadata events from a source MIDI.</summary>
public static class MidiWriter
{
    public static byte[] Write(IReadOnlyList<RawMidiNote> notes, string? sourceMidiPath = null, long? tempoOverrideUsPerQuarter = null)
    {
        ArgumentNullException.ThrowIfNull(notes);
        if (tempoOverrideUsPerQuarter is <= 0 or > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(tempoOverrideUsPerQuarter), "MIDI tempo must be a positive microseconds-per-quarter value.");
        MidiFile midiFile;
        TempoMap tempoMap;
        if (!string.IsNullOrWhiteSpace(sourceMidiPath) && File.Exists(sourceMidiPath))
        {
            midiFile = MidiFile.Read(sourceMidiPath);
            tempoMap = tempoOverrideUsPerQuarter.HasValue
                ? CreateTempoMap(midiFile.TimeDivision, tempoOverrideUsPerQuarter.Value)
                : midiFile.GetTempoMap();
        }
        else
        {
            // Imported notes with no source file default to 120 BPM unless the editor supplied a tempo.
            midiFile = new MidiFile();
            midiFile.TimeDivision = new TicksPerQuarterNoteTimeDivision(480);
            var tempoTrack = new TrackChunk();
            tempoTrack.Events.Add(new SetTempoEvent(checked((int)(tempoOverrideUsPerQuarter ?? 500_000))) { DeltaTime = 0 });
            tempoTrack.Events.Add(new TimeSignatureEvent(4, 4) { DeltaTime = 0 });
            midiFile.Chunks.Add(tempoTrack);
            tempoMap = tempoOverrideUsPerQuarter.HasValue
                ? CreateTempoMap(midiFile.TimeDivision, tempoOverrideUsPerQuarter.Value)
                : midiFile.GetTempoMap();
        }

        var tracks = midiFile.GetTrackChunks().ToList();
        if (tracks.Count == 0)
        {
            var track = new TrackChunk();
            midiFile.Chunks.Add(track);
            tracks.Add(track);
        }

        // Convert original delta times to absolute positions before removing note events.
        var retained = new List<List<(long Time, int Order, MidiEvent Event)>>();
        bool retainedTempo = false;
        for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            var track = tracks[trackIndex];
            long absoluteTime = 0;
            var events = new List<(long Time, int Order, MidiEvent Event)>();
            for (int eventIndex = 0; eventIndex < track.Events.Count; eventIndex++)
            {
                var midiEvent = track.Events[eventIndex];
                absoluteTime = checked(absoluteTime + midiEvent.DeltaTime);
                if (midiEvent is not NoteOnEvent and not NoteOffEvent)
                {
                    MidiEvent retainedEvent = midiEvent;
                    if (tempoOverrideUsPerQuarter.HasValue && midiEvent is SetTempoEvent)
                    {
                        retainedEvent = new SetTempoEvent(checked((int)tempoOverrideUsPerQuarter.Value));
                        retainedTempo = true;
                    }
                    events.Add((absoluteTime, eventIndex * 3 + 1, retainedEvent));
                }
            }
            retained.Add(events);
        }

        if (tempoOverrideUsPerQuarter.HasValue)
        {
            if (!retainedTempo)
                retained[0].Add((0, int.MinValue, new SetTempoEvent(checked((int)tempoOverrideUsPerQuarter.Value))));
        }

        foreach (var note in notes)
        {
            if (note.StartUs < 0 || note.DurationUs < 0 || note.NoteNumber > 127 || note.Velocity > 127 || note.Channel > 15)
                throw new InvalidDataException("An edited MIDI note contains an invalid value.");
            int trackIndex = Math.Max(0, note.TrackId);
            while (trackIndex >= tracks.Count)
            {
                var track = new TrackChunk();
                midiFile.Chunks.Add(track);
                tracks.Add(track);
                retained.Add(new List<(long, int, MidiEvent)>());
            }

            long start = ToTicks(note.StartUs, tempoMap);
            long end = ToTicks(checked(note.StartUs + note.DurationUs), tempoMap);
            if (end < start) end = start;
            var events = retained[trackIndex];
            int tieOrder = events.Count == 0 ? 0 : events.Max(item => item.Order) + 3;
            if (note.Program is >= 0 and <= 127 &&
                !events.Any(item => item.Event is ProgramChangeEvent program && program.Channel == note.Channel))
            {
                events.Add((0, int.MinValue + note.Channel, new ProgramChangeEvent((SevenBitNumber)note.Program)
                {
                    Channel = (FourBitNumber)note.Channel
                }));
            }
            var on = new NoteOnEvent((SevenBitNumber)note.NoteNumber, (SevenBitNumber)Math.Max((byte)1, note.Velocity)) { Channel = (FourBitNumber)note.Channel };
            var off = new NoteOffEvent((SevenBitNumber)note.NoteNumber, (SevenBitNumber)0) { Channel = (FourBitNumber)note.Channel };
            events.Add((start, tieOrder + 1, on));
            events.Add((end, end == start ? tieOrder + 2 : tieOrder, off));
        }

        for (int trackIndex = 0; trackIndex < tracks.Count; trackIndex++)
        {
            var events = retained[trackIndex];
            long endOfTrackTime = events.Where(item => item.Event is not EndOfTrackEvent)
                .Select(item => item.Time).DefaultIfEmpty(0).Max();
            var endOfTrackEvents = events.Where(item => item.Event is EndOfTrackEvent).ToList();
            events.RemoveAll(item => item.Event is EndOfTrackEvent);
            if (endOfTrackEvents.Count > 0)
                endOfTrackEvents = endOfTrackEvents.Select(item => (Math.Max(item.Time, endOfTrackTime), int.MaxValue, item.Event)).ToList();
            events.AddRange(endOfTrackEvents);

            events = events
                .OrderBy(item => item.Time)
                .ThenBy(item => item.Order)
                .ToList();
            tracks[trackIndex].Events.Clear();
            long previousTime = 0;
            foreach (var item in events)
            {
                item.Event.DeltaTime = checked(item.Time - previousTime);
                tracks[trackIndex].Events.Add(item.Event);
                previousTime = item.Time;
            }
        }

        using var stream = new MemoryStream();
        midiFile.Write(stream, MidiFileFormat.MultiTrack, null);
        return stream.ToArray();
    }

    private static long ToTicks(long microseconds, TempoMap tempoMap)
    {
        var time = TimeConverter.ConvertTo<MidiTimeSpan>(new MetricTimeSpan(microseconds), tempoMap);
        return Math.Max(0, time.TimeSpan);
    }

    private static TempoMap CreateTempoMap(TimeDivision timeDivision, long tempoUsPerQuarter)
    {
        var tempoFile = new MidiFile { TimeDivision = timeDivision };
        var tempoTrack = new TrackChunk();
        tempoTrack.Events.Add(new SetTempoEvent(checked((int)tempoUsPerQuarter)) { DeltaTime = 0 });
        tempoFile.Chunks.Add(tempoTrack);
        return tempoFile.GetTempoMap();
    }
}
