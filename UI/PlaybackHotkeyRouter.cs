namespace MidiAutoPlayer.UI;

public enum PlaybackHotkeyTarget
{
    Overlay,
    Piano,
    General
}

public static class PlaybackHotkeyRouter
{
    public static PlaybackHotkeyTarget Resolve(bool overlayVisible, bool pianoPageSelected, bool pianoActive, bool generalActive)
    {
        if (overlayVisible) return PlaybackHotkeyTarget.Overlay;
        if (pianoActive) return PlaybackHotkeyTarget.Piano;
        if (generalActive) return PlaybackHotkeyTarget.General;
        if (pianoPageSelected) return PlaybackHotkeyTarget.Piano;
        return PlaybackHotkeyTarget.General;
    }
}
