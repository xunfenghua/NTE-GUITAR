using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Guitar;

public sealed class StrumAnalyzerOptions
{
    public long ClusterWindowUs { get; set; } = 60_000;    // 聚类窗口 60ms (35~80ms)
    public long MinSpanUs { get; set; } = 18_000;          // 首尾最小时间跨度 18ms (文档 7.1)
    public int MinClusterNotes { get; set; } = 3;          // 一簇最少音符数
    public long MinStrumIntervalUs { get; set; } = 60_000; // 最小扫弦物理间隔 60ms
}

/// <summary>
/// 扫弦分析器: 从 MIDI 音符的微小时序中识别扫弦方向 (LowToHigh / HighToLow / Unknown)
/// </summary>
public static class StrumAnalyzer
{
    public static List<StrumEvent> Analyze(
        IReadOnlyList<RawMidiNote> rawNotes,
        StrumAnalyzerOptions? options = null)
    {
        if (rawNotes == null || rawNotes.Count == 0)
            return new List<StrumEvent>();

        options ??= new StrumAnalyzerOptions();

        // 过滤鼓音轨，按发声时间排序
        var notes = rawNotes
            .Where(n => n.Channel != 9)
            .OrderBy(n => n.StartUs)
            .ThenBy(n => n.NoteNumber)
            .ToList();

        if (notes.Count == 0)
            return new List<StrumEvent>();

        // 1. Onset 聚类
        var clusters = ClusterOnsets(notes, options.ClusterWindowUs);

        // 2. 分析每簇的方向
        var results = new List<StrumEvent>(clusters.Count);
        long lastStrumTimeUs = -options.MinStrumIntervalUs;

        foreach (var cluster in clusters)
        {
            if (cluster.Count == 0) continue;

            long clusterTime = cluster[0].StartUs;

            // 过滤过密的重复扫弦
            if (clusterTime - lastStrumTimeUs < options.MinStrumIntervalUs)
            {
                continue;
            }

            var (hint, confidence) = InferDirection(cluster, options.MinSpanUs, options.MinClusterNotes);

            results.Add(new StrumEvent(clusterTime, hint, confidence));
            lastStrumTimeUs = clusterTime;
        }

        return results;
    }

    private static List<List<RawMidiNote>> ClusterOnsets(List<RawMidiNote> notes, long windowUs)
    {
        var clusters = new List<List<RawMidiNote>>();
        if (notes.Count == 0) return clusters;

        var currentCluster = new List<RawMidiNote> { notes[0] };
        long clusterStart = notes[0].StartUs;

        for (int i = 1; i < notes.Count; i++)
        {
            var note = notes[i];
            if (note.StartUs - clusterStart <= windowUs)
            {
                currentCluster.Add(note);
            }
            else
            {
                clusters.Add(currentCluster);
                currentCluster = new List<RawMidiNote> { note };
                clusterStart = note.StartUs;
            }
        }

        if (currentCluster.Count > 0)
        {
            clusters.Add(currentCluster);
        }

        return clusters;
    }

    public static (StrumDirectionHint Hint, float Confidence) InferDirection(
        IReadOnlyList<RawMidiNote> cluster,
        long minSpanUs,
        int minNotes)
    {
        // 去重相同音高 (取最早出现的)
        var distinctNotes = cluster
            .GroupBy(n => n.NoteNumber)
            .Select(g => g.OrderBy(n => n.StartUs).First())
            .OrderBy(n => n.StartUs)
            .ToList();

        if (distinctNotes.Count < minNotes)
        {
            return (StrumDirectionHint.Unknown, 0f);
        }

        long spanUs = distinctNotes[^1].StartUs - distinctNotes[0].StartUs;
        if (spanUs < minSpanUs)
        {
            // 首尾时间差过小，属于同时发声和弦，不伪造方向
            return (StrumDirectionHint.Unknown, 0f);
        }

        int ascCount = 0;
        int descCount = 0;

        for (int i = 0; i < distinctNotes.Count - 1; i++)
        {
            int diff = distinctNotes[i + 1].NoteNumber - distinctNotes[i].NoteNumber;
            if (diff > 0) ascCount++;
            else if (diff < 0) descCount++;
        }

        int totalTransitions = ascCount + descCount;
        if (totalTransitions == 0)
        {
            return (StrumDirectionHint.Unknown, 0f);
        }

        double score = (ascCount - descCount) / (double)totalTransitions;

        if (score >= 0.5)
        {
            return (StrumDirectionHint.LowToHigh, (float)Math.Abs(score));
        }
        else if (score <= -0.5)
        {
            return (StrumDirectionHint.HighToLow, (float)Math.Abs(score));
        }

        return (StrumDirectionHint.Unknown, (float)Math.Abs(score));
    }
}
