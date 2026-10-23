namespace Nte.GuitarChart.Core;

public sealed record PracticeRules(
    bool ManualPositionSwitch = false,
    string PositionSwitchKey = "Space",
    bool ContinueTimelineOnWrongInput = false,
    bool RequireHoldUntilTargetEnd = true);

public sealed record InputObservation(int GameKey, bool IsKeyDown, long WallClockUs, bool IsAutoRepeat = false);
public sealed record PracticeFeedback(string Kind, string Message, Guid? TargetNoteId = null);

public interface IUserInputObserver
{
    event EventHandler<InputObservation>? InputObserved;
}
