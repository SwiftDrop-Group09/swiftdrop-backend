using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Application.Orders.Processing;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Orders.Persistence;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;
using static SwiftDrop.Merchant.Tests.Orders.Processing.ProcessingTestData;

namespace SwiftDrop.Merchant.Tests.Orders.Processing;

public sealed class StatusRefreshAndResolverTests : IDisposable
{
    private readonly string _databaseName = $"status-refresh-{Guid.NewGuid():N}";
    private readonly ManualTimeProvider _clock = new(Now);
    private readonly MerchantDbContext _database;
    private readonly Guid _businessId = Guid.NewGuid();

    public StatusRefreshAndResolverTests() => _database = NewContext();

    // ── Status refresh loop ────────────────────────────────────────────────

    [Fact]
    public async Task StaleOrder_GetsTheNewerParcelStatus()
    {
        var job = await SeedAsync(CreatedJob(_businessId, "ORD-1", Now.AddHours(-1), ParcelStatuses.Created, 1,
            refreshedAt: Now.AddMinutes(-10)));
        var delivery = new ScriptedDeliveryBookingClient().LookupReturns(Found(ParcelStatuses.InTransit, version: 4));

        var checkedCount = await Handler(delivery).RefreshBatchAsync(CancellationToken.None);

        Assert.Equal(1, checkedCount);
        var stored = await ReloadAsync(job.Id);
        Assert.Equal(ParcelStatuses.InTransit, stored.ParcelStatus);
        Assert.Equal(4, stored.ParcelVersion);
        Assert.Equal(Now, stored.LastRefreshedAt);
    }

    [Fact]
    public async Task OlderAnswerFromDelivery_NeverMovesTheParcelBackwards()
    {
        var job = await SeedAsync(CreatedJob(_businessId, "ORD-1", Now.AddHours(-1), ParcelStatuses.OutForDelivery, 5,
            refreshedAt: Now.AddMinutes(-10)));
        var delivery = new ScriptedDeliveryBookingClient().LookupReturns(Found(ParcelStatuses.Assigned, version: 2));

        await Handler(delivery).RefreshBatchAsync(CancellationToken.None);

        var stored = await ReloadAsync(job.Id);
        Assert.Equal(ParcelStatuses.OutForDelivery, stored.ParcelStatus);
        Assert.Equal(Now, stored.LastRefreshedAt);
    }

    [Fact]
    public async Task FreshTerminalAndUnfinishedOrders_AreNotPolled()
    {
        await SeedAsync(CreatedJob(_businessId, "FRESH", Now.AddHours(-1), ParcelStatuses.Assigned, 2, refreshedAt: Now.AddMinutes(-1)));
        await SeedAsync(CreatedJob(_businessId, "DONE", Now.AddHours(-1), ParcelStatuses.Delivered, 6, Now.AddMinutes(-30),
            refreshedAt: Now.AddHours(-1)));
        await SeedAsync(Job(_businessId, "WAITING", Now.AddHours(-1)));
        var delivery = new ScriptedDeliveryBookingClient();

        var checkedCount = await Handler(delivery).RefreshBatchAsync(CancellationToken.None);

        Assert.Equal(0, checkedCount);
        Assert.Equal(0, delivery.LookupCalls);
    }

    [Theory]
    [InlineData("transient")]
    [InlineData("notfound")]
    public async Task FailedRefresh_DoesNotClaimFreshness(string failure)
    {
        var refreshedAt = Now.AddMinutes(-10);
        var job = await SeedAsync(CreatedJob(_businessId, "ORD-1", Now.AddHours(-1), ParcelStatuses.Assigned, 2, refreshedAt: refreshedAt));
        BookingCallResult result = failure == "transient"
            ? new BookingCallResult.Transient(DeliveryErrorCodes.Unavailable)
            : new BookingCallResult.NotFound();

        await Handler(new ScriptedDeliveryBookingClient().LookupReturns(result)).RefreshBatchAsync(CancellationToken.None);

        Assert.Equal(refreshedAt, (await ReloadAsync(job.Id)).LastRefreshedAt);
    }

    [Fact]
    public async Task RefusedCredential_StopsTheBatch()
    {
        await SeedAsync(CreatedJob(_businessId, "ORD-1", Now.AddHours(-1), ParcelStatuses.Assigned, 2, refreshedAt: Now.AddMinutes(-20)));
        await SeedAsync(CreatedJob(_businessId, "ORD-2", Now.AddHours(-1), ParcelStatuses.Assigned, 2, refreshedAt: Now.AddMinutes(-10)));
        var delivery = new ScriptedDeliveryBookingClient()
            .LookupReturns(new BookingCallResult.Unauthorized(DeliveryErrorCodes.Unauthorized));

        await Handler(delivery).RefreshBatchAsync(CancellationToken.None);

        Assert.Equal(1, delivery.LookupCalls);
    }

    // ── Webhook order resolver ─────────────────────────────────────────────

    [Fact]
    public async Task Resolver_ReturnsTrackingNumberForOwnParcelOnly()
    {
        var job = await SeedAsync(CreatedJob(_businessId, "ORD-1", Now, ParcelStatuses.Created));
        var resolver = new InboxJobWebhookOrderResolver(NewContext());

        var own = await resolver.TryResolveTrackingNumberAsync(job.ParcelId!.Value, _businessId, CancellationToken.None);
        var foreign = await resolver.TryResolveTrackingNumberAsync(job.ParcelId!.Value, Guid.NewGuid(), CancellationToken.None);
        var unknown = await resolver.TryResolveTrackingNumberAsync(Guid.NewGuid(), _businessId, CancellationToken.None);

        Assert.Equal("SD-ORD-1", own);
        Assert.Null(foreign);
        Assert.Null(unknown);
    }

    public void Dispose() => _database.Dispose();

    private static BookingCallResult.Found Found(string status, long version) => new(new BookingConfirmation(
        Guid.NewGuid(), "SD-ANY", new ParcelStatusSnapshot(status, version, Now.AddMinutes(-2)), 540m, "LKR", "2026-11-v1"));

    private RefreshOrderStatusesHandler Handler(IDeliveryBookingClient delivery) =>
        new(new EfInboxJobQueue(_database), delivery, _clock, Options.Create(Settings()),
            NullLogger<RefreshOrderStatusesHandler>.Instance);

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
