using System.Globalization;
using System.Net;
using System.Text;
using Nte.GuitarChart.Core;

namespace MidiAutoPlayer.SemiAuto.Chart;

/// <summary>Exports a compact, event-indexed six-lane chart suitable for printing.</summary>
public static class GuitarChartPdfExporter
{
    private const int EventsPerPage = 24;

    public static string BuildHtml(StoredGuitarChart stored)
    {
        var chart = stored.Chart;
        var notes = chart.Notes
            .Where(n => n.GameKey is >= 1 and <= 6)
            .OrderBy(n => n.StartUs).ThenBy(n => n.GameKey).ToArray();
        var cuesByNoteIndex = new Dictionary<int, PositionEvent>();
        foreach (var cue in chart.PositionEvents)
        {
            int noteIndex = LowerBound(notes, cue.TargetTimeUs);
            if (noteIndex < notes.Length && Math.Abs(notes[noteIndex].StartUs - cue.TargetTimeUs) <= 20_000)
                cuesByNoteIndex[noteIndex] = cue;
        }
        int pageCount = Math.Max(1, (int)Math.Ceiling(notes.Length / (double)EventsPerPage));
        var html = new StringBuilder("<!doctype html><html lang=\"zh-CN\"><head><meta charset=\"utf-8\"><style>");
        html.Append("@page{size:A4 landscape;margin:7mm}*{box-sizing:border-box}body{font-family:'Microsoft YaHei',sans-serif;color:#172033;margin:0}.page{height:194mm;position:relative;break-after:page;page-break-after:always;padding:0 1mm}.page:last-child{break-after:auto;page-break-after:auto}.head{height:22mm;display:flex;align-items:flex-start;justify-content:space-between;border-bottom:1px solid #cbd2df;margin-bottom:2mm}h1{font-size:17pt;line-height:1.2;margin:0 0 1.5mm}.meta{font-size:8.5pt;color:#5a6477}.page-no{text-align:right;color:#5a6477;font-size:9pt}.axis,.lane{display:grid;grid-template-columns:14mm repeat(24,minmax(0,1fr))}.axis{height:10mm;align-items:center;border-bottom:1px solid #aeb8c8}.axis-label{font-size:7pt;color:#667085;text-align:center;white-space:nowrap;overflow:hidden}.axis-label.step{font-weight:700;color:#344054}.axis-label small{display:block;font-size:6pt;font-weight:400}.lanes{height:139mm;border:1px solid #bec7d5}.lane{height:19mm;border-bottom:1px solid #cbd2df;background:linear-gradient(90deg,#f8f9fc,#fff)}.lane.position{height:25mm;background:#f0f3f8;border-bottom:0}.lane-name{grid-column:1;display:flex;align-items:center;justify-content:center;background:#e6eaf1;border-right:1px solid #bec7d5;font-size:10pt;font-weight:700}.cell{position:relative;min-width:0;border-right:1px solid #e0e4eb;display:flex;align-items:center;justify-content:center}.cell:last-child{border-right:0}.note{width:calc(100% - 2px);height:14mm;border-radius:1.5mm;display:flex;flex-direction:column;align-items:center;justify-content:center;color:white;font-weight:700;overflow:hidden}.note .key{font-size:12pt;line-height:1}.note .hold{font-size:5.6pt;font-weight:500;margin-top:1mm;white-space:nowrap}.playable{background:#315bd6}.risk{background:#c87808}.bad{background:#c93748}.position-cue{height:18mm;width:100%;display:flex;flex-direction:column;align-items:center;justify-content:center;border-top:1.4mm solid #7950c8;color:#5637a1;font-size:6.2pt;font-weight:700;line-height:1.2;text-align:center;overflow:hidden}.position-cue small{font-size:5.8pt;font-weight:500}.empty-cue{height:100%;width:100%;border-top:1px solid #d5dbe5}.legend{position:absolute;left:1mm;bottom:0;font-size:7.5pt;color:#596579}.summary{position:absolute;right:1mm;bottom:0;font-size:7.5pt;color:#596579}.warning{color:#b42318;font-weight:700}");
        html.Append("</style></head><body>");

        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            var pageNotes = notes.Skip(pageIndex * EventsPerPage).Take(EventsPerPage).ToArray();
            long startUs = pageNotes.Length == 0 ? 0 : pageNotes[0].StartUs;
            long endUs = pageNotes.Length == 0 ? chart.DurationUs : pageNotes[^1].StartUs;
            html.Append("<section class=\"page\"><header class=\"head\"><div><h1>").Append(E(chart.SourceName))
                .Append("</h1><div class=\"meta\">游戏吉他谱　·　每格一个按键事件　·　时间按演奏顺序排列　·　键位自上而下 1 → 6</div></div><div class=\"page-no\">")
                .Append(FormatTime(startUs)).Append(" – ").Append(FormatTime(endUs)).Append("<br>").Append(pageIndex + 1).Append(" / ").Append(pageCount).Append(" 页</div></header>");

            html.Append("<div class=\"axis\"><div></div>");
            for (int i = 0; i < EventsPerPage; i++)
            {
                if (i >= pageNotes.Length) { html.Append("<div class=\"axis-label\"></div>"); continue; }
                if (i % 4 == 0 || i == pageNotes.Length - 1)
                    html.Append("<div class=\"axis-label step\">").Append(i + 1).Append("<small>").Append(FormatTime(pageNotes[i].StartUs)).Append("</small></div>");
                else html.Append("<div class=\"axis-label\"></div>");
            }
            html.Append("</div><div class=\"lanes\">");

            for (int key = 1; key <= 6; key++)
            {
                html.Append("<div class=\"lane\"><div class=\"lane-name\">键 ").Append(key).Append("</div>");
                for (int i = 0; i < EventsPerPage; i++)
                {
                    html.Append("<div class=\"cell\">");
                    if (i < pageNotes.Length && pageNotes[i].GameKey == key)
                    {
                        var note = pageNotes[i];
                        string status = note.Status switch
                        {
                            FeasibilityStatus.AtRisk => "risk",
                            FeasibilityStatus.Unplayable => "bad",
                            _ => "playable"
                        };
                        html.Append("<div class=\"note ").Append(status).Append("\" title=\"").Append(E(FormatTime(note.StartUs) + " · " + (note.Position?.ToString() ?? "无位置")))
                            .Append("\"><span class=\"key\">").Append(key).Append("</span><span class=\"hold\">").Append(FormatDuration(note.DurationUs)).Append("</span></div>");
                    }
                    html.Append("</div>");
                }
                html.Append("</div>");
            }

            html.Append("<div class=\"lane position\"><div class=\"lane-name\">位置</div>");
            for (int i = 0; i < EventsPerPage; i++)
            {
                PositionEvent? cue = null;
                if (i < pageNotes.Length) cuesByNoteIndex.TryGetValue(pageIndex * EventsPerPage + i, out cue);
                if (cue == null) html.Append("<div class=\"cell\"><div class=\"empty-cue\"></div></div>");
                else html.Append("<div class=\"cell\"><div class=\"position-cue\">→ P").Append(cue.To.Page).Append(" / S").Append(cue.To.Slot)
                    .Append("<small>").Append(cue.To.Fret).Append("品　").Append(FormatTime(cue.PromptTimeUs)).Append("</small></div></div>");
            }
            html.Append("</div></div>");

            html.Append("<div class=\"legend\">蓝：可演奏　橙：切换时间风险　紫：位置变化（P页 / S选槽 / 品位）</div><div class=\"summary\">")
                .Append("本页 ").Append(pageNotes.Length).Append(" 个音符　·　全曲风险 ").Append(chart.AtRiskNoteCount)
                .Append("　不可演奏 ").Append(chart.UnplayableNoteCount);
            if (pageNotes.Length == 0) html.Append("　<span class=\"warning\">本曲无可映射到游戏键 1–6 的音符</span>");
            html.Append("</div></section>");
        }

        return html.Append("</body></html>").ToString();
    }

    private static string E(string value) => WebUtility.HtmlEncode(value ?? string.Empty);
    private static int LowerBound(ChartNote[] notes, long timeUs)
    {
        int low = 0, high = notes.Length;
        while (low < high)
        {
            int mid = low + ((high - low) / 2);
            if (notes[mid].StartUs < timeUs) low = mid + 1;
            else high = mid;
        }
        return low;
    }
    private static string FormatTime(long us)
    {
        var time = TimeSpan.FromMicroseconds(Math.Max(0, us));
        return $"{(long)time.TotalMinutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }
    private static string FormatDuration(long us) => us <= 0 ? "" : us < 1_000_000
        ? $"{Math.Max(1, (int)Math.Round(us / 1000d))} ms"
        : $"{(us / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)} s";
}
