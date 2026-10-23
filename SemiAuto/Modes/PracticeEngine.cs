namespace Nte.GuitarChart.Core;

public sealed class PracticeEngine
{
    public PracticeRules Rules { get; }

    public PracticeEngine(PracticeRules? rules = null)
    {
        Rules = rules ?? new PracticeRules();
    }

    public PracticeFeedback EvaluateKey(int expectedGameKey, int actualGameKey, Guid targetNoteId)
    {
        if (expectedGameKey == actualGameKey)
        {
            return new PracticeFeedback("correct", "正确", targetNoteId);
        }

        return new PracticeFeedback(
            "wrong",
            $"按错了，当前需要游戏键 {expectedGameKey}，你按下了 {actualGameKey}。",
            targetNoteId);
    }

    public double EvaluateHoldCompletion(long actualHoldUs, long targetDurationUs)
    {
        if (targetDurationUs <= 0) return 1.0;
        return Math.Clamp((double)actualHoldUs / targetDurationUs, 0.0, 1.0);
    }

}
