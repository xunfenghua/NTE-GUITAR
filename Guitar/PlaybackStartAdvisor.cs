using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Guitar;

public sealed class PlaybackStartAdvice
{
    public int PlayMode { get; set; } // 1=品格模式, 2=和弦模式
    public byte InitialPage { get; set; } = 1; // 播放器假定的实际起始页
    public byte TargetPage { get; set; } = 1; // 首个音符/和弦所在页
    public string ModeName { get; set; } = string.Empty;
    public string InitialModeName { get; set; } = "吉他品格模式"; // 玩家需要放置的游戏物理起点
    public string PageName { get; set; } = string.Empty;
    public string FirstItemDescription { get; set; } = string.Empty;
    public string SummaryText { get; set; } = string.Empty;

    public override string ToString() => SummaryText;
}

/// <summary>
/// 播放起始状态建议器 (文档 6 节: 播放前自动告诉用户从哪个界面开始)
/// </summary>
public static class PlaybackStartAdvisor
{
    private static readonly string[] RomanNumerals = new string[]
    {
        "Zero",
        "I", "II", "III", "IV", "V", "VI",
        "VII", "VIII", "IX", "X", "XI", "XII",
        "XIII", "XIV", "XV", "XVI", "XVII", "XVIII",
        "XIX", "XX", "XXI", "XXII", "XXIII", "XXIV"
    };

    public static string GetRomanFret(byte fret) =>
        fret < RomanNumerals.Length ? RomanNumerals[fret] : $"{fret}";

    public static string GetFretPageName(byte page) => page switch
    {
        5 => "P5: Zero (0品空弦)",
        1 => "P1: I ~ VI (第1页)",
        2 => "P2: VII ~ XII (第2页)",
        3 => "P3: XIII ~ XVIII (第3页)",
        4 => "P4: XIX ~ XXIV (第4页)",
        _ => $"P{page}"
    };

    public static string GetChordPageName(byte page) => page switch
    {
        1 => "P1: G / Am / Bm / C / D / Em (第1页)",
        2 => "P2: Cmaj7 / C7 / Cm7 / G7 / C9 / Cm9 (第2页)",
        3 => "P3: C5 / D5 / G5 / A5 / Cmaj9 / Cadd9 (第3页)",
        4 => "P4: G/B / Am7/G / D/F# / F/A / A / Dm (第4页)",
        _ => $"P{page}"
    };

    public static PlaybackStartAdvice GetAdvice(
        int playMode,
        IReadOnlyList<FretPlayEvent>? fretEvents,
        IReadOnlyList<ChordChangeEvent>? chordEvents,
        bool recommendDynamicStartPage = false)
    {
        var advice = new PlaybackStartAdvice { PlayMode = playMode };

        if (playMode == 1) // 品格模式
        {
            advice.ModeName = "吉他品格模式";

            var firstValid = fretEvents?.FirstOrDefault(e => !e.IsTimingConflict);
            if (firstValid != null)
            {
                byte targetPage = firstValid.Page > 0
                    ? firstValid.Page
                    : FretConverter.GetPageAndSlot(firstValid.Fret).Page;

                advice.InitialPage = 1;
                advice.TargetPage = targetPage;
                advice.PageName = GetFretPageName(targetPage);

                string romanFret = GetRomanFret(firstValid.Fret);
                int gameDigit = GameScanCodes.GetGameDigit(firstValid.StringNo);
                advice.FirstItemDescription = $"物理{firstValid.StringNo}弦 {romanFret}品，游戏按键{gameDigit}";
            }
            else
            {
                advice.InitialPage = 1;
                advice.TargetPage = 1;
                advice.PageName = GetFretPageName(1);
                advice.FirstItemDescription = "无可用音符";
            }

            advice.SummaryText =
                $"演奏模式: {advice.ModeName}\r\n" +
                $"首音目标页面: {advice.PageName}\r\n" +
                $"第一音: {advice.FirstItemDescription}\r\n" +
                $"请让游戏处于品格模式 P1；开始后程序会自动翻到目标页面并选槽。";
        }
        else // 和弦模式
        {
            advice.ModeName = "吉他和弦模式";

            var firstChord = chordEvents?.FirstOrDefault(c => c.Chord != ChordId.Unknown);
            if (firstChord != null)
            {
                byte targetPage = firstChord.Page > 0 ? firstChord.Page : (ChordAnalyzer.GetDefinition(firstChord.Chord)?.Page ?? 1);
                advice.InitialPage = 1;
                advice.TargetPage = targetPage;
                advice.PageName = GetChordPageName(targetPage);

                var def = ChordAnalyzer.GetDefinition(firstChord.Chord);
                string chordName = def?.Name ?? firstChord.Chord.ToString();
                advice.FirstItemDescription = $"{chordName} 和弦 (Slot {firstChord.Slot})";
            }
            else
            {
                advice.InitialPage = 1;
                advice.TargetPage = 1;
                advice.PageName = GetChordPageName(1);
                advice.FirstItemDescription = "无可用和弦";
            }

            advice.SummaryText =
                $"演奏模式: {advice.ModeName}\r\n" +
                $"首和弦目标页面: {advice.PageName}\r\n" +
                $"第一和弦: {advice.FirstItemDescription}\r\n" +
                $"请让游戏处于品格模式 P1；程序会自动切换到和弦模式，再翻到目标页面并选槽。";
        }

        return advice;
    }
}
