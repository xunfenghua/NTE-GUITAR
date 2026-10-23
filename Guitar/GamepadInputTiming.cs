namespace MidiAutoPlayer.Guitar;

/// <summary>Minimum report dwell times for the game's sampled controller input.</summary>
internal static class GamepadInputTiming
{
    // Starting values for the virtual controller output adapter. Validate these
    // against the target game's input sampling before lowering them further.
    public const long StringHoldUs = 25_000;
    public const long DpadNeutralUs = 17_000;
    public const long SameStringReleaseUs = 17_000;
    public const int StrumHoldMs = 50;
    public const long SlotHoldUs = 50_000;
    public const long SlotSelectLeadUs = 32_000;
    public const long PageHoldUs = 50_000;
    public const long PageReleaseGapUs = 35_000;
    public const long MaxLeadLatenessUs = 15_000;
    public const long MaxArpeggioSpreadUs = 90_000;
}
