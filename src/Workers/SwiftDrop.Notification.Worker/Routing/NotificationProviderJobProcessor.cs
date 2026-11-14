using Microsoft.EntityFrameworkCore;
using SwiftDrop.Notification.Worker.Persistence;

namespace SwiftDrop.Notification.Worker.Routing;

public sealed class NotificationProviderJobProcessor(
    INotificationProvider provider,
    TimeProvider clock,
    ILogger<NotificationProviderJobProcessor> logger)
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan[] RetryDelays =
    [
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)
    ];
    private const int MaximumAttempts = 4;

    public async Task<bool> ProcessDueAsync(
        NotificationDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var didWork = await PersistInAppJobsAsync(db, now, cancellationToken);
        var candidates = await db.NotificationJobs.AsNoTracking()
            .Where(job => job.Channel == "MockSms" && job.State == "ReadyForProvider" &&
                (job.NextAttemptAt == null || job.NextAttemptAt <= now) &&
                (job.LeaseUntil == null || job.LeaseUntil < now))
            .OrderBy(job => job.CreatedAt)
            .Select(job => job.Id)
            .Take(16)
            .ToListAsync(cancellationToken);

        foreach (var id in candidates)
        {
            didWork = await DeliverMockSmsAsync(db, id, cancellationToken) || didWork;
        }

        return didWork;
    }

    public async Task<bool> PersistInAppJobsAsync(
        NotificationDbContext db,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var jobs = await db.NotificationJobs
            .Where(job => job.Channel == "InApp" && job.State == "ReadyForProvider")
            .Take(64)
            .ToListAsync(cancellationToken);
        var dueJobs = jobs.Where(job => IsDue(job.NextAttemptAt, job.LeaseUntil, now)).ToList();
        foreach (var job in dueJobs)
        {
            job.State = "Persisted";
            job.NextAttemptAt = null;
            job.LeaseUntil = null;
            job.UpdatedAt = now;
        }

        if (dueJobs.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        return dueJobs.Count > 0;
    }

    private async Task<bool> DeliverMockSmsAsync(
        NotificationDbContext db,
        long id,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var leaseUntil = now + LeaseDuration;
        var claimed = await db.NotificationJobs
            .Where(job => job.Id == id && job.Channel == "MockSms" && job.State == "ReadyForProvider" &&
                (job.NextAttemptAt == null || job.NextAttemptAt <= now) &&
                (job.LeaseUntil == null || job.LeaseUntil < now))
            .ExecuteUpdateAsync(update => update.SetProperty(job => job.LeaseUntil, leaseUntil), cancellationToken);
        if (claimed == 0)
        {
            return false;
        }

        return await ProcessClaimedMockSmsAsync(db, id, cancellationToken);
    }

    public async Task<bool> ProcessClaimedMockSmsAsync(
        NotificationDbContext db,
        long id,
        CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();

        var job = await db.NotificationJobs.SingleAsync(item => item.Id == id, cancellationToken);
        var previousAttempts = await db.NotificationProviderAttempts
            .CountAsync(attempt => attempt.NotificationJobId == id, cancellationToken);
        if (previousAttempts >= MaximumAttempts)
        {
            job.State = "Failed";
            job.LastErrorCode ??= "provider_attempts_exhausted";
            job.NextAttemptAt = null;
            job.LeaseUntil = null;
            job.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        if (!job.RecipientId.HasValue)
        {
            job.State = "Failed";
            job.LastErrorCode = "provider_recipient_missing";
            job.NextAttemptAt = null;
            job.LeaseUntil = null;
            job.UpdatedAt = now;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }

        var attempt = new NotificationProviderAttempt
        {
            NotificationJobId = id,
            AttemptNumber = previousAttempts + 1,
            Outcome = "Started",
            StartedAt = now
        };
        db.NotificationProviderAttempts.Add(attempt);
        await db.SaveChangesAsync(cancellationToken);

        NotificationProviderResult result;
        try
        {
            result = await provider.SendMockSmsAsync(
                job.Id, job.EventId, job.RecipientId.Value, job.Status, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Mock SMS provider failed for notification job {JobId}", job.Id);
            result = new NotificationProviderResult(false, "mock_sms_provider_error");
        }

        var completedAt = clock.GetUtcNow();
        attempt.CompletedAt = completedAt;
        attempt.Outcome = result.Succeeded ? "Succeeded" : "Failed";
        attempt.FailureCode = result.Succeeded ? null : SafeFailureCode(result.FailureCode);
        job.LeaseUntil = null;
        job.UpdatedAt = completedAt;

        if (result.Succeeded)
        {
            job.State = "Sent";
            job.LastErrorCode = null;
            job.NextAttemptAt = null;
        }
        else
        {
            job.LastErrorCode = attempt.FailureCode;
            if (attempt.AttemptNumber == MaximumAttempts)
            {
                job.State = "Failed";
                job.NextAttemptAt = null;
            }
            else
            {
                job.NextAttemptAt = completedAt + RetryDelays[attempt.AttemptNumber - 1];
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        if (result.Succeeded)
        {
            logger.LogInformation(
                "Mock SMS notification attempt succeeded for job {JobId}, event {EventId}, attempt {AttemptNumber}",
                job.Id, job.EventId, attempt.AttemptNumber);
        }
        else
        {
            logger.LogWarning(
                "Mock SMS notification attempt failed for job {JobId}, attempt {AttemptNumber}, code {FailureCode}",
                job.Id, attempt.AttemptNumber, attempt.FailureCode);
        }

        return true;
    }

    private static string SafeFailureCode(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 80 &&
        value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')
            ? value
            : "mock_sms_provider_error";

    private static bool IsDue(DateTimeOffset? nextAttemptAt, DateTimeOffset? leaseUntil, DateTimeOffset now) =>
        (nextAttemptAt is null || nextAttemptAt <= now) &&
        (leaseUntil is null || leaseUntil < now);
}
