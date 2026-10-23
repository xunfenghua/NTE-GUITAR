using System.Text.Json;
using System.Windows.Forms;
using Melanchall.DryWetMidi.Core;
using Melanchall.DryWetMidi.Interaction;
using MidiAutoPlayer.Midi;
using EditorMidiWriter = MidiAutoPlayer.Midi.MidiWriter;
using MidiAutoPlayer.Library;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.Piano.Models;
using MidiAutoPlayer.SemiAuto.Chart;

namespace MidiAutoPlayer.Services;

public sealed class LibraryService
{
    private readonly SongLibrary _library;
    private readonly IWin32Window? _owner;

    public LibraryService(SongLibrary library, IWin32Window? owner = null)
    {
        _library = library;
        _owner = owner;
    }

    public object List(JsonElement payload)
    {
        string filter = payload.TryGetProperty("filter", out var f) ? (f.GetString() ?? "all") : "all";
        string query = payload.TryGetProperty("query", out var q) ? (q.GetString() ?? "") : "";

        var songs = _library.GetAllSongs();

        if (filter.Equals("keyboard", StringComparison.OrdinalIgnoreCase))
        {
            songs = songs.Where(s => SongLibrary.ResolveInstrument(s) == LibraryInstrument.Keyboard).ToList();
        }
        else if (filter.Equals("piano", StringComparison.OrdinalIgnoreCase))
        {
            songs = songs.Where(s => SongLibrary.ResolveInstrument(s) == LibraryInstrument.GamePiano).ToList();
        }
        else if (filter.Equals("guitar", StringComparison.OrdinalIgnoreCase))
        {
            songs = songs.Where(s => SongLibrary.ResolveInstrument(s) == LibraryInstrument.Guitar).ToList();
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            songs = songs.Where(s => s.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase)
                                  || s.SourceName.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var keyboardCount = _library.GetSongCount(LibraryInstrument.Keyboard);
        var pianoCount = _library.GetSongCount(LibraryInstrument.GamePiano);
        var guitarCount = _library.GetSongCount(LibraryInstrument.Guitar);
        var totalCount = _library.GetAllSongs().Count;

        var items = songs.Select(s => new
        {
            id = s.Id,
            name = s.DisplayName,
            sourceName = s.SourceName,
            instrument = SongLibrary.ResolveInstrument(s) switch
            {
                LibraryInstrument.Keyboard => "keyboard",
                LibraryInstrument.Guitar => "guitar",
                LibraryInstrument.GamePiano => "piano",
                _ => "unknown"
            },
            instrumentName = SongLibrary.ResolveInstrument(s) switch
            {
                LibraryInstrument.Keyboard => "键盘谱",
                LibraryInstrument.Guitar => "吉他谱",
                LibraryInstrument.GamePiano => "Piano",
                _ => "未知类型"
            },
            durationFormatted = Utils.TimeFormatter.FormatUs(s.DurationUs),
            durationUs = s.DurationUs,
            noteCount = s.NoteCount,
            transpose = s.Options?.Transpose ?? 0,
            recognitionMode = s.RecognitionModeName ?? "全曲保留",
            createdAt = s.CreatedAt.ToString("yyyy-MM-dd HH:mm")
        }).ToList();

        return new
        {
            items,
            counts = new
            {
                all = totalCount,
                keyboard = keyboardCount,
                piano = pianoCount,
                guitar = guitarCount
            }
        };
    }

    public PianoSongSource GetPianoSource(string songId)
    {
        var song = GetSong(songId) ?? throw new InvalidOperationException("未找到所选曲目。");
        if (SongLibrary.ResolveInstrument(song) != LibraryInstrument.GamePiano)
            throw new InvalidOperationException("游戏钢琴只支持 Piano 分类曲目，请先将 MIDI 导入到 Piano 分类。");
        bool useAlternate = string.Equals(song.SelectedSourceKey, "alternate", StringComparison.Ordinal) &&
            song.SelectedRawNotes is { Count: > 0 } && File.Exists(Path.Combine(_library.CurrentLibraryDirectory, song.Id, "alternate_melody.mid"));
        var notes = useAlternate
            ? song.SelectedRawNotes
            : song.RawNotes ?? MidiRawCache.TryGet(song.SourceHash);
        if (notes is null || notes.Count == 0)
            throw new InvalidOperationException("曲库中没有保存这首歌的原始 MIDI 音符；请重新导入 MIDI 后再使用游戏钢琴。");

        string sourceHash = useAlternate
            ? MidiLoader.ComputeFileSha256(Path.Combine(_library.CurrentLibraryDirectory, song.Id, "alternate_melody.mid"))
            : song.SourceHash;

        return new PianoSongSource(
            song.Id,
            song.DisplayName,
            sourceHash,
            notes.Select(CloneRawNote).ToList());
    }

    public object SaveRawPianoSource(string filePath, string songName)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("MIDI 文件不存在: " + filePath);

        var (sha256, rawNotes, _, _) = MidiLoader.Load(filePath);
        if (rawNotes.Count == 0) throw new InvalidDataException("所选 MIDI 中没有可导入的音符。");
        MidiRawCache.Save(sha256, rawNotes);
        string displayName = string.IsNullOrWhiteSpace(songName)
            ? Path.GetFileNameWithoutExtension(filePath)
            : songName.Trim();
        long durationUs = rawNotes.Max(note => note.StartUs + note.DurationUs);

        var existing = _library.GetAllSongs().FirstOrDefault(meta =>
            SongLibrary.ResolveInstrument(meta) == LibraryInstrument.GamePiano &&
            ((!string.IsNullOrWhiteSpace(meta.SourceHash) && meta.SourceHash == sha256) ||
             string.Equals(meta.SourceName, Path.GetFileName(filePath), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(meta.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)));
        var song = new ConvertedSong
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            DisplayName = displayName,
            CreatedAt = existing?.CreatedAt ?? DateTime.Now,
            SourceName = Path.GetFileName(filePath),
            SourceHash = sha256,
            DurationUs = durationUs,
            Notes = new List<GameNoteEvent>(),
            RawNotes = rawNotes,
            SelectedRawNotes = rawNotes.Select(CloneRawNote).ToList(),
            Options = new ConvertOptions { TargetInstrument = LibraryInstrument.GamePiano, RecognitionModeName = "FullSong" },
            FormatVersion = SongSerializer.CurrentFormatVersion,
            TargetInstrument = LibraryInstrument.GamePiano,
            RecognitionModeName = "FullSong"
        };

        string songDir = Path.Combine(_library.CurrentLibraryDirectory, song.Id);
        Directory.CreateDirectory(songDir);
        File.Copy(filePath, Path.Combine(songDir, "source.mid"), overwrite: true);
        _library.SaveSong(song);
        return new { id = song.Id, name = song.DisplayName, noteCount = song.NoteCount, durationUs, isReplaced = existing != null };
    }

    public object SaveTranscribedSource(string selectedMidiPath, string sourceAudioPath,
        string? alternateMidiPath, string diagnosticsJson, int targetInstrument)
    {
        if (targetInstrument is not (LibraryInstrument.Keyboard or LibraryInstrument.Guitar or LibraryInstrument.GamePiano))
            throw new ArgumentOutOfRangeException(nameof(targetInstrument), "请选择键盘谱、吉他谱或 Piano 曲库分类。");
        if (!File.Exists(sourceAudioPath)) throw new FileNotFoundException("Source audio file not found.", sourceAudioPath);
        if (!File.Exists(selectedMidiPath)) throw new FileNotFoundException("Selected melody MIDI not found.", selectedMidiPath);
        var (sha256, rawNotes, _, _) = MidiLoader.Load(selectedMidiPath);
        if (rawNotes.Count == 0) throw new InvalidDataException("The selected melody MIDI contains no notes.");

        string audioExtension = Path.GetExtension(sourceAudioPath).ToLowerInvariant();
        if (audioExtension is not (".wav" or ".mp3" or ".flac" or ".m4a" or ".ogg" or ".aac"))
            throw new InvalidDataException("The source audio type is not supported.");
        string displayName = Path.GetFileNameWithoutExtension(sourceAudioPath);
        long sourceDurationUs = rawNotes.Max(note => note.StartUs + note.DurationUs);
        var options = new ConvertOptions
        {
            TargetInstrument = targetInstrument,
            RecognitionModeName = "FullSong"
        };
        var song = MidiConverter.Convert(rawNotes, Path.GetFileName(sourceAudioPath), sha256, displayName, options);
        song.SourceName = Path.GetFileName(sourceAudioPath);
        song.SourceHash = sha256;
        song.DurationUs = Math.Max(song.DurationUs, sourceDurationUs);
        song.RawNotes = rawNotes;
        song.SelectedRawNotes = rawNotes.Select(CloneRawNote).ToList();
        song.SourceFormat = "AudioTranscription";

        string audioAssetName = "source-audio" + audioExtension;
        bool hasAlternate = !string.IsNullOrWhiteSpace(alternateMidiPath) && File.Exists(alternateMidiPath);
        _library.SaveSongWithAssets(song, songDirectory =>
        {
            File.Copy(selectedMidiPath, Path.Combine(songDirectory, "source.mid"));
            File.Copy(sourceAudioPath, Path.Combine(songDirectory, audioAssetName));
            if (hasAlternate) File.Copy(alternateMidiPath!, Path.Combine(songDirectory, "alternate_melody.mid"));
            File.WriteAllText(Path.Combine(songDirectory, "transcription_result.json"), diagnosticsJson);
        });
        return new
        {
            id = song.Id,
            name = song.DisplayName,
            noteCount = song.NoteCount,
            durationUs = song.DurationUs,
            targetInstrument,
            sourceAudio = audioAssetName,
            sourceMidi = "source.mid",
            alternateMidi = hasAlternate ? "alternate_melody.mid" : null,
            diagnostics = "transcription_result.json"
        };
    }

    public object ListGuitarSongs(string query = "")
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new { filter = "guitar", query }));
        return List(document.RootElement);
    }

    public ConvertedSong? GetSong(string id)
    {
        try
        {
            return _library.LoadSong(id);
        }
        catch
        {
            return null;
        }
    }

    public string GetOriginalMidiPath(string songId)
    {
        var song = GetSong(songId) ?? throw new FileNotFoundException("未找到所选本地曲目。", songId);
        if (string.IsNullOrWhiteSpace(song.Id) || song.Id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            song.Id.Contains('/') || song.Id.Contains('\\'))
            throw new InvalidDataException("本地曲目编号无效，无法读取原始 MIDI。");

        string root = Path.GetFullPath(_library.CurrentLibraryDirectory);
        string rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        string midiPath = Path.GetFullPath(Path.Combine(root, song.Id, "source.mid"));
        if (!midiPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("本地曲目 MIDI 路径超出曲库目录。");
        if (!File.Exists(midiPath))
            throw new FileNotFoundException("该曲目没有保留原始 MIDI 文件，请重新导入 MIDI 后投稿。", midiPath);
        return midiPath;
    }

    public StoredGuitarChart GenerateGuitarChart(string songId, Nte.GuitarChart.Core.ChartCompileOptions compileOptions,
        bool extractLeadFromFullSong = false)
    {
        var song = GetSong(songId) ?? throw new InvalidOperationException("未找到曲目。");
        if (SongLibrary.ResolveInstrument(song) != 1)
            throw new InvalidOperationException("游戏吉他谱仅支持吉他曲目，请先将曲目转换为吉他模式。");

        var notes = extractLeadFromFullSong
            ? EffectiveSongNotes.ToGuitarOverlayNotes(song)
            : EffectiveSongNotes.ToRawNotes(song);
        if (notes.Count == 0) throw new InvalidOperationException("该曲目没有可用于生成吉他谱的有效 MIDI 音符。");
        var fingerprint = EffectiveNotesFingerprint.Compute(notes);
        var options = compileOptions with
        {
            SourceName = song.DisplayName,
            UseViterbi = true
        };
        var chart = new Nte.GuitarChart.Core.GuitarChartCompiler(
            new Nte.GuitarChart.Core.WheelPositionTimingModel())
            .Compile(Nte.GuitarChart.Core.RawMidiNoteAdapter.FromExistingList(notes), options);
        if (chart.PlayableNoteCount + chart.AtRiskNoteCount == 0)
            throw new InvalidOperationException("没有可演奏的吉他目标，未保存空谱面；请检查音域或曲目转换设置。");

        var stored = new StoredGuitarChart(
            GuitarChartStore.CurrentFormatVersion, song.Id, song.SourceHash, fingerprint,
            DateTime.UtcNow, options, chart);
        _library.SaveGuitarChart(song.Id, stored);
        return stored;
    }

    public StoredGuitarChart GetOrGenerateGuitarChart(string songId, Nte.GuitarChart.Core.ChartCompileOptions compileOptions,
        bool extractLeadFromFullSong = false)
    {
        var song = GetSong(songId) ?? throw new InvalidOperationException("未找到曲目。");
        if (SongLibrary.ResolveInstrument(song) != 1)
            throw new InvalidOperationException("游戏吉他谱仅支持吉他曲目，请先将曲目转换为吉他模式。");
        var notes = extractLeadFromFullSong
            ? EffectiveSongNotes.ToGuitarOverlayNotes(song)
            : EffectiveSongNotes.ToRawNotes(song);
        if (notes.Count == 0) throw new InvalidOperationException("该曲目没有可用于生成吉他谱的有效 MIDI 音符。");
        var currentFingerprint = EffectiveNotesFingerprint.Compute(notes);
        var currentOptions = compileOptions with
        {
            SourceName = song.DisplayName,
            UseViterbi = true
        };
        var loaded = _library.LoadGuitarChart(song.Id, song.SourceHash, currentFingerprint);
        if (loaded.Stored != null && loaded.Stored.CompileOptions == currentOptions) return loaded.Stored;
        return GenerateGuitarChart(song.Id, currentOptions, extractLeadFromFullSong);
    }

    public bool Delete(string id)
    {
        return _library.DeleteSong(id);
    }

    public bool Rename(string id, string newName)
    {
        return _library.RenameSong(id, newName);
    }

    public object? PickMidiFile()
    {
        using var ofd = new OpenFileDialog
        {
            Title = "选择 MIDI 乐谱文件",
            Filter = "MIDI 文件 (*.mid;*.midi)|*.mid;*.midi|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };

        if (ofd.ShowDialog(_owner) != DialogResult.OK) return null;
        return ParseMidiFile(ofd.FileName);
    }

    public object GetEditableSongSnapshot(string songId)
    {
        var song = _library.LoadSong(songId);
        string songDirectory = Path.Combine(_library.CurrentLibraryDirectory, song.Id);
        string sourcePath = Path.Combine(songDirectory, "source.mid");
        var rawNotes = LoadPrimarySourceNotes(song, sourcePath);
        if (rawNotes.Count == 0) throw new InvalidOperationException("曲目没有可编辑的原始 MIDI 音符。");
        var selectedNotes = song.SelectedRawNotes is { Count: > 0 }
            ? song.SelectedRawNotes.Select(CloneRawNote).ToList()
            : rawNotes;
        string selectedSourceKey = string.Equals(song.SelectedSourceKey, "alternate", StringComparison.Ordinal)
            ? "alternate"
            : "source";
        string primaryHash = File.Exists(sourcePath) ? MidiLoader.ComputeFileSha256(sourcePath) : song.SourceHash;
        var primaryTiming = ReadMidiTiming(sourcePath);

        var sources = new List<object>
        {
            new
            {
                key = "source",
                name = song.SourceName,
                sourceHash = primaryHash,
                tempoUsPerQuarter = primaryTiming.TempoUsPerQuarter,
                timeSignature = primaryTiming.TimeSignature,
                notes = rawNotes,
                trackLabels = BuildTrackLabels(rawNotes, sourcePath)
            }
        };
        string alternatePath = Path.Combine(songDirectory, "alternate_melody.mid");
        string selectedSourceHash = primaryHash;
        if (File.Exists(alternatePath))
        {
            var alternateLoad = MidiLoader.Load(alternatePath);
            var alternate = alternateLoad.Notes;
            if (alternate.Count > 0)
            {
                var alternateTiming = ReadMidiTiming(alternatePath);
                if (selectedSourceKey == "alternate")
                {
                    selectedSourceHash = alternateLoad.Sha256;
                    selectedNotes = song.SelectedRawNotes is { Count: > 0 }
                        ? song.SelectedRawNotes.Select(CloneRawNote).ToList()
                        : alternate.Select(CloneRawNote).ToList();
                }
                sources.Add(new
                {
                    key = "alternate",
                    name = "Alternate melody",
                    sourceHash = alternateLoad.Sha256,
                    tempoUsPerQuarter = alternateTiming.TempoUsPerQuarter,
                    timeSignature = alternateTiming.TimeSignature,
                    notes = alternate,
                    trackLabels = BuildTrackLabels(alternate, alternatePath)
                });
            }
            else if (selectedSourceKey == "alternate")
            {
                selectedSourceKey = "source";
                selectedNotes = rawNotes;
            }
        }
        else if (selectedSourceKey == "alternate")
        {
            selectedSourceKey = "source";
            selectedNotes = rawNotes;
        }
        string timingSourcePath = selectedSourceKey == "alternate" && File.Exists(alternatePath) ? alternatePath : sourcePath;
        var timing = ReadMidiTiming(timingSourcePath);

        return new
        {
            id = song.Id,
            name = song.DisplayName,
            sourceName = song.SourceName,
            sourceHash = song.SourceHash,
            selectedSourceKey,
            selectedSourceHash,
            tempoUsPerQuarter = timing.TempoUsPerQuarter,
            timeSignature = timing.TimeSignature,
            instrument = SongLibrary.ResolveInstrument(song),
            isNew = false,
            sources,
            notes = selectedNotes,
            allNotes = rawNotes,
            options = song.Options
        };
    }

    public object OpenMidiInEditor(string filePath)
    {
        if (!File.Exists(filePath)) throw new FileNotFoundException("MIDI file not found.", filePath);
        var loaded = MidiLoader.Load(filePath);
        if (loaded.Notes.Count == 0) throw new InvalidDataException("The selected MIDI contains no editable notes.");
        var timing = ReadMidiTiming(filePath);
        return new
        {
            id = (string?)null,
            name = Path.GetFileNameWithoutExtension(filePath),
            sourceName = Path.GetFileName(filePath),
            sourceHash = loaded.Sha256,
            tempoUsPerQuarter = timing.TempoUsPerQuarter,
            timeSignature = timing.TimeSignature,
            sourcePath = filePath,
            instrument = LibraryInstrument.GamePiano,
            isNew = true,
            sources = new[]
            {
                new
                {
                    key = "source",
                    name = Path.GetFileName(filePath),
                    notes = loaded.Notes,
                    tempoUsPerQuarter = timing.TempoUsPerQuarter,
                    timeSignature = timing.TimeSignature,
                    trackLabels = BuildTrackLabels(loaded.Notes, filePath)
                }
            },
            notes = loaded.Notes,
            allNotes = loaded.Notes,
            options = new ConvertOptions { TargetInstrument = LibraryInstrument.GamePiano, RecognitionModeName = "FullSong" }
        };
    }

    public object SaveEditedSong(JsonElement payload)
    {
        string? songId = payload.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        string name = payload.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "" : "";
        string? sourcePath = payload.TryGetProperty("sourcePath", out var sourcePathElement) ? sourcePathElement.GetString() : null;
        string sourceKey = payload.TryGetProperty("sourceKey", out var sourceKeyElement) ? sourceKeyElement.GetString() ?? "source" : "source";
        if (sourceKey is not ("source" or "alternate")) throw new ArgumentException("Unknown editor source key.");
        var notes = DeserializeEditorNotes(payload.GetProperty("notes"));
        if (notes.Count == 0) throw new InvalidDataException("The edited song must contain at least one note.");

        ConvertedSong? existing = !string.IsNullOrWhiteSpace(songId) ? _library.LoadSong(songId) : null;
        if (sourceKey == "alternate" && existing is null)
            throw new InvalidOperationException("An alternate source can only be edited on an existing transcription song.");
        string existingDirectory = existing is null ? "" : Path.Combine(_library.CurrentLibraryDirectory, existing.Id);
        string sourceMidiPath = existing is null ? sourcePath ?? "" : Path.Combine(existingDirectory, "source.mid");
        string alternateMidiPath = existing is null ? "" : Path.Combine(existingDirectory, "alternate_melody.mid");
        if (existing is not null && sourceKey == "alternate" && !File.Exists(alternateMidiPath))
            throw new InvalidOperationException("The selected song has no alternate melody source.");
        string? templatePath = existing is null ? sourcePath : sourceKey == "alternate" ? alternateMidiPath : sourceMidiPath;
        byte[] midiBytes = EditorMidiWriter.Write(notes, templatePath, ReadEditorTempoOverride(payload));
        // Parse the generated file before touching the library, catching invalid output and refreshing timing metadata.
        string stagedMidiPath = Path.Combine(Path.GetTempPath(), "nte-editor-" + Guid.NewGuid().ToString("N") + ".mid");
        File.WriteAllBytes(stagedMidiPath, midiBytes);
        try
        {
            var parsed = MidiLoader.Load(stagedMidiPath);
            if (parsed.Notes.Count == 0) throw new InvalidDataException("Generated MIDI did not contain any notes.");
            string hash = parsed.Sha256;
            string displayName = string.IsNullOrWhiteSpace(name)
                ? existing?.DisplayName ?? (sourcePath is not null ? Path.GetFileNameWithoutExtension(sourcePath) : "Edited MIDI")
                : name.Trim();
            int instrument = existing is null ? LibraryInstrument.GamePiano : SongLibrary.ResolveInstrument(existing);
            var options = existing?.Options ?? new ConvertOptions
            {
                TargetInstrument = LibraryInstrument.GamePiano,
                RecognitionModeName = "FullSong"
            };
            options.TargetInstrument = instrument;

            string sourceName = existing?.SourceName ?? Path.GetFileName(sourcePath ?? "edited.mid");
            string convertedHash = sourceKey == "alternate" && existing is not null ? existing.SourceHash : hash;
            var converted = MidiConverter.Convert(parsed.Notes, sourceName, convertedHash, displayName, options);
            converted.Id = existing?.Id ?? Guid.NewGuid().ToString("N");
            converted.CreatedAt = existing?.CreatedAt ?? DateTime.Now;
            converted.DisplayName = displayName;
            converted.SourceName = sourceName;
            converted.SourceHash = convertedHash;
            converted.RawNotes = sourceKey == "alternate" && existing is not null
                ? LoadPrimarySourceNotes(existing, sourceMidiPath)
                : parsed.Notes;
            converted.SelectedRawNotes = SelectNotes(parsed.Notes, options);
            converted.SelectedSourceKey = sourceKey;
            converted.Options = options;
            converted.TargetInstrument = instrument;
            converted.RecognitionModeName = existing?.RecognitionModeName ?? options.RecognitionModeName;
            converted.FormatVersion = SongSerializer.CurrentFormatVersion;
            if (sourceKey == "alternate" && existing is not null)
                converted.DurationUs = parsed.Notes.Max(note => note.StartUs + note.DurationUs);

            if (existing is null)
            {
                _library.SaveSongWithAssets(converted, directory => File.WriteAllBytes(Path.Combine(directory, "source.mid"), midiBytes));
            }
            else
            {
                string destinationAsset = sourceKey == "alternate" ? "alternate_melody.mid" : "source.mid";
                _library.UpdateSongWithAssets(converted, directory => File.WriteAllBytes(Path.Combine(directory, destinationAsset), midiBytes));
            }
            return new { id = converted.Id, name = converted.DisplayName, instrument, noteCount = converted.NoteCount, sourceHash = converted.SourceHash, selectedSourceKey = converted.SelectedSourceKey };
        }
        finally
        {
            try { File.Delete(stagedMidiPath); } catch { }
        }
    }

    public object? ExportEditedMidi(JsonElement payload)
    {
        var notes = DeserializeEditorNotes(payload.GetProperty("notes"));
        string? templatePath = payload.TryGetProperty("sourcePath", out var pathElement) ? pathElement.GetString() : null;
        string sourceKey = payload.TryGetProperty("sourceKey", out var sourceKeyElement) ? sourceKeyElement.GetString() ?? "source" : "source";
        if (payload.TryGetProperty("id", out var idElement) && !string.IsNullOrWhiteSpace(idElement.GetString()))
        {
            string songDir = Path.Combine(_library.CurrentLibraryDirectory, idElement.GetString()!);
            string libraryMidi = Path.Combine(songDir, sourceKey == "alternate" ? "alternate_melody.mid" : "source.mid");
            if (File.Exists(libraryMidi)) templatePath = libraryMidi;
        }
        using var dialog = new SaveFileDialog
        {
            Title = "Export edited MIDI",
            Filter = "MIDI files (*.mid)|*.mid|All files (*.*)|*.*",
            FileName = Path.GetFileNameWithoutExtension((payload.TryGetProperty("name", out var name) ? name.GetString() : null) ?? "edited") + ".mid",
            AddExtension = true,
            DefaultExt = "mid",
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(_owner) != DialogResult.OK) return null;
        string destinationPath = dialog.FileName;
        string destinationDirectory = Path.GetDirectoryName(destinationPath)!;
        string temporaryPath = Path.Combine(destinationDirectory,
            "." + Path.GetFileName(destinationPath) + "." + Guid.NewGuid().ToString("N") + ".tmp.mid");
        try
        {
            byte[] bytes = EditorMidiWriter.Write(notes, templatePath, ReadEditorTempoOverride(payload));
            File.WriteAllBytes(temporaryPath, bytes);
            var parsed = MidiLoader.Load(temporaryPath);
            if (parsed.Notes.Count == 0) throw new InvalidDataException("Exported MIDI did not contain any notes.");
            File.Move(temporaryPath, destinationPath, overwrite: true);
            return new { filePath = destinationPath, fileName = Path.GetFileName(destinationPath), noteCount = parsed.Notes.Count };
        }
        finally
        {
            try { if (File.Exists(temporaryPath)) File.Delete(temporaryPath); } catch { }
        }
    }

    private static List<RawMidiNote> DeserializeEditorNotes(JsonElement notesElement)
    {
        var notes = JsonSerializer.Deserialize<List<RawMidiNote>>(notesElement.GetRawText(), SongSerializer.JsonOptions)
            ?? throw new InvalidDataException("Edited MIDI note data is invalid.");
        foreach (var note in notes)
        {
            if (note.NoteNumber > 127 || note.Velocity > 127 || note.Channel > 15 || note.TrackId < 0 || note.StartUs < 0 || note.DurationUs < 0)
                throw new InvalidDataException("Edited MIDI note data contains an invalid value.");
        }
        return notes;
    }

    private static long? ReadEditorTempoOverride(JsonElement payload)
    {
        if (!payload.TryGetProperty("tempoUsPerQuarter", out var tempoElement) || tempoElement.ValueKind == JsonValueKind.Null)
            return null;
        long tempo = tempoElement.GetInt64();
        if (tempo is < 200_000 or > 3_000_000)
            throw new InvalidDataException("Editor BPM must be between 20 and 300.");
        return tempo;
    }

    private static List<object> BuildTrackLabels(IReadOnlyList<RawMidiNote> notes, string? midiPath)
    {
        List<TrackMetadata> metadata = !string.IsNullOrWhiteSpace(midiPath) && File.Exists(midiPath)
            ? MidiLoader.Load(midiPath).Tracks
            : new List<TrackMetadata>();
        var metadataById = metadata.ToDictionary(track => track.TrackId);
        var notesByTrack = notes.GroupBy(note => note.TrackId).ToDictionary(group => group.Key, group => group.ToList());
        var trackIds = metadata.Select(track => track.TrackId).Concat(notesByTrack.Keys).Distinct().OrderBy(id => id);
        var labels = new List<object>();
        foreach (int trackId in trackIds)
        {
            metadataById.TryGetValue(trackId, out var track);
            notesByTrack.TryGetValue(trackId, out var trackNotes);
            int program = track?.Program ?? trackNotes?.FirstOrDefault()?.Program ?? -1;
            int minPitch = track?.MinPitch ?? (trackNotes is { Count: > 0 } ? trackNotes.Min(note => (int)note.NoteNumber) : -1);
            int maxPitch = track?.MaxPitch ?? (trackNotes is { Count: > 0 } ? trackNotes.Max(note => (int)note.NoteNumber) : -1);
            string name = string.IsNullOrWhiteSpace(track?.Name) ? $"Track #{trackId}" : track.Name;
            labels.Add(new
            {
                id = trackId,
                name,
                program,
                instrumentName = TrackMetadata.GetGmInstrumentName(program),
                noteCount = track?.NoteCount ?? trackNotes?.Count ?? 0,
                minPitch,
                maxPitch,
                pitchRange = minPitch >= 0 && maxPitch >= 0
                    ? $"{TrackMetadata.NoteNumberToName(minPitch)}~{TrackMetadata.NoteNumberToName(maxPitch)} ({minPitch}~{maxPitch})"
                    : "-",
                channels = track?.Channels.OrderBy(channel => channel).ToList() ?? trackNotes?.Select(note => note.Channel).Distinct().OrderBy(channel => channel).ToList() ?? new List<byte>()
            });
        }
        return labels;
    }

    private static (long TempoUsPerQuarter, object TimeSignature) ReadMidiTiming(string? midiPath)
    {
        if (string.IsNullOrWhiteSpace(midiPath) || !File.Exists(midiPath))
            return (500_000, new { numerator = 4, denominator = 4 });
        TempoMap tempoMap = MidiFile.Read(midiPath).GetTempoMap();

        var start = new MidiTimeSpan(0);
        var tempo = tempoMap.GetTempoAtTime(start);
        var signature = tempoMap.GetTimeSignatureAtTime(start);
        return (tempo.MicrosecondsPerQuarterNote, new { numerator = signature.Numerator, denominator = signature.Denominator });
    }

    private static List<RawMidiNote> ResolveRawNotes(ConvertedSong song, string sourcePath)
    {
        var notes = song.RawNotes ?? MidiRawCache.TryGet(song.SourceHash);
        if (notes is { Count: > 0 }) return notes.Select(CloneRawNote).ToList();
        if (File.Exists(sourcePath)) return MidiLoader.Load(sourcePath).Notes;
        return new List<RawMidiNote>();
    }

    private static List<RawMidiNote> LoadPrimarySourceNotes(ConvertedSong song, string sourcePath)
    {
        if (File.Exists(sourcePath)) return MidiLoader.Load(sourcePath).Notes;
        return ResolveRawNotes(song, sourcePath);
    }

    private static List<RawMidiNote> SelectNotes(IReadOnlyList<RawMidiNote> rawNotes, ConvertOptions options)
    {
        IEnumerable<RawMidiNote> selected = rawNotes;
        if (options.EnabledTracks is { Count: > 0 })
        {
            var enabled = options.EnabledTracks.ToHashSet();
            selected = selected.Where(note => enabled.Contains(note.TrackId));
        }
        string strategy = options.RecognitionModeName;
        if (!string.IsNullOrWhiteSpace(strategy) && strategy is not ("FullSong" or "ManualTracks") &&
            Enum.TryParse<RecognitionMode>(strategy, out var mode))
        {
            var candidates = Recognition.RecognitionService.GenerateAllCandidates(selected.ToList(), null);
            if (candidates.TryGetValue(mode, out var result) && result.Notes.Count > 0) selected = result.Notes;
        }
        return selected.Select(CloneRawNote).ToList();
    }

    public object ParseMidiFile(string filePath)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("MIDI 文件不存在: " + filePath);

        var (sha256, rawNotes, trackIds, trackMetas) = MidiLoader.Load(filePath);
        long maxEndUs = rawNotes.Count > 0 ? rawNotes.Max(n => n.StartUs + n.DurationUs) : 0;

        return new
        {
            filePath,
            fileName = Path.GetFileName(filePath),
            sha256,
            noteCount = rawNotes.Count,
            durationUs = maxEndUs,
            durationFormatted = Utils.TimeFormatter.FormatUs(maxEndUs),
            trackCount = trackMetas.Count,
            tracks = trackMetas.Select(t => new
            {
                id = t.TrackId,
                name = string.IsNullOrWhiteSpace(t.Name) ? $"Track #{t.TrackId}" : t.Name,
                program = t.Program,
                instrumentName = t.InstrumentName,
                noteCount = t.NoteCount,
                minPitch = t.MinPitch,
                maxPitch = t.MaxPitch,
                pitchRange = t.PitchRangeDisplay
            }).ToList()
        };
    }

    public object ConvertAndSave(
        string filePath,
        string songName,
        int targetInstrument,
        int transpose,
        string outOfRangeMode,
        bool filterDrumTrack,
        bool enableMelodyExtraction,
        string melodyStrategy,
        List<int>? enabledTracks = null,
        string? existingSongId = null)
    {
        if (!File.Exists(filePath))
            throw new FileNotFoundException("MIDI 文件不存在: " + filePath);

        var (sha256, rawNotes, _, trackMetas) = MidiLoader.Load(filePath);
        MidiRawCache.Save(sha256, rawNotes);

        var fullRawNotes = rawNotes; // Retain full unpruned raw notes for permanent preservation

        // 如果用户选择了特定音轨 (人工提取音轨策略)
        if (enabledTracks != null && enabledTracks.Count > 0)
        {
            var allowed = new HashSet<int>(enabledTracks);
            rawNotes = rawNotes.Where(n => allowed.Contains(n.TrackId)).ToList();
        }

        // 如果启用了特定主旋律提取算法 (非 FullSong 且非 ManualTracks)
        string strategy = enableMelodyExtraction ? melodyStrategy : "FullSong";
        if (strategy != "FullSong" && strategy != "ManualTracks")
        {
            if (Enum.TryParse<RecognitionMode>(strategy, out var recMode))
            {
                var candidates = Recognition.RecognitionService.GenerateAllCandidates(rawNotes, trackMetas);
                if (candidates.TryGetValue(recMode, out var chosenResult) && chosenResult.Notes.Count > 0)
                {
                    rawNotes = chosenResult.Notes;
                }
            }
        }

        var options = new ConvertOptions
        {
            Transpose = transpose,
            OutOfRangeMode = string.IsNullOrWhiteSpace(outOfRangeMode) ? "OctaveFold" : outOfRangeMode,
            IgnoreDrumChannel = filterDrumTrack,
            TargetInstrument = targetInstrument,
            RecognitionModeName = strategy,
            EnabledTracks = enabledTracks
        };

        string displayName = string.IsNullOrWhiteSpace(songName)
            ? Path.GetFileNameWithoutExtension(filePath)
            : songName;

        var converted = MidiConverter.Convert(
            rawNotes,
            Path.GetFileName(filePath),
            sha256,
            displayName,
            options);

        converted.RawNotes = fullRawNotes;
        converted.SelectedRawNotes = rawNotes.Select(CloneRawNote).ToList();

        // 智能匹配待覆盖/替换的原曲目
        var allSongs = _library.GetAllSongs();
        SongMeta? existing = null;
        if (!string.IsNullOrWhiteSpace(existingSongId))
        {
            existing = allSongs.FirstOrDefault(s => s.Id == existingSongId);
        }
        if (existing == null)
        {
            existing = allSongs.FirstOrDefault(s =>
                SongLibrary.ResolveInstrument(s) == targetInstrument &&
                ((!string.IsNullOrEmpty(s.SourceHash) && s.SourceHash == sha256) ||
                 string.Equals(s.SourceName, Path.GetFileName(filePath), StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(s.DisplayName, displayName, StringComparison.OrdinalIgnoreCase)));
        }

        if (existing != null)
        {
            converted.Id = existing.Id;
            converted.CreatedAt = existing.CreatedAt;
        }

        // 备份原版 MIDI 文件至曲目私有目录，确保永久不丢失
        string songDir = Path.Combine(_library.CurrentLibraryDirectory, converted.Id);
        Directory.CreateDirectory(songDir);
        try
        {
            string destMid = Path.Combine(songDir, "source.mid");
            File.Copy(filePath, destMid, overwrite: true);
        }
        catch { }

        _library.SaveSong(converted);

        return new
        {
            id = converted.Id,
            name = converted.DisplayName,
            noteCount = converted.NoteCount,
            durationUs = converted.DurationUs,
            isReplaced = existing != null
        };
    }

    public object GetSongForReconvert(string songId)
    {
        var existing = _library.LoadSong(songId);
        if (existing == null) throw new InvalidOperationException("未找到曲目");

        var rawNotes = existing.RawNotes ?? MidiRawCache.TryGet(existing.SourceHash);
        if (rawNotes == null || rawNotes.Count == 0)
        {
            string sourceMid = Path.Combine(_library.CurrentLibraryDirectory, existing.Id, "source.mid");
            if (File.Exists(sourceMid))
            {
                try
                {
                    var (_, reloaded, _, _) = MidiLoader.Load(sourceMid);
                    rawNotes = reloaded;
                }
                catch { }
            }
        }

        var trackList = new List<object>();

        if (rawNotes != null && rawNotes.Count > 0)
        {
            var grouped = rawNotes.GroupBy(n => n.TrackId).OrderBy(g => g.Key);
            foreach (var g in grouped)
            {
                int prog = g.First().Program;
                int minP = g.Min(n => (int)n.NoteNumber);
                int maxP = g.Max(n => (int)n.NoteNumber);
                string range = $"{TrackMetadata.NoteNumberToName(minP)}~{TrackMetadata.NoteNumberToName(maxP)} ({minP}~{maxP})";
                trackList.Add(new
                {
                    id = g.Key,
                    name = $"Track #{g.Key}",
                    program = prog,
                    instrumentName = TrackMetadata.GetGmInstrumentName(prog),
                    noteCount = g.Count(),
                    minPitch = minP,
                    maxPitch = maxP,
                    pitchRange = range
                });
            }
        }

        return new
        {
            id = existing.Id,
            name = existing.DisplayName,
            sourceName = existing.SourceName,
            sourceHash = existing.SourceHash,
            targetInstrument = existing.TargetInstrument,
            transpose = existing.Options.Transpose,
            melodyStrategy = existing.RecognitionModeName,
            outOfRange = existing.Options.OutOfRangeMode,
            enabledTracks = existing.Options.EnabledTracks,
            tracks = trackList
        };
    }

    public object ReconvertSong(
        string songId,
        int targetInstrument,
        int transpose,
        string melodyStrategy,
        string outOfRangeMode = "OctaveFold",
        List<int>? enabledTracks = null)
    {
        var existing = _library.LoadSong(songId);
        if (existing == null) throw new InvalidOperationException("未找到曲目");

        var rawNotes = existing.RawNotes ?? MidiRawCache.TryGet(existing.SourceHash);
        if (rawNotes == null || rawNotes.Count == 0)
        {
            string sourceMid = Path.Combine(_library.CurrentLibraryDirectory, existing.Id, "source.mid");
            if (File.Exists(sourceMid))
            {
                try
                {
                    var (_, reloaded, _, _) = MidiLoader.Load(sourceMid);
                    rawNotes = reloaded;
                }
                catch { }
            }
        }

        if (rawNotes == null || rawNotes.Count == 0)
        {
            rawNotes = existing.Notes.Select(n => new RawMidiNote
            {
                StartUs = n.TimeUs,
                DurationUs = n.DurationUs > 0 ? n.DurationUs : 300_000,
                NoteNumber = (byte)(52 + n.NoteIndex),
                Velocity = 100,
                Channel = 0,
                Program = 0
            }).ToList();
        }

        var fullRawNotes = rawNotes;

        // 人工指定音轨过滤
        if (enabledTracks != null && enabledTracks.Count > 0)
        {
            var allowed = new HashSet<int>(enabledTracks);
            rawNotes = rawNotes.Where(n => allowed.Contains(n.TrackId)).ToList();
        }

        // 旋律算法提取
        if (!string.IsNullOrEmpty(melodyStrategy) && melodyStrategy != "FullSong" && melodyStrategy != "ManualTracks")
        {
            if (Enum.TryParse<RecognitionMode>(melodyStrategy, out var recMode))
            {
                var candidates = Recognition.RecognitionService.GenerateAllCandidates(rawNotes, null);
                if (candidates.TryGetValue(recMode, out var chosenResult) && chosenResult.Notes.Count > 0)
                {
                    rawNotes = chosenResult.Notes;
                }
            }
        }

        var options = new ConvertOptions
        {
            Transpose = transpose,
            OutOfRangeMode = string.IsNullOrWhiteSpace(outOfRangeMode) ? "OctaveFold" : outOfRangeMode,
            IgnoreDrumChannel = true,
            TargetInstrument = targetInstrument,
            RecognitionModeName = melodyStrategy,
            EnabledTracks = enabledTracks
        };

        var recompiled = MidiConverter.Convert(
            rawNotes,
            existing.SourceName,
            existing.SourceHash,
            existing.DisplayName,
            options);

        recompiled.Id = existing.Id;
        recompiled.DisplayName = existing.DisplayName;
        recompiled.CreatedAt = existing.CreatedAt;
        recompiled.RawNotes = fullRawNotes;
        recompiled.SelectedRawNotes = rawNotes.Select(CloneRawNote).ToList();

        _library.SaveSong(recompiled);

        return new
        {
            id = recompiled.Id,
            name = recompiled.DisplayName,
            noteCount = recompiled.NoteCount,
            durationUs = recompiled.DurationUs
        };
    }

    public object PreviewExtraction(
        string? filePath,
        string? songId,
        int targetInstrument,
        int transpose,
        string outOfRangeMode,
        string melodyStrategy,
        List<int>? enabledTracks = null)
    {
        List<RawMidiNote> rawNotes;
        List<TrackMetadata> trackMetas = new();

        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            var loaded = MidiLoader.Load(filePath);
            rawNotes = loaded.Notes;
            trackMetas = loaded.Tracks;
        }
        else if (!string.IsNullOrEmpty(songId))
        {
            var existing = _library.LoadSong(songId);
            if (existing == null) throw new InvalidOperationException("未找到曲目");
            rawNotes = existing.RawNotes ?? MidiRawCache.TryGet(existing.SourceHash) ?? new List<RawMidiNote>();
            if (rawNotes.Count == 0)
            {
                string sourceMid = Path.Combine(_library.CurrentLibraryDirectory, existing.Id, "source.mid");
                if (File.Exists(sourceMid))
                {
                    try
                    {
                        var (_, reloaded, _, _) = MidiLoader.Load(sourceMid);
                        rawNotes = reloaded;
                    }
                    catch { }
                }
            }
            var grouped = rawNotes.GroupBy(n => n.TrackId);
            foreach (var g in grouped)
            {
                int prog = g.First().Program;
                int minP = g.Min(n => (int)n.NoteNumber);
                int maxP = g.Max(n => (int)n.NoteNumber);
                trackMetas.Add(new TrackMetadata
                {
                    TrackId = g.Key,
                    Name = $"Track #{g.Key}",
                    Program = prog,
                    NoteCount = g.Count(),
                    MinPitch = minP,
                    MaxPitch = maxP
                });
            }
        }
        else
        {
            throw new ArgumentException("缺少 filePath 或 songId");
        }

        int origCount = rawNotes.Count;
        int origMin = rawNotes.Count > 0 ? rawNotes.Min(n => (int)n.NoteNumber) : -1;
        int origMax = rawNotes.Count > 0 ? rawNotes.Max(n => (int)n.NoteNumber) : -1;
        string origRange = (origMin >= 0 && origMax >= 0)
            ? $"{TrackMetadata.NoteNumberToName(origMin)}~{TrackMetadata.NoteNumberToName(origMax)} ({origMin}~{origMax})"
            : "-";

        // 1. Filter enabled tracks
        if (enabledTracks != null && enabledTracks.Count > 0)
        {
            var allowed = new HashSet<int>(enabledTracks);
            rawNotes = rawNotes.Where(n => allowed.Contains(n.TrackId)).ToList();
        }

        int melodyScore = 100;
        string strategySummary = "全曲音符完整保留";

        // 2. Apply melody extraction
        string strategy = melodyStrategy ?? "ContinuousMelody";
        if (strategy != "FullSong" && strategy != "ManualTracks")
        {
            if (Enum.TryParse<RecognitionMode>(strategy, out var recMode))
            {
                var candidates = Recognition.RecognitionService.GenerateAllCandidates(rawNotes, trackMetas);
                if (candidates.TryGetValue(recMode, out var chosen) && chosen.Notes.Count > 0)
                {
                    rawNotes = chosen.Notes;
                    melodyScore = chosen.MelodyScore;
                    strategySummary = chosen.Summary;
                }
            }
        }
        else if (strategy == "ManualTracks")
        {
            strategySummary = $"人工保留勾选音轨 (共 {enabledTracks?.Count ?? 0} 轨)";
        }

        int extCount = rawNotes.Count;
        int extMin = rawNotes.Count > 0 ? Math.Clamp(rawNotes.Min(n => n.NoteNumber + transpose), 0, 127) : -1;
        int extMax = rawNotes.Count > 0 ? Math.Clamp(rawNotes.Max(n => n.NoteNumber + transpose), 0, 127) : -1;
        string extRange = (extMin >= 0 && extMax >= 0)
            ? $"{TrackMetadata.NoteNumberToName(extMin)}~{TrackMetadata.NoteNumberToName(extMax)} ({extMin}~{extMax})"
            : "-";

        double reductionPercent = origCount > 0 ? Math.Round((1.0 - (double)extCount / origCount) * 100.0, 1) : 0;

        int instMin = targetInstrument == 1 ? 40 : 52;
        int instMax = targetInstrument == 1 ? 88 : 87;
        string instRangeName = targetInstrument == 1 ? "吉他 0~24品 (E2~E6, MIDI 40~88)" : "36键常规乐器 (E3~D#6, MIDI 52~87)";

        int outOfRangeCount = rawNotes.Count(n => (n.NoteNumber + transpose) < instMin || (n.NoteNumber + transpose) > instMax);
        double rangeFitRate = extCount > 0 ? Math.Round((1.0 - (double)outOfRangeCount / extCount) * 100.0, 1) : 100.0;

        return new
        {
            originalNoteCount = origCount,
            originalPitchRange = origRange,
            extractedNoteCount = extCount,
            extractedPitchRange = extRange,
            reductionPercent,
            melodyScore,
            targetInstrumentName = targetInstrument == 1 ? "吉他模式" : "36键模式",
            instrumentStandardRange = instRangeName,
            outOfRangeCount,
            rangeFitRate,
            strategySummary
        };
    }

    public List<RawMidiNote> GetNotesForAudition(
        string? filePath,
        string? songId,
        int transpose,
        string melodyStrategy,
        List<int>? enabledTracks = null)
    {
        List<RawMidiNote> rawNotes;
        List<TrackMetadata> trackMetas = new();

        if (!string.IsNullOrEmpty(filePath) && File.Exists(filePath))
        {
            var loaded = MidiLoader.Load(filePath);
            rawNotes = loaded.Notes;
            trackMetas = loaded.Tracks;
        }
        else if (!string.IsNullOrEmpty(songId))
        {
            var existing = _library.LoadSong(songId);
            if (existing == null) return new List<RawMidiNote>();
            rawNotes = existing.RawNotes ?? MidiRawCache.TryGet(existing.SourceHash) ?? new List<RawMidiNote>();
            if (rawNotes.Count == 0)
            {
                string sourceMid = Path.Combine(_library.CurrentLibraryDirectory, existing.Id, "source.mid");
                if (File.Exists(sourceMid))
                {
                    try
                    {
                        var (_, reloaded, _, _) = MidiLoader.Load(sourceMid);
                        rawNotes = reloaded;
                    }
                    catch { }
                }
            }
            var grouped = rawNotes.GroupBy(n => n.TrackId);
            foreach (var g in grouped)
            {
                int prog = g.First().Program;
                trackMetas.Add(new TrackMetadata
                {
                    TrackId = g.Key,
                    Name = $"Track #{g.Key}",
                    Program = prog,
                    NoteCount = g.Count()
                });
            }
        }
        else
        {
            return new List<RawMidiNote>();
        }

        if (enabledTracks != null && enabledTracks.Count > 0)
        {
            var allowed = new HashSet<int>(enabledTracks);
            rawNotes = rawNotes.Where(n => allowed.Contains(n.TrackId)).ToList();
        }

        string strategy = melodyStrategy ?? "ContinuousMelody";
        if (strategy != "FullSong" && strategy != "ManualTracks")
        {
            if (Enum.TryParse<RecognitionMode>(strategy, out var recMode))
            {
                var candidates = Recognition.RecognitionService.GenerateAllCandidates(rawNotes, trackMetas);
                if (candidates.TryGetValue(recMode, out var chosen) && chosen.Notes.Count > 0)
                {
                    rawNotes = chosen.Notes;
                }
            }
        }

        if (transpose != 0)
        {
            rawNotes = rawNotes.Select(n => new RawMidiNote
            {
                StartUs = n.StartUs,
                DurationUs = n.DurationUs,
                NoteNumber = (byte)Math.Clamp(n.NoteNumber + transpose, 0, 127),
                Velocity = n.Velocity,
                Channel = n.Channel,
                TrackId = n.TrackId,
                Program = n.Program
            }).ToList();
        }

        return rawNotes;
    }

    public int SeedBuiltInScoresIfNecessary(string? customScoresDir = null, bool force = false)
    {
        string? scoresDir = customScoresDir;
        if (string.IsNullOrWhiteSpace(scoresDir) || !Directory.Exists(scoresDir))
        {
            string p1 = Path.Combine(AppContext.BaseDirectory, "谱子");
            string p2 = Path.Combine(AppContext.BaseDirectory, "Scores");
            string p3 = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "谱子"));

            if (Directory.Exists(p1)) scoresDir = p1;
            else if (Directory.Exists(p2)) scoresDir = p2;
            else if (Directory.Exists(p3)) scoresDir = p3;
            else
            {
                // 单 exe 独立运行模式：释放嵌入式程序集中的内置分类谱库
                scoresDir = Utils.EmbeddedAssetExtractor.EnsureScoresExtracted();
            }
        }

        if (string.IsNullOrWhiteSpace(scoresDir) || !Directory.Exists(scoresDir))
        {
            return 0;
        }

        string markerFile = Path.Combine(_library.CurrentLibraryDirectory, ".builtin_seeded_v1");
        if (!force && File.Exists(markerFile))
        {
            return 0;
        }

        int seededCount = 0;
        var existingSongs = _library.GetAllSongs();

        // 1. 吉他分类 (TargetInstrument = 1)
        string guitarDir = Path.Combine(scoresDir, "吉他");
        if (!Directory.Exists(guitarDir)) guitarDir = Path.Combine(scoresDir, "Guitar");
        if (Directory.Exists(guitarDir))
        {
            var files = Directory.GetFiles(guitarDir, "*.mid", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                string fileName = Path.GetFileName(file);
                string songName = Path.GetFileNameWithoutExtension(file);

                bool alreadyExists = existingSongs.Any(s =>
                    SongLibrary.ResolveInstrument(s) == 1 &&
                    (string.Equals(s.DisplayName, songName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(s.SourceName, fileName, StringComparison.OrdinalIgnoreCase)));

                if (!alreadyExists)
                {
                    try
                    {
                        ConvertAndSave(
                            filePath: file,
                            songName: songName,
                            targetInstrument: 1, // 吉他
                            transpose: 0,
                            outOfRangeMode: "OctaveFold",
                            filterDrumTrack: true,
                            enableMelodyExtraction: false,
                            melodyStrategy: "FullSong"
                        );
                        seededCount++;
                    }
                    catch { }
                }
            }
        }

        // Legacy piano folder content remains in the Keyboard script category (target 0).
        string pianoDir = Path.Combine(scoresDir, "钢琴");
        if (!Directory.Exists(pianoDir)) pianoDir = Path.Combine(scoresDir, "Piano");
        if (Directory.Exists(pianoDir))
        {
            var files = Directory.GetFiles(pianoDir, "*.mid", SearchOption.AllDirectories);
            foreach (var file in files)
            {
                string fileName = Path.GetFileName(file);
                string songName = Path.GetFileNameWithoutExtension(file);

                bool alreadyExists = existingSongs.Any(s =>
                    SongLibrary.ResolveInstrument(s) == 0 &&
                    (string.Equals(s.DisplayName, songName, StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(s.SourceName, fileName, StringComparison.OrdinalIgnoreCase)));

                if (!alreadyExists)
                {
                    try
                    {
                        ConvertAndSave(
                            filePath: file,
                            songName: songName,
                            targetInstrument: 0, // 键盘/钢琴
                            transpose: 0,
                            outOfRangeMode: "OctaveFold",
                            filterDrumTrack: true,
                            enableMelodyExtraction: false,
                            melodyStrategy: "FullSong"
                        );
                        seededCount++;
                    }
                    catch { }
                }
            }
        }

        try
        {
            File.WriteAllText(markerFile, DateTime.UtcNow.ToString("O"));
        }
        catch { }

        return seededCount;
    }

    private static RawMidiNote CloneRawNote(RawMidiNote note) => new()
    {
        StartUs = note.StartUs,
        DurationUs = note.DurationUs,
        NoteNumber = note.NoteNumber,
        Velocity = note.Velocity,
        Channel = note.Channel,
        TrackId = note.TrackId,
        Program = note.Program,
        BarIndex = note.BarIndex,
        BeatInBar = note.BeatInBar,
        BeatsPerBar = note.BeatsPerBar,
        BeatUnitDenominator = note.BeatUnitDenominator,
        HasMeterPosition = note.HasMeterPosition,
        IsPrimaryMelody = note.IsPrimaryMelody,
        WasOctaveShifted = note.WasOctaveShifted,
        OrigString = note.OrigString,
        OrigFret = note.OrigFret,
        VoiceIndex = note.VoiceIndex,
        IsLetRing = note.IsLetRing,
        IsPalmMute = note.IsPalmMute,
        IsBrushChord = note.IsBrushChord,
        IsBrushUp = note.IsBrushUp,
        ExplicitChordName = note.ExplicitChordName,
        SourceFormat = note.SourceFormat
    };
}
