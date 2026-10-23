using System.Text.Json;

namespace MidiAutoPlayer.SemiAuto.Chart;

public static class GuitarChartStore
{
    // Increment when compiler semantics change so stored charts are regenerated.
    public const int CurrentFormatVersion = 5;
    public const string FileName = "guitar_chart.json";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public static void Save(string songDirectory, StoredGuitarChart stored)
    {
        Directory.CreateDirectory(songDirectory);
        string path = Path.Combine(songDirectory, FileName);
        string tempPath = path + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(stored, JsonOptions));
            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
    }

    public static GuitarChartLoadResult Load(string songDirectory, string songId,
        string sourceHash, string effectiveNotesFingerprint)
    {
        string path = Path.Combine(songDirectory, FileName);
        if (!File.Exists(path)) return new(null, true, "尚未生成游戏吉他谱。");
        try
        {
            var stored = JsonSerializer.Deserialize<StoredGuitarChart>(File.ReadAllText(path), JsonOptions);
            if (stored == null) return new(null, true, "谱面文件为空或格式无效。");
            bool stale = stored.FormatVersion != CurrentFormatVersion ||
                         !string.Equals(stored.SongId, songId, StringComparison.Ordinal) ||
                         !string.Equals(stored.SourceHash, sourceHash, StringComparison.OrdinalIgnoreCase) ||
                         !string.Equals(stored.EffectiveNotesFingerprint, effectiveNotesFingerprint, StringComparison.OrdinalIgnoreCase);
            return stale
                ? new(null, true, "源曲目或有效音符已变更，需要重新生成谱面。")
                : new(stored, false, null);
        }
        catch (Exception ex)
        {
            return new(null, true, $"读取游戏吉他谱失败：{ex.Message}");
        }
    }
}
