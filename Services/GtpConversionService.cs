using System.Security.Cryptography;
using System.Text.Json;
using MidiAutoPlayer.Gtp;
using MidiAutoPlayer.Midi;
using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Services;

public sealed record GtpTrackInfo(int Id, string Name, int NoteCount, long DurationUs);
public sealed record GtpPickedFile(string FilePath, string FileName);
public sealed record GtpParseInfo(string FilePath, string FileName, string Sha256, IReadOnlyList<GtpTrackInfo> Tracks, string Title);
public sealed record GtpPreviewNote(long StartUs, long DurationUs, byte NoteNumber, byte Velocity, byte Channel, int TrackId, int Program, int? VoiceIndex = null, int? OrigString = null, int? OrigFret = null);
public sealed record GtpConversionInfo(string Id, string Name, string Target, int NoteCount, long DurationUs, IReadOnlyList<GtpPreviewNote> PreviewNotes);
public sealed record GtpPreviewResult(IReadOnlyList<GtpPreviewNote> PreviewNotes);
public sealed record GtpLibraryItem(string Id, string Name, string Target, int NoteCount, long DurationUs, DateTime CreatedAt, string SourceName);

/// <summary>Imports Guitar Pro files and stores converted songs in an isolated JSON shelf.</summary>
public sealed class GtpConversionService(IWin32Window? owner = null)
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    private static readonly object ShelfLock = new();
    private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "NTE-GUITAR", "gtp-library");
    private readonly IWin32Window? _owner = owner;

    public GtpPickedFile? PickFile()
    {
        using var dialog = new OpenFileDialog
        {
            Title = "选择 Guitar Pro 谱面",
            Filter = "Guitar Pro 谱面 (*.gp;*.gp3;*.gp4;*.gp5;*.gpx)|*.gp;*.gp3;*.gp4;*.gp5;*.gpx|所有文件 (*.*)|*.*",
            CheckFileExists = true,
            Multiselect = false
        };
        return dialog.ShowDialog(_owner) == DialogResult.OK
            ? new GtpPickedFile(Path.GetFullPath(dialog.FileName), Path.GetFileName(dialog.FileName))
            : null;
    }

    public GtpParseInfo Parse(string path)
    {
        var (fullPath, bytes) = ReadSource(path);
        string hash = System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        GtpParsedScore parsed = GtpImporter.Load(bytes);
        var tracks = parsed.Tracks.Select(t => new GtpTrackInfo(t.Id, t.Name, t.Notes.Count,
            t.Notes.Select(n => n.StartUs + n.DurationUs).DefaultIfEmpty(0).Max())).ToList();
        string title = string.IsNullOrWhiteSpace(parsed.Title) || parsed.Title.Contains('\uFFFD')
            ? Path.GetFileNameWithoutExtension(fullPath)
            : parsed.Title;
        return new GtpParseInfo(fullPath, Path.GetFileName(fullPath), hash, tracks, title);
    }

    public GtpConversionInfo Convert(string path, string name, IReadOnlyList<int> trackIds, string target, string selection, int transpose)
    {
        var (fullPath, bytes) = ReadSource(path);
        string hash = System.Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var parsed = GtpImporter.Load(bytes);
        string normalizedTarget = NormalizeTarget(target);
        var selectedTracks = parsed.Tracks.Where(t => trackIds.Contains(t.Id) && !t.IsPercussion).ToList();
        if (selectedTracks.Count == 0) throw new InvalidOperationException("请选择至少一条包含音符的轨道。");
        var selected = SelectNotes(selectedTracks, selection).Where(n => !n.IsDead).Select(CloneRichNote).ToList();
        if (selected.Count == 0) throw new InvalidOperationException("所选轨道和声部中没有可转换的有效音符。");
        if (normalizedTarget is "keyboard36" or "piano") NormalizeBrushesForKeyboard(selected);
        foreach (var note in selected)
        {
            int pitch = Math.Clamp(note.NoteNumber + transpose, 0, 127);
            note.NoteNumber = (byte)pitch;
            EvaluateGameGuitarPosition(note);
        }
        selected.Sort((a, b) => a.StartUs != b.StartUs ? a.StartUs.CompareTo(b.StartUs) : a.NoteNumber.CompareTo(b.NoteNumber));
        var raw = selected.Select(n => n.ToRaw()).ToList();
        int targetInstrument = normalizedTarget switch
        {
            "guitar" => LibraryInstrument.Guitar,
            "piano" => LibraryInstrument.GamePiano,
            _ => LibraryInstrument.Keyboard
        };
        var options = new ConvertOptions
        {
            Transpose = 0,
            IgnoreDrumChannel = false,
            OutOfRangeMode = "OctaveFold",
            TargetInstrument = targetInstrument,
            RecognitionModeName = "ManualTracks",
            EnabledTracks = selectedTracks.Select(t => t.Id).ToList()
        };
        string displayName = string.IsNullOrWhiteSpace(name) ? Path.GetFileNameWithoutExtension(fullPath) : name.Trim();
        ConvertedSong song;
        if (normalizedTarget == "piano")
        {
            song = new ConvertedSong
            {
                Id = Guid.NewGuid().ToString("N"), DisplayName = displayName, CreatedAt = DateTime.Now,
                SourceName = Path.GetFileName(fullPath), SourceHash = hash,
                DurationUs = raw.Select(n => n.StartUs + n.DurationUs).DefaultIfEmpty(0).Max(),
                RawNotes = raw, SelectedRawNotes = raw.ToList(), Options = options,
                TargetInstrument = LibraryInstrument.GamePiano, RecognitionModeName = "ManualTracks", SourceFormat = "GTP"
            };
        }
        else
        {
            song = MidiConverter.Convert(raw, Path.GetFileName(fullPath), hash, displayName, options);
            song.TargetInstrument = targetInstrument;
            song.SelectedRawNotes = raw.ToList();
            song.RawNotes = raw;
            song.SourceFormat = "GTP";
        }
        Save(song, normalizedTarget, selected);
        return ToDto(song, normalizedTarget, selected);
    }

    public IReadOnlyList<GtpLibraryItem> List()
    {
        lock (ShelfLock)
        {
            var result = new List<GtpLibraryItem>();
            foreach (string file in Directory.Exists(_root) ? Directory.EnumerateFiles(_root, "*.json", SearchOption.TopDirectoryOnly) : Array.Empty<string>())
            {
                try
                {
                    var entry = JsonSerializer.Deserialize<ShelfEntry>(File.ReadAllText(file), JsonOptions);
                    if (entry?.Song != null) result.Add(Summarize(entry.Song, entry.Target));
                }
                catch { /* A damaged shelf item does not hide other items. */ }
            }
            return result.OrderByDescending(x => x.CreatedAt).ToList();
        }
    }

    public ConvertedSong Load(string id) => ReadEntry(id).Song;

    public GtpPreviewResult Preview(string id)
    {
        var entry = ReadEntry(id);
        var song = entry.Song;
        if (entry.RichNotes is { Count: > 0 }) return new GtpPreviewResult(entry.RichNotes.Select(n => new GtpPreviewNote(n.StartUs, n.DurationUs, n.NoteNumber, n.Velocity, n.Channel, n.TrackId, n.Program, n.VoiceIndex, n.OrigString, n.OrigFret)).ToList());
        var raw = song.SelectedRawNotes ?? song.RawNotes ?? new List<RawMidiNote>();
        return new GtpPreviewResult(raw.Select(n => new GtpPreviewNote(n.StartUs, n.DurationUs, n.NoteNumber, n.Velocity, n.Channel, n.TrackId, n.Program)).ToList());
    }

    public bool Delete(string id)
    {
        string file = ResolveItemPath(id);
        lock (ShelfLock)
        {
            if (!File.Exists(file)) return false;
            File.Delete(file);
            return true;
        }
    }

    private void Save(ConvertedSong song, string target, IReadOnlyList<GtpRichNote> richNotes)
    {
        lock (ShelfLock)
        {
            Directory.CreateDirectory(_root);
            string file = ResolveItemPath(song.Id);
            string tmp = file + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(new ShelfEntry { Target = target, Song = song, RichNotes = richNotes.ToList() }, JsonOptions));
            File.Move(tmp, file, overwrite: true);
        }
    }

    private ShelfEntry ReadEntry(string id)
    {
        string file = ResolveItemPath(id);
        lock (ShelfLock)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("GTP 转换库中不存在该曲目。", id);
            return JsonSerializer.Deserialize<ShelfEntry>(File.ReadAllText(file), JsonOptions)
                ?? throw new InvalidDataException("GTP 转换库文件内容无效。");
        }
    }

    private string ResolveItemPath(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || id.Contains('/') || id.Contains('\\'))
            throw new ArgumentException("无效的 GTP 曲目 ID。", nameof(id));
        return Path.Combine(_root, id + ".json");
    }

    private static (string FullPath, byte[] Bytes) ReadSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("请指定 Guitar Pro 文件路径。", nameof(path));
        string fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)) throw new FileNotFoundException("找不到 Guitar Pro 文件。", fullPath);
        string ext = Path.GetExtension(fullPath);
        if (!new[] { ".gp", ".gp3", ".gp4", ".gp5", ".gpx" }.Contains(ext, StringComparer.OrdinalIgnoreCase))
            throw new NotSupportedException($"不支持该文件类型“{ext}”。请选择 .gp、.gp3、.gp4、.gp5 或 .gpx 文件。");
        var info = new FileInfo(fullPath);
        if (info.Length <= 0 || info.Length > 100 * 1024 * 1024) throw new InvalidDataException("谱面文件为空或超过 100 MB，无法导入。");
        return (fullPath, File.ReadAllBytes(fullPath));
    }

    private static string NormalizeTarget(string target) => target?.Trim().ToLowerInvariant() switch
    {
        "keyboard" or "keyboard36" => "keyboard36", "guitar" => "guitar", "piano" => "piano",
        _ => throw new ArgumentException("target 必须是 keyboard36、guitar 或 piano。", nameof(target))
    };

    private static List<GtpRichNote> SelectNotes(IReadOnlyList<GtpTrackSource> tracks, string? selection)
    {
        string mode = (selection ?? "all").Trim().ToLowerInvariant();
        var all = tracks.SelectMany(t => t.Notes).ToList();
        if (mode is "all" or "full" or "fullscore" or "manualtracks" or "") return all;
        if (mode is "melody" or "lead" or "voice" or "topvoice")
        {
            return all.GroupBy(n => n.TrackId).SelectMany(group => ExtractGtpSingleTrackMelody(group.ToList())).ToList();
        }
        if (mode.StartsWith("voice:", StringComparison.Ordinal) && int.TryParse(mode.AsSpan(6), out int voiceIndex))
            return all.Where(n => n.VoiceIndex == voiceIndex || n.VoiceIndex == voiceIndex - 1).ToList();
        if (mode is "stringmelody" or "string-melody")
        {
            int stringNo = all.Where(n => n.OrigString.HasValue).GroupBy(n => n.OrigString!.Value).OrderByDescending(g => g.Average(n => n.NoteNumber)).FirstOrDefault()?.Key ?? 1;
            return all.Where(n => n.OrigString == stringNo).ToList();
        }
        throw new ArgumentException("selection 支持 all、melody、voice[:n] 或 stringmelody。", nameof(selection));
    }

    private static GtpRichNote CloneRichNote(GtpRichNote n) => new()
    {
        StartUs = n.StartUs, DurationUs = n.DurationUs, NoteNumber = n.NoteNumber, Velocity = n.Velocity,
        Channel = n.Channel, TrackId = n.TrackId, Program = n.Program, VoiceIndex = n.VoiceIndex,
        OrigString = n.OrigString, OrigFret = n.OrigFret, IsDead = n.IsDead, IsGrace = n.IsGrace,
        IsLetRing = n.IsLetRing, IsHarmonic = n.IsHarmonic, IsTied = n.IsTied, HasBend = n.HasBend,
        Brush = n.Brush, IsBrushChord = n.IsBrushChord, IsBrushUp = n.IsBrushUp,
        IsPalmMute = n.IsPalmMute, ExplicitChordName = n.ExplicitChordName,
        GameString = n.GameString, GameFret = n.GameFret, GamePage = n.GamePage, GameSlot = n.GameSlot
    };

    private static List<GtpRichNote> ExtractGtpSingleTrackMelody(IReadOnlyList<GtpRichNote> notes)
    {
        bool hasDualVoices = notes.Any(n => n.VoiceIndex == 0) && notes.Any(n => n.VoiceIndex == 1);
        var clusters = new List<List<GtpRichNote>>();
        foreach (var note in notes.OrderBy(n => n.StartUs))
        {
            if (clusters.Count == 0 || note.StartUs - clusters[^1][0].StartUs > 30_000)
                clusters.Add(new List<GtpRichNote> { note });
            else clusters[^1].Add(note);
        }

        var melody = new List<GtpRichNote>();
        GtpRichNote? active = null;
        foreach (var cluster in clusters)
        {
            long clusterTime = cluster[0].StartUs;
            bool sustaining = active != null && clusterTime < active.StartUs + Math.Min(active.DurationUs, 1_200_000) - 25_000;
            if (hasDualVoices)
            {
                var lead = cluster.Where(n => n.VoiceIndex == 0).OrderByDescending(n => n.NoteNumber).FirstOrDefault();
                if (lead != null) { melody.Add(lead); active = lead; }
                continue;
            }
            var ranked = cluster.Select(n => (Note: n, Score: ScoreMelodyCandidate(n, cluster, active, sustaining)))
                .OrderByDescending(x => x.Score).First();
            if (ranked.Score > 0) { melody.Add(ranked.Note); active = ranked.Note; }
        }
        return melody;
    }

    private static double ScoreMelodyCandidate(GtpRichNote note, IReadOnlyList<GtpRichNote> cluster, GtpRichNote? active, bool sustaining)
    {
        double score = 10;
        int maxPitch = cluster.Max(n => n.NoteNumber);
        score += note.NoteNumber == maxPitch ? 8 : -(maxPitch - note.NoteNumber) * 1.8;
        score += note.OrigString switch { 1 => 9, 2 => 6.5, 3 => 3, 4 => -1.5, 5 => -7, 6 => -10, _ => 0 };
        if (note.IsPalmMute) score -= 12;
        if (sustaining && active != null && active.NoteNumber - note.NoteNumber >= 5 && note.OrigString is >= 4) score -= 25;
        if (active != null)
        {
            int interval = Math.Abs(note.NoteNumber - active.NoteNumber);
            if (interval <= 5) score += 4.5;
            else if (interval > 12 && note.NoteNumber < active.NoteNumber) score -= 9;
        }
        return score;
    }

    private static void NormalizeBrushesForKeyboard(List<GtpRichNote> notes)
    {
        foreach (var trackGroup in notes.Where(n => n.IsBrushChord).GroupBy(n => (n.TrackId, n.VoiceIndex)))
        {
            var ordered = trackGroup.OrderBy(n => n.StartUs).ToList();
            for (int i = 0; i < ordered.Count;)
            {
                int end = i + 1;
                long anchor = ordered[i].StartUs;
                while (end < ordered.Count && ordered[end].StartUs - anchor <= 100_000) end++;
                var cluster = ordered.GetRange(i, end - i);
                if (cluster.Count > 1)
                {
                    foreach (var note in cluster) note.StartUs = anchor;
                    var kept = cluster.OrderByDescending(n => n.NoteNumber).Take(1)
                        .Concat(cluster.OrderBy(n => n.NoteNumber).Take(1))
                        .Concat(cluster.OrderBy(n => Math.Abs(n.NoteNumber - cluster.Average(x => x.NoteNumber))).Take(2))
                        .Distinct().ToHashSet();
                    notes.RemoveAll(n => cluster.Contains(n) && !kept.Contains(n));
                }
                i = end;
            }
        }
    }

    private static void EvaluateGameGuitarPosition(GtpRichNote note)
    {
        int[] open = [64, 59, 55, 50, 45, 40];
        var candidates = open.Select((pitch, i) => new { String = i + 1, Fret = (int)note.NoteNumber - pitch })
            .Where(x => x.Fret is >= 0 and <= 24)
            .OrderBy(x => x.Fret == 0 ? 0 : (x.Fret - 1) / 6 + 1).ThenBy(x => x.Fret).ThenBy(x => x.String).ToList();
        if (candidates.Count == 0) { note.GameString = null; note.GameFret = null; note.GamePage = null; note.GameSlot = null; return; }
        var best = candidates[0];
        note.GameString = best.String;
        note.GameFret = best.Fret;
        note.GamePage = best.Fret == 0 ? 0 : ((best.Fret - 1) / 6) + 1;
        note.GameSlot = best.Fret == 0 ? 1 : ((best.Fret - 1) % 6) + 1;
    }

    private static GtpConversionInfo ToDto(ConvertedSong song, string target, IReadOnlyList<GtpRichNote> rich) => new(
        song.Id, song.DisplayName, target, song.NoteCount, song.DurationUs,
        rich.Select(n => new GtpPreviewNote(n.StartUs, n.DurationUs, n.NoteNumber, n.Velocity, n.Channel, n.TrackId, n.Program, n.VoiceIndex, n.OrigString, n.OrigFret)).ToList());

    private static GtpLibraryItem Summarize(ConvertedSong song, string target) => new(song.Id, song.DisplayName, target, song.NoteCount, song.DurationUs, song.CreatedAt, song.SourceName);
    private sealed class ShelfEntry { public string Target { get; set; } = "keyboard36"; public ConvertedSong Song { get; set; } = new(); public List<GtpRichNote>? RichNotes { get; set; } }
}
