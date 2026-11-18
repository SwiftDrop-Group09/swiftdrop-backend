namespace SwiftDrop.Merchant.Api.Domain.Orders;

/// <summary>
/// Backoff for transient Delivery failures: 5 s, 30 s, 2 min, 10 min, then hourly
/// for as long as it takes. Jobs are never dropped; after
/// <see cref="AlertThreshold"/> failures the processor should raise an alert.
/// </summary>
public static class InboxJobRetryPolicy
{
    public const int AlertThreshold = 10;

    public static TimeSpan SteadyStateDelay { get; } = TimeSpan.FromHours(1);

    private static readonly TimeSpan[] EscalatingDelays =
    [
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromMinutes(2),
        TimeSpan.FromMinutes(10)
    ];

    /// <summary>Delay before the next attempt, given how many attempts have already failed.</summary>
    public static TimeSpan DelayAfter(int failedAttempts)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(failedAttempts, 1);
        return failedAttempts <= EscalatingDelays.Length
            ? EscalatingDelays[failedAttempts - 1]
            : SteadyStateDelay;
    }

    public static bool ShouldAlert(int failedAttempts) => failedAttempts >= AlertThreshold;
}
