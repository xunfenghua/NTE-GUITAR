using MidiAutoPlayer.Models;

namespace MidiAutoPlayer.Services;

public sealed class FirstRunService
{
    public const int CurrentTutorialVersion = 340; // V3.4.0

    public static bool ShouldShowTutorialOnStartup(AppConfig config)
    {
        if (config.DoNotShowTutorialOnStartup) return false;
        return config.TutorialVersionSeen < CurrentTutorialVersion;
    }

    public static void MarkTutorialSeen(AppConfig config, bool neverShowAgain = false)
    {
        config.TutorialVersionSeen = CurrentTutorialVersion;
        if (neverShowAgain)
        {
            config.DoNotShowTutorialOnStartup = true;
        }
    }
}
