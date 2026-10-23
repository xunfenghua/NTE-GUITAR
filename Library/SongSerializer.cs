using System.Text.Json;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Library;

public static class SongSerializer
{
    private const uint Magic = 0x474E4F53; // 'SONG'
    private const uint RawMagic = 0x4D574152; // 'RAWM'
    public const int CurrentFormatVersion = 2;

    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static void SaveSong(string songDir, ConvertedSong song)
    {
        Directory.CreateDirectory(songDir);

        // 1. Write song.bin (binary format of notes)
        string binPath = Path.Combine(songDir, "song.bin");
        string binTmp = binPath + ".tmp";
        using (var stream = new FileStream(binTmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new BinaryWriter(stream))
        {
            writer.Write(Magic);
            writer.Write(CurrentFormatVersion);
            writer.Write(song.Notes.Count);

            foreach (var note in song.Notes)
            {
                writer.Write(note.TimeUs);
                writer.Write(note.NoteIndex);
                writer.Write(note.DurationUs);
            }
        }
        File.Move(binTmp, binPath, overwrite: true);

        // 2. Write raw_notes.bin (if raw notes are available)
        if (song.RawNotes != null && song.RawNotes.Count > 0)
        {
            SaveRawNotes(Path.Combine(songDir, "raw_notes.bin"), song.RawNotes);
            if (!string.IsNullOrEmpty(song.SourceHash))
            {
                Midi.MidiRawCache.Save(song.SourceHash, song.RawNotes);
            }
        }

        // Optional and backward compatible: existing songs may not have a selection snapshot.
        string selectedRawPath = Path.Combine(songDir, "selected_raw_notes.bin");
        if (song.SelectedRawNotes is { Count: > 0 })
            SaveRawNotesStrict(selectedRawPath, song.SelectedRawNotes);
        else if (File.Exists(selectedRawPath))
            File.Delete(selectedRawPath);

        // 3. Write meta.json
        var meta = new SongMeta
        {
            Id = song.Id,
            DisplayName = song.DisplayName,
            CreatedAt = song.CreatedAt,
            SourceName = song.SourceName,
            SourceHash = song.SourceHash,
            DurationUs = song.DurationUs,
            NoteCount = song.NoteCount,
            Options = song.Options,
            FormatVersion = song.FormatVersion,
            TargetInstrument = song.TargetInstrument,
            RecognitionModeName = song.RecognitionModeName,
            SelectedSourceKey = song.SelectedSourceKey
        };
        string metaPath = Path.Combine(songDir, "meta.json");
        string metaTmp = metaPath + ".tmp";
        string json = JsonSerializer.Serialize(meta, JsonOptions);
        File.WriteAllText(metaTmp, json);
        File.Move(metaTmp, metaPath, overwrite: true);
    }

    public static ConvertedSong LoadSong(string songDir)
    {
        string metaPath = Path.Combine(songDir, "meta.json");
        string binPath = Path.Combine(songDir, "song.bin");

        if (!File.Exists(metaPath))
            throw new FileNotFoundException("Song metadata file not found", metaPath);
        if (!File.Exists(binPath))
            throw new FileNotFoundException("Song notes binary file not found", binPath);

        string json = File.ReadAllText(metaPath);
        var meta = JsonSerializer.Deserialize<SongMeta>(json, JsonOptions)
                   ?? throw new InvalidDataException("Failed to deserialize song metadata.");

        var notes = new List<GameNoteEvent>();
        using (var stream = new FileStream(binPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        using (var reader = new BinaryReader(stream))
        {
            uint magic = reader.ReadUInt32();
            if (magic != Magic)
                throw new InvalidDataException("Invalid song binary magic header.");

            int version = reader.ReadInt32();
            if (version != 1 && version != 2)
                throw new InvalidDataException($"Unsupported song format version: {version}. Reconversion required.");

            int count = reader.ReadInt32();
            if (count < 0 || count > 5_000_000)
                throw new InvalidDataException($"Unreasonable note count: {count}");

            for (int i = 0; i < count; i++)
            {
                long timeUs = reader.ReadInt64();
                byte noteIndex = reader.ReadByte();
                long durationUs = (version >= 2) ? reader.ReadInt64() : 0;
                notes.Add(new GameNoteEvent(timeUs, noteIndex, durationUs));
            }
        }

        // Try load RawNotes from song directory first, then cache, then source.mid
        string rawPath = Path.Combine(songDir, "raw_notes.bin");
        List<RawMidiNote>? rawNotes = LoadRawNotes(rawPath);

        if ((rawNotes == null || rawNotes.Count == 0 || rawNotes.Any(note => !note.HasMeterPosition)) && !string.IsNullOrEmpty(meta.SourceHash))
        {
            rawNotes = Midi.MidiRawCache.TryGet(meta.SourceHash);
            if (rawNotes != null && rawNotes.Count > 0)
            {
                SaveRawNotes(rawPath, rawNotes);
            }
        }

        if (rawNotes == null || rawNotes.Count == 0 || rawNotes.Any(note => !note.HasMeterPosition))
        {
            string sourceMidPath = Path.Combine(songDir, "source.mid");
            if (File.Exists(sourceMidPath))
            {
                try
                {
                    var (_, loadedNotes, _, _) = Midi.MidiLoader.Load(sourceMidPath);
                    if (rawNotes == null || rawNotes.Count == 0 || loadedNotes.Count > 0)
                    {
                        rawNotes = loadedNotes;
                        SaveRawNotes(rawPath, rawNotes);
                    }
                }
                catch { }
            }
        }

        List<RawMidiNote>? selectedRawNotes = LoadRawNotes(Path.Combine(songDir, "selected_raw_notes.bin"));

        return new ConvertedSong
        {
            Id = meta.Id,
            DisplayName = meta.DisplayName,
            CreatedAt = meta.CreatedAt,
            SourceName = meta.SourceName,
            SourceHash = meta.SourceHash,
            DurationUs = meta.DurationUs,
            Notes = notes,
            RawNotes = rawNotes,
            SelectedRawNotes = selectedRawNotes is { Count: > 0 } ? selectedRawNotes : null,
            Options = meta.Options,
            FormatVersion = meta.FormatVersion,
            TargetInstrument = meta.TargetInstrument,
            RecognitionModeName = meta.RecognitionModeName,
            SelectedSourceKey = meta.SelectedSourceKey
        };
    }

    public static void SaveRawNotes(string filePath, IReadOnlyList<RawMidiNote> notes)
    {
        try
        {
            WriteRawNotes(filePath, notes);
        }
        catch { }
    }

    private static void SaveRawNotesStrict(string filePath, IReadOnlyList<RawMidiNote> notes) => WriteRawNotes(filePath, notes);

    private static void WriteRawNotes(string filePath, IReadOnlyList<RawMidiNote> notes)
    {
        string tmp = filePath + ".tmp";
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(RawMagic);
                writer.Write(4);
                writer.Write(notes.Count);
                foreach (var note in notes)
                {
                    writer.Write(note.StartUs);
                    writer.Write(note.DurationUs);
                    writer.Write(note.NoteNumber);
                    writer.Write(note.Velocity);
                    writer.Write(note.Channel);
                    writer.Write(note.TrackId);
                    writer.Write(note.Program);
                    writer.Write(note.BarIndex);
                    writer.Write(note.BeatInBar);
                    writer.Write(note.BeatsPerBar);
                    writer.Write(note.BeatUnitDenominator);
                    writer.Write(note.IsPrimaryMelody);
                    writer.Write(note.WasOctaveShifted);
                    writer.Write(note.OrigString ?? int.MinValue);
                    writer.Write(note.OrigFret ?? int.MinValue);
                    writer.Write(note.VoiceIndex ?? int.MinValue);
                    writer.Write(note.IsLetRing);
                    writer.Write(note.IsPalmMute);
                    writer.Write(note.IsBrushChord);
                    writer.Write(note.IsBrushUp);
                    writer.Write(note.ExplicitChordName ?? string.Empty);
                    writer.Write(note.SourceFormat ?? string.Empty);
                }
            }
            File.Move(tmp, filePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    public static List<RawMidiNote>? LoadRawNotes(string filePath)
    {
        if (!File.Exists(filePath)) return null;
        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);
            uint magic = reader.ReadUInt32();
            if (magic != RawMagic) return null;
            int version = reader.ReadInt32();
            if (version < 1 || version > 4) return null;
            int count = reader.ReadInt32();
            if (count < 0 || count > 10_000_000) return null;
            var list = new List<RawMidiNote>(count);
            for (int i = 0; i < count; i++)
            {
                var note = new RawMidiNote
                {
                    StartUs = reader.ReadInt64(),
                    DurationUs = reader.ReadInt64(),
                    NoteNumber = reader.ReadByte(),
                    Velocity = reader.ReadByte(),
                    Channel = reader.ReadByte(),
                    TrackId = reader.ReadInt32(),
                    Program = version >= 2 ? reader.ReadInt32() : -1,
                    BarIndex = version >= 3 ? reader.ReadInt64() : 0,
                    BeatInBar = version >= 3 ? reader.ReadDouble() : 0,
                    BeatsPerBar = version >= 3 ? reader.ReadByte() : (byte)4,
                    BeatUnitDenominator = version >= 3 ? reader.ReadByte() : (byte)4,
                    HasMeterPosition = version >= 3
                };
                if (version >= 4)
                {
                    note.IsPrimaryMelody = reader.ReadBoolean();
                    note.WasOctaveShifted = reader.ReadBoolean();
                    int origString = reader.ReadInt32();
                    int origFret = reader.ReadInt32();
                    int voiceIndex = reader.ReadInt32();
                    note.OrigString = origString == int.MinValue ? null : origString;
                    note.OrigFret = origFret == int.MinValue ? null : origFret;
                    note.VoiceIndex = voiceIndex == int.MinValue ? null : voiceIndex;
                    note.IsLetRing = reader.ReadBoolean();
                    note.IsPalmMute = reader.ReadBoolean();
                    note.IsBrushChord = reader.ReadBoolean();
                    note.IsBrushUp = reader.ReadBoolean();
                    note.ExplicitChordName = reader.ReadString();
                    note.SourceFormat = reader.ReadString();
                }
                list.Add(note);
            }
            return list;
        }
        catch
        {
            return null;
        }
    }

    public static SongMeta? LoadMetaOnly(string songDir)
    {
        try
        {
            string metaPath = Path.Combine(songDir, "meta.json");
            if (!File.Exists(metaPath)) return null;

            string json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize<SongMeta>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }
}
