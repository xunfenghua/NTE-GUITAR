using MidiAutoPlayer.Models;
using System.Security.Cryptography;

namespace MidiAutoPlayer.Gtp;

/// <summary>A note as imported from a Guitar Pro score, retaining tab and articulation data.</summary>
public sealed class GtpRichNote
{
    public long StartUs { get; set; }
    public long DurationUs { get; set; }
    public byte NoteNumber { get; set; }
    public byte Velocity { get; set; } = 90;
    public byte Channel { get; set; }
    public int TrackId { get; set; }
    public int Program { get; set; } = -1;
    public int VoiceIndex { get; set; }
    public int? OrigString { get; set; }
    public int? OrigFret { get; set; }
    public bool IsDead { get; set; }
    public bool IsGrace { get; set; }
    public bool IsLetRing { get; set; }
    public bool IsHarmonic { get; set; }
    public bool IsTied { get; set; }
    public bool HasBend { get; set; }
    public string Brush { get; set; } = "none";
    public bool IsBrushChord { get; set; }
    public bool IsBrushUp { get; set; }
    public bool IsPalmMute { get; set; }
    public string? ExplicitChordName { get; set; }
    public int? GameString { get; set; }
    public int? GameFret { get; set; }
    public int? GamePage { get; set; }
    public int? GameSlot { get; set; }

    public RawMidiNote ToRaw() => new()
    {
        StartUs = StartUs, DurationUs = DurationUs, NoteNumber = NoteNumber,
        Velocity = Velocity, Channel = Channel, TrackId = TrackId, Program = Program,
        OrigString = GameString ?? OrigString, OrigFret = GameFret ?? OrigFret,
        VoiceIndex = VoiceIndex, IsLetRing = IsLetRing, IsPalmMute = IsPalmMute,
        IsBrushChord = IsBrushChord, IsBrushUp = IsBrushUp, ExplicitChordName = ExplicitChordName,
        SourceFormat = "GTP"
    };
}

public sealed class GtpTrackSource
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public int Program { get; set; } = -1;
    public bool IsPercussion { get; set; }
    public List<int> Tuning { get; set; } = new();
    public int Capo { get; set; }
    public List<GtpBeat> Beats { get; set; } = new();
    public List<GtpRichNote> Notes { get; set; } = new();
}

public sealed class GtpBeat
{
    public int TrackId { get; set; }
    public int MeasureIndex { get; set; }
    public int VoiceIndex { get; set; }
    public long Tick { get; set; }
    public long DurationTicks { get; set; }
    public string Brush { get; set; } = "none";
    public bool IsGrace { get; set; }
    public bool IsLetRing { get; set; }
    public List<GtpAtom> Atoms { get; set; } = new();
}

public sealed class GtpAtom
{
    public int NoteNumber { get; set; }
    public int? String { get; set; }
    public int? Fret { get; set; }
    public bool IsDead { get; set; }
    public bool IsHarmonic { get; set; }
    public bool IsTied { get; set; }
    public bool HasBend { get; set; }
}

public sealed class GtpParsedScore
{
    public string Title { get; set; } = string.Empty;
    public int TicksPerQuarter { get; set; } = 960;
    public List<GtpTrackSource> Tracks { get; set; } = new();
    public List<long> MeasureTicks { get; set; } = new();
    public List<double> TemposBpm { get; set; } = new();
    public List<GtpTempoPoint> TempoMap { get; set; } = new();
    public List<int> PlaybackMeasureOrder { get; set; } = new();
}

public sealed record GtpTempoPoint(long Tick, double Bpm);

/// <summary>AlphaTab-backed Guitar Pro importer and event normalization pipeline.</summary>
public static class GtpImporter
{
    private static readonly object CacheLock = new();
    private static readonly Dictionary<string, GtpParsedScore> ParseCache = new(StringComparer.Ordinal);
    private static readonly Queue<string> CacheOrder = new();

    public static GtpParsedScore Load(byte[] bytes)
    {
        if (bytes is not { Length: > 0 }) throw new InvalidDataException("Guitar Pro 文件为空。");
        string hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        lock (CacheLock)
        {
            if (ParseCache.TryGetValue(hash, out var cached)) return cached;
        }
        var parsed = LoadUncached(bytes);
        lock (CacheLock)
        {
            if (ParseCache.TryGetValue(hash, out var cached)) return cached;
            ParseCache[hash] = parsed;
            CacheOrder.Enqueue(hash);
            while (CacheOrder.Count > 8) ParseCache.Remove(CacheOrder.Dequeue());
        }
        return parsed;
    }

    private static GtpParsedScore LoadUncached(byte[] bytes)
    {
        if (bytes is not { Length: > 0 }) throw new InvalidDataException("Guitar Pro 文件为空。");
        object score;
        try
        {
            score = AlphaTab.Importer.ScoreLoader.LoadScoreFromBytes(bytes);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException("无法读取该 Guitar Pro 文件。请确认文件格式受当前 AlphaTab 解析器支持。", ex);
        }

        var result = new GtpParsedScore
        {
            Title = Text(score, "Title", "Name") ?? string.Empty,
            TicksPerQuarter = (int)Math.Clamp(Number(score, 960, "Division", "TicksPerQuarter", "Ppqn"), 1, int.MaxValue)
        };
        var masterBars = Items(Get(score, "MasterBars", "Bars")).ToList();
        var tracks = Items(Get(score, "Tracks")).ToList();
        if (tracks.Count == 0) throw new InvalidDataException("乐谱中没有可转换的轨道。");

        double currentTempo = NumberDouble(Get(score, "Tempo"), 120);
        if (currentTempo <= 0) currentTempo = 120;
        result.TempoMap.Add(new GtpTempoPoint(0, currentTempo));
        for (int i = 0; i < masterBars.Count; i++)
        {
            long barStart = Number(masterBars[i], i == 0 ? 0 : result.MeasureTicks[^1] + result.TicksPerQuarter * 4L, "Start", "StartTick");
            result.MeasureTicks.Add(barStart);
            long barEnd = i + 1 < masterBars.Count
                ? Number(masterBars[i + 1], barStart + result.TicksPerQuarter * 4L, "Start", "StartTick")
                : barStart + result.TicksPerQuarter * 4L;
            foreach (var automation in Items(Get(masterBars[i], "TempoAutomations"))
                         .Where(a => string.Equals(Text(a, "Type"), "Tempo", StringComparison.OrdinalIgnoreCase))
                         .OrderBy(a => NumberDouble(Get(a, "RatioPosition"), 0)))
            {
                double bpm = NumberDouble(Get(automation, "Value"), currentTempo);
                if (bpm <= 0) continue;
                double ratio = Math.Clamp(NumberDouble(Get(automation, "RatioPosition"), 0), 0, 1);
                long tick = barStart + (long)Math.Round((barEnd - barStart) * ratio);
                result.TempoMap.Add(new GtpTempoPoint(tick, bpm));
                if (ratio <= 0) currentTempo = bpm;
            }
            result.TemposBpm.Add(currentTempo);
        }
        if (masterBars.Count == 0) { result.MeasureTicks.Add(0); result.TemposBpm.Add(currentTempo); }
        result.PlaybackMeasureOrder = ExpandMeasureOrder(masterBars);

        for (int ti = 0; ti < tracks.Count; ti++)
        {
            object track = tracks[ti];
            var staff = Items(Get(track, "Staves", "Staff")).FirstOrDefault();
            if (staff == null) continue;
            var source = new GtpTrackSource
            {
                Id = ti,
                Name = ValidText(Text(track, "Name", "ShortName")) ?? $"Track {ti + 1}",
                Program = (int)Number(track, -1, "PlaybackInfo.Program", "Program", "Patch"),
                IsPercussion = Bool(track, "IsPercussion", "PlaybackInfo.IsPercussion"),
                Capo = (int)Number(staff, 0, "Capo"),
                Tuning = Items(Get(staff, "Tuning.Tunings", "Tuning", "Tunings")).Select(x => (int)Number(x, 0)).ToList()
            };
            var bars = Items(Get(staff, "Bars")).ToList();
            for (int bi = 0; bi < bars.Count; bi++)
            {
                object bar = bars[bi];
                var voices = Items(Get(bar, "Voices")).ToList();
                for (int vi = 0; vi < voices.Count; vi++)
                {
                    var beats = Items(Get(voices[vi], "Beats")).ToList();
                    foreach (var beatObj in beats)
                    {
                        // AlphaTab's PlaybackStart is relative to the current bar, while
                        // MeasureTicks stores the absolute score position of that bar.
                        long barStartTick = result.MeasureTicks[Math.Min(bi, result.MeasureTicks.Count - 1)];
                        long beatOffsetTick = Number(beatObj, 0, "PlaybackStart", "DisplayStart");
                        long tick = checked(barStartTick + beatOffsetTick);
                        long dur = Number(beatObj, result.TicksPerQuarter, "PlaybackDuration", "DurationTicks");
                        bool grace = Get(beatObj, "GraceType") is { } gt && !gt.ToString()!.Equals("None", StringComparison.OrdinalIgnoreCase);
                        bool letRing = Bool(beatObj, "IsLetRing");
                        string brush = Text(beatObj, "BrushType", "Brush") ?? "none";
                        string chord = Text(beatObj, "Chord.Name", "ChordName") ?? "";
                        bool brushChord = !string.IsNullOrWhiteSpace(brush) && !brush.Equals("none", StringComparison.OrdinalIgnoreCase);
                        var beat = new GtpBeat { TrackId = ti, MeasureIndex = bi, VoiceIndex = vi, Tick = tick, DurationTicks = dur, IsGrace = grace, IsLetRing = letRing, Brush = brush };
                        foreach (var noteObj in Items(Get(beatObj, "Notes")))
                        {
                            bool dead = Bool(noteObj, "IsDead");
                            int? alphaString = NullableInt(noteObj, "String");
                            // AlphaTab uses 1 for the lowest/thickest string; the app exposes 1 as high E.
                            int? stringNo = alphaString.HasValue && source.Tuning.Count > 0
                                ? source.Tuning.Count - alphaString.Value + 1
                                : alphaString;
                            int? fret = NullableInt(noteObj, "Fret");
                            int pitch = (int)Number(noteObj, -1, "RealValue", "Pitch", "Value");
                            if (InvokeRealValue(noteObj) is { } real && real is >= 0 and <= 127) pitch = (int)Math.Round(real);
                            if (pitch < 0 && fret.HasValue && alphaString is > 0 && alphaString <= source.Tuning.Count)
                                pitch = source.Tuning[alphaString.Value - 1] + fret.Value + source.Capo;
                            if (pitch < 0 || pitch > 127) continue;
                            bool harmonic = Get(noteObj, "HarmonicType") is { } ht && !ht.ToString()!.Equals("None", StringComparison.OrdinalIgnoreCase);
                            bool tied = Bool(noteObj, "IsTieDestination", "IsTied");
                            bool bend = Items(Get(noteObj, "BendPoints")).Any();
                            bool let = letRing || Bool(noteObj, "IsLetRing");
                            beat.Atoms.Add(new GtpAtom { NoteNumber = pitch, String = stringNo, Fret = fret, IsDead = dead, IsHarmonic = harmonic, IsTied = tied, HasBend = bend });
                            source.Notes.Add(new GtpRichNote
                            {
                                StartUs = 0, DurationUs = 0, NoteNumber = (byte)pitch,
                                Velocity = (byte)Math.Clamp(Number(beatObj, 90, "Dynamic", "Velocity"), 1, 127),
                                TrackId = ti, Program = source.Program, VoiceIndex = vi, OrigString = stringNo, OrigFret = fret,
                                IsDead = dead, IsGrace = grace, IsLetRing = let, IsHarmonic = harmonic, IsTied = tied, HasBend = bend, Brush = brush,
                                IsBrushChord = brushChord, IsBrushUp = brush.Contains("up", StringComparison.OrdinalIgnoreCase),
                                IsPalmMute = Bool(noteObj, "IsPalmMute", "IsPalmMuted"), ExplicitChordName = chord
                            });
                        }
                        source.Beats.Add(beat);
                        // ticks remain in AlphaTab's absolute score timeline; repeat playback is expanded below.
                        foreach (var note in source.Notes.TakeLast(beat.Atoms.Count))
                        {
                            note.StartUs = tick;
                            note.DurationUs = dur;
                        }
                    }
                }
            }
            source.Notes = ConvertTicksAndUnroll(source.Notes, result);
            source.Beats = source.Beats.OrderBy(b => b.Tick).ThenBy(b => b.VoiceIndex).ToList();
            result.Tracks.Add(source);
        }
        if (result.Tracks.Count == 0) throw new InvalidDataException("乐谱中没有可读取的谱表。");
        return result;
    }

    private static List<GtpRichNote> ConvertTicksAndUnroll(List<GtpRichNote> source, GtpParsedScore score)
    {
        long ToUs(long tick)
        {
            double us = 0;
            double bpm = score.TempoMap.Count > 0 && score.TempoMap[0].Bpm > 0 ? score.TempoMap[0].Bpm : 120;
            long cursor = 0;
            foreach (var change in score.TempoMap.OrderBy(point => point.Tick))
            {
                if (change.Tick > tick) break;
                if (change.Tick > cursor)
                {
                    us += (change.Tick - cursor) * (60_000_000d / (bpm * score.TicksPerQuarter));
                    cursor = change.Tick;
                }
                if (change.Bpm > 0) bpm = change.Bpm;
            }
            if (tick > cursor) us += (tick - cursor) * (60_000_000d / (bpm * score.TicksPerQuarter));
            return (long)Math.Round(us);
        }
        var output = new List<GtpRichNote>();
        var bars = score.MeasureTicks;
        var order = score.PlaybackMeasureOrder.Count > 0 ? score.PlaybackMeasureOrder : Enumerable.Range(0, bars.Count).ToList();
        long timeline = 0;
        for (int playIndex = 0; playIndex < order.Count; playIndex++)
        {
            int measure = order[playIndex];
            if (measure < 0 || measure >= bars.Count) continue;
            long startTick = bars[measure];
            long endTick = measure + 1 < bars.Count ? bars[measure + 1] : source.Where(n => n.StartUs >= startTick).Select(n => n.StartUs + Math.Max(1, n.DurationUs)).DefaultIfEmpty(startTick + score.TicksPerQuarter * 4L).Max();
            long measureUs = Math.Max(1, ToUs(endTick) - ToUs(startTick));
            foreach (var n in source.Where(n => n.StartUs >= startTick && n.StartUs < endTick))
            {
                if (n.IsDead) continue;
                long rawStart = n.StartUs;
                long duration = n.DurationUs;
                if (n.IsGrace) { rawStart = Math.Max(startTick, rawStart - score.TicksPerQuarter / 4); duration = Math.Max(1, duration - score.TicksPerQuarter / 4); }
                long relUs = Math.Max(0, ToUs(rawStart) - ToUs(startTick));
                long durUs = Math.Max(1, ToUs(rawStart + duration) - ToUs(rawStart));
                long brushOffsetUs = 0;
                if (!string.IsNullOrWhiteSpace(n.Brush) && !n.Brush.Equals("none", StringComparison.OrdinalIgnoreCase))
                {
                    var sameBrush = source.Where(x => x.TrackId == n.TrackId && x.VoiceIndex == n.VoiceIndex && x.StartUs == n.StartUs &&
                        x.Brush == n.Brush && !x.IsDead).OrderBy(x => x.NoteNumber).ToList();
                    int index = sameBrush.FindIndex(x => x.NoteNumber == n.NoteNumber && x.OrigString == n.OrigString);
                    if (n.Brush.Contains("down", StringComparison.OrdinalIgnoreCase)) index = sameBrush.Count - 1 - index;
                    if (index > 0) brushOffsetUs = index * 8_000L;
                }
                output.Add(Clone(n, timeline + relUs + brushOffsetUs, durUs));
            }
            timeline += measureUs;
        }
        output.Sort((a, b) => a.StartUs.CompareTo(b.StartUs));
        // Merge tied continuation notes on equal pitch and string.
        var merged = new List<GtpRichNote>();
        foreach (var note in output)
        {
            var previous = merged.LastOrDefault(n => n.NoteNumber == note.NoteNumber && n.OrigString == note.OrigString && n.StartUs + n.DurationUs >= note.StartUs - 2_000);
            if (note.IsTied && previous != null) previous.DurationUs = Math.Max(previous.DurationUs, note.StartUs + note.DurationUs - previous.StartUs);
            else merged.Add(note);
        }
        // let-ring extends only through the following same-string onset, capped at 4 seconds.
        foreach (var note in merged.Where(n => n.IsLetRing))
        {
            long next = merged.Where(n => n.TrackId == note.TrackId && n.OrigString == note.OrigString && n.StartUs > note.StartUs).Select(n => n.StartUs).DefaultIfEmpty(note.StartUs + 4_000_000).Min();
            note.DurationUs = Math.Max(note.DurationUs, Math.Min(next, note.StartUs + 4_000_000) - note.StartUs);
        }
        return merged;
    }

    private static List<int> ExpandMeasureOrder(IReadOnlyList<object> bars)
    {
        var order = new List<int>();
        var repeatStart = 0;
        var repeatPasses = new Dictionary<int, int>();
        var i = 0;
        int guard = Math.Max(1, bars.Count * 16);
        while (i < bars.Count && order.Count < guard)
        {
            object bar = bars[i];
            if (Bool(bar, "IsRepeatStart", "RepeatStart")) { repeatStart = i; if (!repeatPasses.ContainsKey(i)) repeatPasses[i] = 0; }
            int endings = (int)Number(bar, 0, "AlternateEndings");
            int pass = repeatPasses.TryGetValue(repeatStart, out var p) ? p : 0;
            if (endings != 0 && pass < 31 && (endings & (1 << pass)) == 0) { i++; continue; }
            order.Add(i);
            if (Bool(bar, "IsRepeatEnd", "RepeatEnd"))
            {
                int count = (int)Math.Clamp(Number(bar, 2, "RepeatCount", "RepeatCountValue"), 1, 8);
                if (pass + 1 < count) { repeatPasses[repeatStart] = pass + 1; i = repeatStart; continue; }
                repeatPasses.Remove(repeatStart);
                repeatStart = i + 1;
            }
            i++;
        }
        return order.Count == 0 ? Enumerable.Range(0, bars.Count).ToList() : order;
    }

    private static GtpRichNote Clone(GtpRichNote n, long start, long duration) => new()
    {
        StartUs = start, DurationUs = duration, NoteNumber = n.NoteNumber, Velocity = n.Velocity, Channel = n.Channel,
        TrackId = n.TrackId, Program = n.Program, VoiceIndex = n.VoiceIndex, OrigString = n.OrigString, OrigFret = n.OrigFret,
        IsDead = n.IsDead, IsGrace = n.IsGrace, IsLetRing = n.IsLetRing, IsHarmonic = n.IsHarmonic, IsTied = n.IsTied,
        HasBend = n.HasBend, Brush = n.Brush, IsBrushChord = n.IsBrushChord, IsBrushUp = n.IsBrushUp,
        IsPalmMute = n.IsPalmMute, ExplicitChordName = n.ExplicitChordName,
        GameString = n.GameString, GameFret = n.GameFret, GamePage = n.GamePage, GameSlot = n.GameSlot
    };

    private static object? Get(object? value, params string[] names)
    {
        if (value == null) return null;
        foreach (string name in names)
        {
            object? cur = value;
            foreach (var part in name.Split('.'))
            {
                if (cur == null) break;
                var p = cur.GetType().GetProperty(part, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.IgnoreCase);
                cur = p?.GetValue(cur);
            }
            if (cur != null) return cur;
        }
        return null;
    }
    private static IEnumerable<object> Items(object? v) => v is System.Collections.IEnumerable e && v is not string ? e.Cast<object>() : Enumerable.Empty<object>();
    private static long Number(object? o, long fallback, params string[] p) { var v = p.Length == 0 ? o : Get(o, p); try { return v == null ? fallback : Convert.ToInt64(Get(v, "Value") ?? v); } catch { return fallback; } }
    private static double NumberDouble(object? v, double fallback) { try { return v == null ? fallback : Convert.ToDouble(v); } catch { return fallback; } }
    private static bool Bool(object? o, params string[] p) { var v = Get(o, p); try { return v != null && Convert.ToBoolean(v); } catch { return v?.ToString()?.StartsWith("True", StringComparison.OrdinalIgnoreCase) == true; } }
    private static string? Text(object? o, params string[] p) => Get(o, p)?.ToString();
    private static string? ValidText(string? value) => string.IsNullOrWhiteSpace(value) || value.Contains('\uFFFD') ? null : value;
    private static int? NullableInt(object? o, params string[] p) { var v = p.Length == 0 ? o : Get(o, p); try { return v == null ? null : Convert.ToInt32(Get(v, "Value") ?? v); } catch { return null; } }
    private static double? InvokeRealValue(object note)
    {
        try
        {
            var method = note.GetType().GetMethod("CalculateRealValue", new[] { typeof(bool), typeof(bool) });
            var value = method?.Invoke(note, new object[] { true, true });
            return value == null ? null : Convert.ToDouble(value);
        }
        catch { return null; }
    }
}
