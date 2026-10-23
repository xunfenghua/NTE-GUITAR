namespace MidiAutoPlayer.Utils;

public static class TimeFormatter
{
    public static string FormatUs(long us)
    {
        if (us < 0) us = 0;
        var ts = TimeSpan.FromMilliseconds(us / 1000.0);
        if (ts.TotalHours >= 1)
        {
            return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
        }
        return $"{(int)ts.TotalMinutes:D2}:{ts.Seconds:D2}";
    }
}
