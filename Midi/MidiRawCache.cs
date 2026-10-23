using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Midi;

public static class MidiRawCache
{
    private const uint Magic = 0x4D574152; // 'RAWM'
    public const int CurrentParserVersion = 3;

    public static string CacheDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "NTE-GUITAR", "cache", "raw");

    public static List<RawMidiNote>? TryGet(string sha256)
    {
        try
        {
            string filePath = Path.Combine(CacheDirectory, $"{sha256}.bin");
            if (!File.Exists(filePath))
                return null;

            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var reader = new BinaryReader(stream);

            uint magic = reader.ReadUInt32();
            if (magic != Magic) return null;

            int version = reader.ReadInt32();
            if (version < 1 || version > CurrentParserVersion) return null;

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
                    Program = (version >= 2) ? reader.ReadInt32() : -1,
                    BarIndex = version >= 3 ? reader.ReadInt64() : 0,
                    BeatInBar = version >= 3 ? reader.ReadDouble() : 0,
                    BeatsPerBar = version >= 3 ? reader.ReadByte() : (byte)4,
                    BeatUnitDenominator = version >= 3 ? reader.ReadByte() : (byte)4,
                    HasMeterPosition = version >= 3
                };
                list.Add(note);
            }

            return list;
        }
        catch
        {
            // Cache corrupted or unreadable -> ignore and let caller re-parse
            return null;
        }
    }

    public static void Save(string sha256, IReadOnlyList<RawMidiNote> notes)
    {
        try
        {
            Directory.CreateDirectory(CacheDirectory);
            string targetPath = Path.Combine(CacheDirectory, $"{sha256}.bin");
            string tmpPath = targetPath + ".tmp";

            using (var stream = new FileStream(tmpPath, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(CurrentParserVersion);
                writer.Write(notes.Count);

                for (int i = 0; i < notes.Count; i++)
                {
                    var note = notes[i];
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
                }
            }

            File.Move(tmpPath, targetPath, overwrite: true);
        }
        catch
        {
            // Cache write failure shouldn't crash the app
        }
    }
}
