using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Application.Orders.Processing;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Orders.Persistence;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;
using static SwiftDrop.Merchant.Tests.Orders.Processing.ProcessingTestData;

namespace SwiftDrop.Merchant.Tests.Orders.Processing;

/// <summary>Batch claiming, leasing and persistence through the real EF queue (in-memory provider).</summary>
public sealed class ProcessDueOrdersHandlerTests : IDisposable
{
    private readonly string _databaseName = $"order-processing-{Guid.NewGuid():N}";
    private readonly ManualTimeProvider _clock = new(Now);
    private readonly MerchantDbContext _database;

    public ProcessDueOrdersHandlerTests() => _database = NewContext();

    [Fact]
    public async Task ProcessesDueJobsAndPersistsOutcome()
    {
        var job = await SeedAsync(Job());

        var claimed = await Handler(new ScriptedDeliveryBookingClient()).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, claimed);
        var stored = await ReloadAsync(job.Id);
        Assert.Equal(InboxJobState.Created, stored.State);
        Assert.NotNull(stored.TrackingNumber);
    }

    [Fact]
    public async Task SkipsJobsThatAreNotDueOrAlreadySettled()
    {
        var notDue = Job(externalOrderId: "ORD-LATER");
        notDue.ScheduleRetry(DeliveryErrorCodes.Timeout, Now);   // due in 5 s
        var failed = Job(externalOrderId: "ORD-FAILED");
        failed.MarkFailed(DeliveryErrorCodes.InvalidOrder, Now);
        await SeedAsync(notDue);
        await SeedAsync(failed);
        var delivery = new ScriptedDeliveryBookingClient();

        var claimed = await Handler(delivery).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(0, claimed);
        Assert.Equal(0, delivery.LookupCalls);
    }

    [Fact]
    public async Task ClaimLeasesJobsSoTheyAreNotClaimedTwice()
    {
        var job = await SeedAsync(Job());
        var queue = new EfInboxJobQueue(_database);

        var first = await queue.ClaimDueAsync(10, TimeSpan.FromMinutes(2), Now, CancellationToken.None);
        var second = await new EfInboxJobQueue(NewContext()).ClaimDueAsync(10, TimeSpan.FromMinutes(2), Now, CancellationToken.None);

        Assert.Single(first);
        Assert.Empty(second);
        Assert.Equal(Now.AddMinutes(2), (await ReloadAsync(job.Id)).NextRetryAt);
    }

    [Fact]
    public async Task ExpiredLease_MakesAnInterruptedJobDueAgain()
    {
        await SeedAsync(Job());
        await new EfInboxJobQueue(_database).ClaimDueAsync(10, TimeSpan.FromMinutes(2), Now, CancellationToken.None);

        var afterLease = await new EfInboxJobQueue(NewContext())
            .ClaimDueAsync(10, TimeSpan.FromMinutes(2), Now.AddMinutes(2), CancellationToken.None);

        Assert.Single(afterLease);
    }

    [Fact]
    public async Task RefusedCredential_StopsTheBatchAndLeavesRemainingJobsLeased()
    {
        var first = await SeedAsync(Job(externalOrderId: "ORD-1", acceptedAt: Now.AddSeconds(-2)));
        var second = await SeedAsync(Job(externalOrderId: "ORD-2", acceptedAt: Now.AddSeconds(-1)));
        var delivery = new ScriptedDeliveryBookingClient()
            .LookupReturns(new BookingCallResult.Unauthorized(DeliveryErrorCodes.Unauthorized));

        await Handler(delivery).ProcessBatchAsync(CancellationToken.None);

        Assert.Equal(1, delivery.LookupCalls);
        Assert.Equal(InboxJobState.Retrying, (await ReloadAsync(first.Id)).State);
        var untouched = await ReloadAsync(second.Id);
        Assert.Equal(InboxJobState.Accepted, untouched.State);
        Assert.Equal(Now.AddMinutes(2), untouched.NextRetryAt);
    }

    [Fact]
    public async Task RespectsBatchSize()
    {
        for (var i = 0; i < 3; i++)
        {
            await SeedAsync(Job(externalOrderId: $"ORD-{i}"));
        }

        var settings = Settings();
        settings.BatchSize = 2;
        var handler = new ProcessDueOrdersHandler(new EfInboxJobQueue(_database),
            Processor(new ScriptedDeliveryBookingClient(), _clock), _clock, Options.Create(settings));

        Assert.Equal(2, await handler.ProcessBatchAsync(CancellationToken.None));
    }

    public void Dispose() => _database.Dispose();

    private ProcessDueOrdersHandler Handler(IDeliveryBookingClient delivery) =>
        new(new EfInboxJobQueue(_database), Processor(delivery, _clock), _clock, Options.Create(Settings()));

    private MerchantDbContext NewContext() => new(new DbContextOptionsBuilder<MerchantDbContext>()
        .UseInMemoryDatabase(_databaseName).Options);

    private async Task<InboxJob> SeedAsync(InboxJob job)
    {
        await using var context = NewContext();
        context.InboxJobs.Add(job);
        await context.SaveChangesAsync();
        return job;
    }

    private async Task<InboxJob> ReloadAsync(Guid id)
    {
        await using var context = NewContext();
        return await context.InboxJobs.SingleAsync(job => job.Id == id);
    }
}
