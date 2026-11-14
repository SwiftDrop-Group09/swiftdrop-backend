using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using SwiftDrop.Contracts.Events.V1;
using SwiftDrop.Notification.Worker.Messaging;
using SwiftDrop.Notification.Worker.Persistence;
using SwiftDrop.Notification.Worker.Routing;

namespace SwiftDrop.Notification.Tests;

public sealed class StatusEventAndProviderTests
{
    [Fact]
    public async Task CustomerStatusEventCreatesDurableJobsAndDuplicateIsDeduplicated()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-14T10:00:00Z"));
        var processor = new StatusNotificationProcessor(
            database.Context, clock, NullLogger<StatusNotificationProcessor>.Instance);
        var message = CustomerEvent();

        await processor.ProcessAsync(message, CancellationToken.None);
        await processor.ProcessAsync(message, CancellationToken.None);

        Assert.Equal(1, await database.Context.ConsumedStatusEvents.CountAsync());
        var jobs = await database.Context.NotificationJobs.OrderBy(job => job.Channel).ToListAsync();
        Assert.Equal(["InApp", "MockSms"], jobs.Select(job => job.Channel).ToArray());
        Assert.All(jobs, job => Assert.Equal("PendingRecipientResolution", job.State));
    }

    [Fact]
    public async Task MerchantStatusEventCreatesOnlyWebhookDispatchJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-14T10:00:00Z"));
        var processor = new StatusNotificationProcessor(
            database.Context, clock, NullLogger<StatusNotificationProcessor>.Instance);
        var merchantId = Guid.NewGuid();

        await processor.ProcessAsync(CustomerEvent(merchantId.ToString("D")), CancellationToken.None);

        Assert.Empty(await database.Context.NotificationJobs.ToListAsync());
        var dispatch = Assert.Single(await database.Context.WebhookDispatchJobs.ToListAsync());
        Assert.Equal(merchantId.ToString("D"), dispatch.MerchantId);
        Assert.Equal("Pending", dispatch.State);
    }

    [Fact]
    public async Task OlderParcelVersionIsRecordedButCreatesNoNotificationJobs()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-14T10:00:00Z"));
        var processor = new StatusNotificationProcessor(
            database.Context, clock, NullLogger<StatusNotificationProcessor>.Instance);
        var parcelId = Guid.NewGuid().ToString("D");

        await processor.ProcessAsync(CustomerEvent(parcelId: parcelId, parcelVersion: 4, status: "Delivered"),
            CancellationToken.None);
        await processor.ProcessAsync(CustomerEvent(parcelId: parcelId, parcelVersion: 3, status: "InTransit"),
            CancellationToken.None);

        Assert.Equal(2, await database.Context.ConsumedStatusEvents.CountAsync());
        Assert.Equal(2, await database.Context.NotificationJobs.CountAsync());
        Assert.All(await database.Context.NotificationJobs.ToListAsync(), job =>
            Assert.Equal(4, job.ParcelVersion));
    }

    [Fact]
    public async Task InvalidStatusEventIsRejectedWithoutPersistingAnyEffects()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-14T10:00:00Z"));
        var processor = new StatusNotificationProcessor(
            database.Context, clock, NullLogger<StatusNotificationProcessor>.Instance);
        var invalid = CustomerEvent() with { SchemaVersion = 99 };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            processor.ProcessAsync(invalid, CancellationToken.None));

        Assert.Empty(await database.Context.ConsumedStatusEvents.ToListAsync());
        Assert.Empty(await database.Context.NotificationJobs.ToListAsync());
    }

    [Fact]
    public async Task MockSmsSuccessRecordsOneAttemptAndCompletesJob()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-14T10:00:00Z"));
        var provider = new RecordingProvider(new NotificationProviderResult(true, null));
        var processor = new NotificationProviderJobProcessor(
            provider, clock, NullLogger<NotificationProviderJobProcessor>.Instance);
        var job = MockSmsJob(clock.GetUtcNow());
        database.Context.NotificationJobs.Add(job);
        await database.Context.SaveChangesAsync();

        var didWork = await processor.ProcessClaimedMockSmsAsync(
            database.Context, job.Id, CancellationToken.None);

        Assert.True(didWork);
        var persisted = await database.Context.NotificationJobs.SingleAsync();
        var attempt = Assert.Single(await database.Context.NotificationProviderAttempts.ToListAsync());
        Assert.Equal("Sent", persisted.State);
        Assert.Equal("Succeeded", attempt.Outcome);
        Assert.Equal(1, attempt.AttemptNumber);
        Assert.NotNull(attempt.CompletedAt);
        Assert.Equal(1, provider.Calls);
    }

    [Fact]
    public async Task MockSmsFailureUsesThreeBackoffsThenFailsAfterFourthAttempt()
    {
        await using var database = await TestDatabase.CreateAsync();
        var initial = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        var clock = new ManualTimeProvider(initial);
        var provider = new RecordingProvider(
            new NotificationProviderResult(false, "temporary_failure"),
            new NotificationProviderResult(false, "temporary_failure"),
            new NotificationProviderResult(false, "temporary_failure"),
            new NotificationProviderResult(false, "terminal_failure"));
        var processor = new NotificationProviderJobProcessor(
            provider, clock, NullLogger<NotificationProviderJobProcessor>.Instance);
        database.Context.NotificationJobs.Add(MockSmsJob(initial));
        await database.Context.SaveChangesAsync();

        var expectedBackoffs = new[]
        {
            TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2)
        };
        for (var attemptNumber = 1; attemptNumber <= 4; attemptNumber++)
        {
            await processor.ProcessClaimedMockSmsAsync(
                database.Context, 1, CancellationToken.None);
            var job = await database.Context.NotificationJobs.SingleAsync();
            Assert.Equal(attemptNumber == 4 ? "Failed" : "ReadyForProvider", job.State);
            if (attemptNumber < 4)
            {
                Assert.Equal(clock.GetUtcNow() + expectedBackoffs[attemptNumber - 1], job.NextAttemptAt);
                clock.Advance(expectedBackoffs[attemptNumber - 1]);
            }
            else
            {
                Assert.Null(job.NextAttemptAt);
                Assert.Equal("terminal_failure", job.LastErrorCode);
            }
        }

        var attempts = await database.Context.NotificationProviderAttempts
            .OrderBy(attempt => attempt.AttemptNumber).ToListAsync();
        Assert.Equal(4, attempts.Count);
        Assert.Equal([1, 2, 3, 4], attempts.Select(attempt => attempt.AttemptNumber).ToArray());
        Assert.All(attempts, attempt => Assert.Equal("Failed", attempt.Outcome));
        Assert.Equal(4, provider.Calls);
    }

    [Fact]
    public async Task MockSmsRetryCanRecoverAndPreservesAttemptHistory()
    {
        await using var database = await TestDatabase.CreateAsync();
        var initial = DateTimeOffset.Parse("2026-11-14T10:00:00Z");
        var clock = new ManualTimeProvider(initial);
        var provider = new RecordingProvider(
            new NotificationProviderResult(false, "temporary_failure"),
            new NotificationProviderResult(true, null));
        var processor = new NotificationProviderJobProcessor(
            provider, clock, NullLogger<NotificationProviderJobProcessor>.Instance);
        database.Context.NotificationJobs.Add(MockSmsJob(initial));
        await database.Context.SaveChangesAsync();

        await processor.ProcessClaimedMockSmsAsync(database.Context, 1, CancellationToken.None);
        var failedAttemptJob = await database.Context.NotificationJobs.SingleAsync();
        Assert.Equal("ReadyForProvider", failedAttemptJob.State);
        Assert.Equal(initial + TimeSpan.FromSeconds(5), failedAttemptJob.NextAttemptAt);

        clock.Advance(TimeSpan.FromSeconds(5));
        await processor.ProcessClaimedMockSmsAsync(database.Context, 1, CancellationToken.None);

        var recoveredJob = await database.Context.NotificationJobs.SingleAsync();
        var attempts = await database.Context.NotificationProviderAttempts
            .OrderBy(attempt => attempt.AttemptNumber).ToListAsync();
        Assert.Equal("Sent", recoveredJob.State);
        Assert.Null(recoveredJob.NextAttemptAt);
        Assert.Equal(["Failed", "Succeeded"], attempts.Select(attempt => attempt.Outcome).ToArray());
        Assert.Equal([1, 2], attempts.Select(attempt => attempt.AttemptNumber).ToArray());
    }

    [Fact]
    public async Task InAppRecordIsPersistedWithoutCallingSmsProvider()
    {
        await using var database = await TestDatabase.CreateAsync();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-11-14T10:00:00Z"));
        var provider = new RecordingProvider(new NotificationProviderResult(true, null));
        var processor = new NotificationProviderJobProcessor(
            provider, clock, NullLogger<NotificationProviderJobProcessor>.Instance);
        database.Context.NotificationJobs.Add(new NotificationJob
        {
            EventId = Guid.NewGuid(),
            RecipientId = Guid.NewGuid(),
            Channel = "InApp",
            ParcelId = Guid.NewGuid().ToString("D"),
            ParcelVersion = 2,
            Status = "InTransit",
            CorrelationId = "test-correlation",
            OccurredAt = clock.GetUtcNow(),
            State = "ReadyForProvider",
            CreatedAt = clock.GetUtcNow(),
            UpdatedAt = clock.GetUtcNow()
        });
        await database.Context.SaveChangesAsync();

        await processor.PersistInAppJobsAsync(database.Context, clock.GetUtcNow(), CancellationToken.None);

        Assert.Equal("Persisted", (await database.Context.NotificationJobs.SingleAsync()).State);
        Assert.Empty(await database.Context.NotificationProviderAttempts.ToListAsync());
        Assert.Equal(0, provider.Calls);
    }

    private static StatusChangedV1 CustomerEvent(
        string? merchantId = null,
        string? parcelId = null,
        long parcelVersion = 2,
        string status = "InTransit") => new(
        Guid.NewGuid(),
        StatusChangedV1.CanonicalEventType,
        StatusChangedV1.CurrentSchemaVersion,
        DateTimeOffset.Parse("2026-11-14T09:59:00Z"),
        parcelId ?? Guid.NewGuid().ToString("D"),
        parcelVersion,
        merchantId,
        status,
        status == "Delivered" ? "OutForDelivery" : "PickedUp",
        "test-correlation");

    private static NotificationJob MockSmsJob(DateTimeOffset now) => new()
    {
        EventId = Guid.NewGuid(),
        RecipientId = Guid.NewGuid(),
        Channel = "MockSms",
        ParcelId = Guid.NewGuid().ToString("D"),
        ParcelVersion = 2,
        Status = "Delivered",
        CorrelationId = "test-correlation",
        OccurredAt = now,
        State = "ReadyForProvider",
        CreatedAt = now,
        UpdatedAt = now
    };

    private sealed class RecordingProvider(params NotificationProviderResult[] results) : INotificationProvider
    {
        private readonly Queue<NotificationProviderResult> results = new(results);
        public int Calls { get; private set; }

        public Task<NotificationProviderResult> SendMockSmsAsync(
            long notificationJobId,
            Guid eventId,
            Guid recipientId,
            string status,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            return Task.FromResult(results.Dequeue());
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset current) : TimeProvider
    {
        private DateTimeOffset current = current;
        public override DateTimeOffset GetUtcNow() => current;
        public void Advance(TimeSpan amount) => current += amount;
    }

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection connection;

        private TestDatabase(SqliteConnection connection, NotificationDbContext context)
        {
            this.connection = connection;
            Context = context;
        }

        public NotificationDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<NotificationDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new NotificationDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await connection.DisposeAsync();
        }
    }
}
