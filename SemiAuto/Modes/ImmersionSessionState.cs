namespace MidiAutoPlayer.SemiAuto.Modes;

/// <summary>Tracks manual immersion toggles separately from playback-owned immersion.</summary>
public sealed class ImmersionSessionState
{
    private readonly object _sync = new();
    private bool _isEnabled;
    private bool _isOwnedByAutomaticPlayback;

    public bool IsEnabled { get { lock (_sync) return _isEnabled; } }
    public bool IsOwnedByAutomaticPlayback { get { lock (_sync) return _isOwnedByAutomaticPlayback; } }

    public void ToggleManually()
    {
        lock (_sync) _isEnabled = !_isEnabled;
    }

    public bool StartAutomatic(bool enabled)
    {
        if (!enabled) return false;
        lock (_sync)
        {
            _isEnabled = true;
            _isOwnedByAutomaticPlayback = true;
        }
        return true;
    }

    public bool EndAutomatic()
    {
        lock (_sync)
        {
            if (!_isOwnedByAutomaticPlayback) return false;
            _isOwnedByAutomaticPlayback = false;
            _isEnabled = false;
            return true;
        }
    }
}
