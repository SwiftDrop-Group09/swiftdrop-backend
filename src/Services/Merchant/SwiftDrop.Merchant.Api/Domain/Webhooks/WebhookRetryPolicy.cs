namespace SwiftDrop.Merchant.Api.Domain.Webhooks;

public static class WebhookRetryPolicy
{
    public const int MaxAttempts = 6;
    public static readonly TimeSpan MaxRetryAfterCap = TimeSpan.FromHours(24);

    public static TimeSpan GetScheduledDelay(int nextAttemptNumber) => nextAttemptNumber switch
    {
        2 => TimeSpan.FromMinutes(1),
        3 => TimeSpan.FromMinutes(5),
        4 => TimeSpan.FromMinutes(15),
        5 => TimeSpan.FromMinutes(30),
        6 => TimeSpan.FromMinutes(60),
        _ => TimeSpan.Zero
    };

    public static bool IsRetryableStatusCode(int? statusCode)
    {
        if (!statusCode.HasValue)
        {
            // Network failure or timeout
            return true;
        }

        var code = statusCode.Value;
        return code is 408 or 429 or (>= 500 and <= 599);
    }

    public static TimeSpan CalculateDelay(int nextAttemptNumber, TimeSpan? retryAfter = null)
    {
        var scheduled = GetScheduledDelay(nextAttemptNumber);
        if (!retryAfter.HasValue)
        {
            return scheduled;
        }

        var candidate = retryAfter.Value > scheduled ? retryAfter.Value : scheduled;
        return candidate > MaxRetryAfterCap ? MaxRetryAfterCap : candidate;
    }
}
