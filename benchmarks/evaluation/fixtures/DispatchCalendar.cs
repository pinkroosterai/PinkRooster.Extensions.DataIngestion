namespace Evaluation.Fulfillment;

public interface IDispatchClock
{
    DateOnly Today { get; }
}

public sealed class DispatchCalendar(IDispatchClock clock)
{
    private const int StandardDispatchDays = 2;

    public DateOnly GetDispatchDate(bool express)
    {
        int days = express ? 0 : StandardDispatchDays;
        return clock.Today.AddDays(days);
    }
}
