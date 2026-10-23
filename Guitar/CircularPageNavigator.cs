namespace MidiAutoPlayer.Guitar;

/// <summary>Finds the shortest route around a mode's page wheel.</summary>
public static class CircularPageNavigator
{
    /// <summary>
    /// Returns the number of taps and direction required to reach a page.
    /// Direction is +1 for next page, -1 for previous page, and 0 when no tap is needed.
    /// Ties prefer +1.
    /// </summary>
    public static (int Steps, int Direction) GetShortestPath(byte currentPage, byte targetPage, int totalPages = 5)
    {
        if (totalPages <= 1) return (0, 0);

        int current = Math.Clamp((int)currentPage, 1, totalPages) - 1;
        int target = Math.Clamp((int)targetPage, 1, totalPages) - 1;
        int forward = (target - current + totalPages) % totalPages;
        int backward = (current - target + totalPages) % totalPages;

        if (forward == 0) return (0, 0);
        return forward <= backward ? (forward, 1) : (backward, -1);
    }

    /// <summary>Returns the page reached after one next (+1) or previous (-1) tap.</summary>
    public static byte StepPage(byte currentPage, int direction, int totalPages = 5)
    {
        if (totalPages <= 1) return 1;
        int current = Math.Clamp((int)currentPage, 1, totalPages) - 1;
        int next = (current + (direction >= 0 ? 1 : -1) + totalPages) % totalPages;
        return (byte)(next + 1);
    }
}
