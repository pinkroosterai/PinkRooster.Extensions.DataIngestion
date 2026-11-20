namespace PinkRooster.Extensions.DataIngestion.Chunkers;

/// <summary>
/// Finds how many consecutive units (rows, lines, prose pieces) fit a token budget, starting from a cheap estimate and
/// confirming the boundary with exact counts. It gives the same answer as adding units one at a time and stopping at
/// the first that does not fit, provided adding a unit never lowers the token count, but costs a couple of exact
/// counts per chunk instead of one per unit.
/// </summary>
internal static class FitSearch
{
    /// <param name="available">The number of units that can be added.</param>
    /// <param name="estimate">The estimated number of units that fit.</param>
    /// <param name="fits">Whether the first <c>count</c> units fit, measured exactly.</param>
    internal static int Count(int available, int estimate, Func<int, bool> fits)
    {
        int count = Math.Clamp(estimate, 0, available);
        int initial = count;
        while (count > 0 && !fits(count))
        {
            count--;
        }

        if (count == initial)
        {
            while (count < available && fits(count + 1))
            {
                count++;
            }
        }

        return count;
    }
}
