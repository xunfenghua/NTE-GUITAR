using System.Text.Json;
using MidiAutoPlayer.Models;
using MidiAutoPlayer.SemiAuto.Chart;

namespace MidiAutoPlayer.Library;

public sealed class SongLibrary
{
    public static string LibraryDirectory
    {
        get
        {
            string newPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "NTE-GUITAR", "library");
            string oldPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "MidiAutoPlayer", "library");

            if (!Directory.Exists(newPath) && Directory.Exists(oldPath))
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(newPath)!);
                    Directory.Move(oldPath, newPath);
                }
                catch { }
            }
            return newPath;
        }
    }

    private readonly string? _customLibraryDir;
    public string CurrentLibraryDirectory => _customLibraryDir ?? LibraryDirectory;
    private string IndexFilePath => Path.Combine(CurrentLibraryDirectory, "index.json");

    private readonly List<SongMeta> _cachedIndex = new();
    private readonly object _lock = new();

    public SongLibrary(string? customLibraryDir = null)
    {
        _customLibraryDir = customLibraryDir;
        Directory.CreateDirectory(CurrentLibraryDirectory);
        RecoverSongDirectoryTransactions();
        LoadIndex();
    }

    public static int ResolveInstrument(SongMeta meta)
    {
        if (meta.TargetInstrument == LibraryInstrument.Keyboard || meta.Options?.TargetInstrument == LibraryInstrument.Keyboard)
            return LibraryInstrument.Keyboard;
        return meta.TargetInstrument;
    }

    public static int ResolveInstrument(ConvertedSong song)
    {
        if (song.TargetInstrument == LibraryInstrument.Keyboard || song.Options?.TargetInstrument == LibraryInstrument.Keyboard)
            return LibraryInstrument.Keyboard;
        return song.TargetInstrument;
    }

    public IReadOnlyList<SongMeta> GetAllSongs()
    {
        lock (_lock)
        {
            return _cachedIndex.OrderByDescending(s => s.CreatedAt).ToList();
        }
    }

    public IReadOnlyList<SongMeta> GetSongsByInstrument(int instrument)
    {
        lock (_lock)
        {
            return _cachedIndex
                .Where(s => ResolveInstrument(s) == instrument)
                .OrderByDescending(s => s.CreatedAt)
                .ToList();
        }
    }

    public int GetSongCount(int instrument)
    {
        lock (_lock)
        {
            return _cachedIndex.Count(s => ResolveInstrument(s) == instrument);
        }
    }

    public ConvertedSong LoadSong(string id)
    {
        string songDir = ResolveSongDirectory(id);
        return SongSerializer.LoadSong(songDir);
    }

    public void SaveGuitarChart(string songId, StoredGuitarChart chart)
    {
        if (!string.Equals(chart.SongId, songId, StringComparison.Ordinal))
            throw new ArgumentException("谱面曲目 ID 与目标曲库记录不一致。", nameof(chart));
        GuitarChartStore.Save(ResolveSongDirectory(songId), chart);
    }

    public GuitarChartLoadResult LoadGuitarChart(string songId, string sourceHash, string effectiveNotesFingerprint)
    {
        return GuitarChartStore.Load(ResolveSongDirectory(songId), songId, sourceHash, effectiveNotesFingerprint);
    }

    private string ResolveSongDirectory(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            id.Contains('/') || id.Contains('\\'))
            throw new ArgumentException("无效的曲目 ID。", nameof(id));
        lock (_lock)
        {
            if (!_cachedIndex.Any(song => string.Equals(song.Id, id, StringComparison.Ordinal)))
                throw new FileNotFoundException("曲库中不存在该曲目。", id);
        }
        string root = Path.GetFullPath(CurrentLibraryDirectory);
        string directory = Path.GetFullPath(Path.Combine(root, id));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("曲目路径超出曲库目录。");
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("曲目目录不存在。");
        return directory;
    }

    public void SaveSong(ConvertedSong song)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(song.Id))
                song.Id = Guid.NewGuid().ToString("N");

            string songDir = Path.Combine(CurrentLibraryDirectory, song.Id);
            SongSerializer.SaveSong(songDir, song);

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
                RecognitionModeName = song.RecognitionModeName
            };

            int idx = _cachedIndex.FindIndex(s => s.Id == song.Id);
            if (idx >= 0)
                _cachedIndex[idx] = meta;
            else
                _cachedIndex.Add(meta);

            PersistIndex();
        }
    }

    /// <summary>Creates a song directory and its index entry as one rollback-safe operation.</summary>
    public void SaveSongWithAssets(ConvertedSong song, Action<string> writeAssets)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(writeAssets);
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(song.Id)) song.Id = Guid.NewGuid().ToString("N");
            string root = Path.GetFullPath(CurrentLibraryDirectory);
            string finalDirectory = Path.GetFullPath(Path.Combine(root, song.Id));
            if (!finalDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid song ID.", nameof(song));
            if (_cachedIndex.Any(item => item.Id == song.Id) || Directory.Exists(finalDirectory))
                throw new IOException("The target song already exists.");

            string stagedDirectory = Path.Combine(root, ".song-stage-" + Guid.NewGuid().ToString("N"));
            bool movedToFinal = false;
            try
            {
                SongSerializer.SaveSong(stagedDirectory, song);
                writeAssets(stagedDirectory);
                Directory.Move(stagedDirectory, finalDirectory);
                movedToFinal = true;

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
                    RecognitionModeName = song.RecognitionModeName
                };
                _cachedIndex.Add(meta);
                try
                {
                    PersistIndexStrict();
                }
                catch
                {
                    _cachedIndex.RemoveAll(item => item.Id == song.Id);
                    throw;
                }
            }
            catch
            {
                try
                {
                    if (Directory.Exists(stagedDirectory)) Directory.Delete(stagedDirectory, recursive: true);
                    if (movedToFinal && Directory.Exists(finalDirectory)) Directory.Delete(finalDirectory, recursive: true);
                }
                catch { }
                throw;
            }
        }
    }

    /// <summary>Replaces an existing song and its assets as a rollback-safe transaction.</summary>
    public void UpdateSongWithAssets(ConvertedSong song, Action<string> writeAssets)
    {
        ArgumentNullException.ThrowIfNull(song);
        ArgumentNullException.ThrowIfNull(writeAssets);
        lock (_lock)
        {
            int index = _cachedIndex.FindIndex(item => item.Id == song.Id);
            if (index < 0) throw new FileNotFoundException("The song does not exist in the library.", song.Id);

            string root = Path.GetFullPath(CurrentLibraryDirectory);
            string finalDirectory = Path.GetFullPath(Path.Combine(root, song.Id));
            if (!finalDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Invalid song ID.", nameof(song));
            if (!Directory.Exists(finalDirectory)) throw new DirectoryNotFoundException("Song directory does not exist.");

            string stagedDirectory = Path.Combine(root, ".song-stage-" + Guid.NewGuid().ToString("N"));
            string backupDirectory = Path.Combine(root, ".song-backup-" + Guid.NewGuid().ToString("N"));
            var previousMeta = _cachedIndex[index];
            bool originalMoved = false;
            bool stagedMoved = false;
            try
            {
                CopyDirectory(finalDirectory, stagedDirectory);
                SongSerializer.SaveSong(stagedDirectory, song);
                writeAssets(stagedDirectory);

                Directory.Move(finalDirectory, backupDirectory);
                originalMoved = true;
                Directory.Move(stagedDirectory, finalDirectory);
                stagedMoved = true;

                _cachedIndex[index] = CreateMeta(song);
                PersistIndexStrict();
                try { Directory.Delete(backupDirectory, recursive: true); } catch { }
            }
            catch
            {
                _cachedIndex[index] = previousMeta;
                try
                {
                    if (stagedMoved && Directory.Exists(finalDirectory)) Directory.Delete(finalDirectory, recursive: true);
                    if (originalMoved && Directory.Exists(backupDirectory)) Directory.Move(backupDirectory, finalDirectory);
                    if (Directory.Exists(stagedDirectory)) Directory.Delete(stagedDirectory, recursive: true);
                    PersistIndexStrict();
                }
                catch { }
                throw;
            }
        }
    }

    private static SongMeta CreateMeta(ConvertedSong song) => new()
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
        RecognitionModeName = song.RecognitionModeName
    };

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (string directory in Directory.GetDirectories(source))
            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
    }

    public bool RenameSong(string id, string newDisplayName)
    {
        lock (_lock)
        {
            int idx = _cachedIndex.FindIndex(s => s.Id == id);
            if (idx < 0) return false;

            string songDir = Path.Combine(CurrentLibraryDirectory, id);
            string metaPath = Path.Combine(songDir, "meta.json");
            if (!File.Exists(metaPath)) return false;

            var meta = SongSerializer.LoadMetaOnly(songDir);
            if (meta == null) return false;

            meta.DisplayName = newDisplayName;
            string metaTmp = metaPath + ".tmp";
            string json = JsonSerializer.Serialize(meta, SongSerializer.JsonOptions);
            File.WriteAllText(metaTmp, json);
            File.Move(metaTmp, metaPath, overwrite: true);

            _cachedIndex[idx].DisplayName = newDisplayName;
            PersistIndex();
            return true;
        }
    }

    public bool DeleteSong(string id)
    {
        lock (_lock)
        {
            int idx = _cachedIndex.FindIndex(s => s.Id == id);
            if (idx >= 0)
            {
                _cachedIndex.RemoveAt(idx);
                PersistIndex();
            }

            string songDir = Path.Combine(CurrentLibraryDirectory, id);
            if (Directory.Exists(songDir))
            {
                try
                {
                    Directory.Delete(songDir, recursive: true);
                }
                catch
                {
                    // If directory is locked temporarily, index is already updated
                    return false;
                }
            }
            return true;
        }
    }

    private void LoadIndex()
    {
        lock (_lock)
        {
            _cachedIndex.Clear();
            bool loadedSuccessfully = false;

            if (File.Exists(IndexFilePath))
            {
                try
                {
                    string json = File.ReadAllText(IndexFilePath);
                    var list = JsonSerializer.Deserialize<List<SongMeta>>(json, SongSerializer.JsonOptions);
                    if (list != null)
                    {
                        // Verify folders exist and refresh stale index metadata from the committed song folder.
                        var validItems = new List<SongMeta>();
                        bool shouldPersistIndex = false;
                        foreach (var item in list.OrderByDescending(s => s.CreatedAt))
                        {
                            string songDir = Path.Combine(CurrentLibraryDirectory, item.Id);
                            if (!Directory.Exists(songDir) || !File.Exists(Path.Combine(songDir, "song.bin"))) continue;

                            var actualMeta = SongSerializer.LoadMetaOnly(songDir);
                            var current = actualMeta is not null && string.Equals(actualMeta.Id, item.Id, StringComparison.Ordinal)
                                ? actualMeta
                                : item;
                            if (!ReferenceEquals(current, item) && !string.Equals(
                                    JsonSerializer.Serialize(item, SongSerializer.JsonOptions),
                                    JsonSerializer.Serialize(current, SongSerializer.JsonOptions),
                                    StringComparison.Ordinal))
                                shouldPersistIndex = true;

                            int inst = ResolveInstrument(current);
                            bool duplicate = validItems.Any(existing =>
                                ResolveInstrument(existing) == inst &&
                                (!string.IsNullOrEmpty(existing.SourceHash) && existing.SourceHash == current.SourceHash ||
                                 (!string.IsNullOrEmpty(existing.SourceName) && string.Equals(existing.SourceName, current.SourceName, StringComparison.OrdinalIgnoreCase) && string.Equals(existing.DisplayName, current.DisplayName, StringComparison.OrdinalIgnoreCase))));

                            if (!duplicate)
                            {
                                validItems.Add(current);
                            }
                            else
                            {
                                shouldPersistIndex = true;
                                try
                                {
                                    Directory.Delete(songDir, recursive: true);
                                }
                                catch { }
                            }
                        }

                        _cachedIndex.AddRange(validItems);
                        loadedSuccessfully = true;

                        MergeUnindexedSongFolders(shouldPersistIndex);
                        if (shouldPersistIndex) PersistIndex();
                    }
                }
                catch
                {
                    loadedSuccessfully = false;
                }
            }

            // Self-healing: if index file missing or corrupted, rebuild from directory scan
            if (!loadedSuccessfully || _cachedIndex.Count == 0)
            {
                RebuildIndexFromFolders();
            }
        }
    }

    private void RebuildIndexFromFolders()
    {
        try
        {
            var dirs = Directory.GetDirectories(CurrentLibraryDirectory);
            foreach (var dir in dirs)
            {
                if (IsTransactionArtifactDirectory(dir))
                    continue;
                var meta = SongSerializer.LoadMetaOnly(dir);
                if (meta != null && IsSafeSongId(meta.Id) && File.Exists(Path.Combine(dir, "song.bin")))
                {
                    if (!_cachedIndex.Any(s => s.Id == meta.Id))
                    {
                        _cachedIndex.Add(meta);
                    }
                }
            }
            PersistIndex();
        }
        catch
        {
            // Ignore directory scan errors
        }
    }

    private void MergeUnindexedSongFolders(bool shouldPersistIndex)
    {
        try
        {
            foreach (string directory in Directory.GetDirectories(CurrentLibraryDirectory))
            {
                if (IsTransactionArtifactDirectory(directory)) continue;
                var meta = SongSerializer.LoadMetaOnly(directory);
                if (meta is null || string.IsNullOrWhiteSpace(meta.Id) ||
                    !string.Equals(Path.GetFileName(directory), meta.Id, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(Path.Combine(directory, "song.bin")) ||
                    _cachedIndex.Any(item => string.Equals(item.Id, meta.Id, StringComparison.Ordinal)))
                    continue;

                bool duplicate = _cachedIndex.Any(existing =>
                    ResolveInstrument(existing) == ResolveInstrument(meta) &&
                    ((!string.IsNullOrEmpty(existing.SourceHash) && existing.SourceHash == meta.SourceHash) ||
                     (!string.IsNullOrEmpty(existing.SourceName) &&
                      string.Equals(existing.SourceName, meta.SourceName, StringComparison.OrdinalIgnoreCase) &&
                      string.Equals(existing.DisplayName, meta.DisplayName, StringComparison.OrdinalIgnoreCase))));
                if (duplicate) continue;
                _cachedIndex.Add(meta);
                shouldPersistIndex = true;
            }
            if (shouldPersistIndex) PersistIndex();
        }
        catch { }
    }

    private void RecoverSongDirectoryTransactions()
    {
        lock (_lock)
        {
            string root = Path.GetFullPath(CurrentLibraryDirectory);
            var stages = Directory.GetDirectories(root, ".song-stage-*", SearchOption.TopDirectoryOnly);
            var backups = Directory.GetDirectories(root, ".song-backup-*", SearchOption.TopDirectoryOnly);
            var groups = stages.Concat(backups)
                .Select(path => (Path: path, Meta: SongSerializer.LoadMetaOnly(path)))
                .Where(item => item.Meta is not null && IsSafeSongId(item.Meta.Id))
                .GroupBy(item => item.Meta!.Id, StringComparer.Ordinal)
                .OrderBy(group => group.Key, StringComparer.Ordinal);

            foreach (var group in groups)
            {
                string id = group.Key;
                string finalDirectory = Path.GetFullPath(Path.Combine(root, id));
                if (!finalDirectory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                var artifacts = group.OrderBy(item => item.Path, StringComparer.Ordinal).ToList();
                var stageCandidates = artifacts.Where(item => Path.GetFileName(item.Path).StartsWith(".song-stage-", StringComparison.OrdinalIgnoreCase))
                    .Where(item => IsValidSongDirectory(item.Path, id))
                    .OrderByDescending(item => Directory.GetLastWriteTimeUtc(item.Path))
                    .ThenBy(item => item.Path, StringComparer.Ordinal).ToList();
                var backupCandidates = artifacts.Where(item => Path.GetFileName(item.Path).StartsWith(".song-backup-", StringComparison.OrdinalIgnoreCase))
                    .Where(item => IsValidSongDirectory(item.Path, id))
                    .OrderByDescending(item => Directory.GetLastWriteTimeUtc(item.Path))
                    .ThenBy(item => item.Path, StringComparer.Ordinal).ToList();

                if (!Directory.Exists(finalDirectory))
                {
                    // A complete stage represents the intended replacement; otherwise recover the old folder.
                    string? restorePath = stageCandidates.Select(item => item.Path).FirstOrDefault()
                        ?? backupCandidates.Select(item => item.Path).FirstOrDefault();
                    if (restorePath is not null)
                    {
                        try
                        {
                            Directory.Move(restorePath, finalDirectory);
                        }
                        catch { continue; }
                    }
                }

                if (Directory.Exists(finalDirectory) && IsValidSongDirectory(finalDirectory, id))
                {
                    foreach (var artifact in artifacts)
                    {
                        if (!Directory.Exists(artifact.Path)) continue;
                        try { Directory.Delete(artifact.Path, recursive: true); } catch { }
                    }
                }
                else if (!Directory.Exists(finalDirectory))
                {
                    // Incomplete stage folders are disposable; invalid backups are retained for manual recovery.
                    foreach (var artifact in artifacts.Where(item => Path.GetFileName(item.Path).StartsWith(".song-stage-", StringComparison.OrdinalIgnoreCase)))
                        try { if (Directory.Exists(artifact.Path)) Directory.Delete(artifact.Path, recursive: true); } catch { }
                }
            }

            // Stage directories are private temporary artifacts; any survivors are stale or incomplete.
            foreach (string stage in stages)
                try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    private static bool IsValidSongDirectory(string directory, string expectedId)
    {
        if (!File.Exists(Path.Combine(directory, "song.bin"))) return false;
        var meta = SongSerializer.LoadMetaOnly(directory);
        return meta is not null && string.Equals(meta.Id, expectedId, StringComparison.Ordinal);
    }

    private static bool IsSafeSongId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 &&
        !id.Contains('/') && !id.Contains('\\') && id is not ("." or "..");

    private static bool IsTransactionArtifactDirectory(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith(".song-stage-", StringComparison.OrdinalIgnoreCase) ||
               name.StartsWith(".song-backup-", StringComparison.OrdinalIgnoreCase);
    }

    private void PersistIndex()
    {
        try
        {
            string tmpPath = IndexFilePath + ".tmp";
            string json = JsonSerializer.Serialize(_cachedIndex, SongSerializer.JsonOptions);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, IndexFilePath, overwrite: true);
        }
        catch
        {
            // Non-fatal if index write fails temporarily
        }
    }

    private void PersistIndexStrict()
    {
        string tmpPath = IndexFilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            string json = JsonSerializer.Serialize(_cachedIndex, SongSerializer.JsonOptions);
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, IndexFilePath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(tmpPath)) File.Delete(tmpPath); } catch { }
        }
    }
}
