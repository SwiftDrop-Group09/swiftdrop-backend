using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Authentication;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Courier;

internal sealed class StatusTestScenario(PostgresFixture fixture) : IAsyncDisposable
{
    public Guid CourierId { get; private set; }
    public Guid OtherCourierId { get; private set; }
    public Guid ParcelId { get; private set; }
    public StatusClock Clock { get; } = new();
    public QuoteApiFactory Factory { get; private set; } = null!;
    public HttpClient Client { get; private set; } = null!;
    public string Path => $"/api/v1/deliveries/{ParcelId:D}/status";

    public static async Task<StatusTestScenario> CreateAsync(PostgresFixture fixture,
        ParcelStatus source = ParcelStatus.Assigned, bool merchant = false, Guid? courierId = null,
        Action<IServiceCollection>? configure = null)
    {
        var scenario = new StatusTestScenario(fixture) { CourierId = courierId ?? Guid.NewGuid(), OtherCourierId = Guid.NewGuid() };
        await scenario.SeedAsync(source, merchant);
        scenario.Factory = scenario.CreateFactory(configure: configure);
        scenario.Client = scenario.Factory.CreateClient();
        return scenario;
    }

    public DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    public QuoteApiFactory CreateFactory(Guid? actor = null, string role = "Courier", Action<IServiceCollection>? configure = null) =>
        new(fixture.ConnectionString, DeliveryTestAuthentication.Principal((actor ?? CourierId).ToString("D"), role), configureServices: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            configure?.Invoke(services);
        });

    public Task<HttpResponseMessage> UpdateAsync(string status, long expectedVersion, string? key = null,
        string? notes = "private status note", string? reason = null, HttpClient? client = null) =>
        CourierStatusAuthenticationTests.Send(client ?? Client,
            JsonSerializer.Serialize(new CourierStatusInput(status, expectedVersion, notes, reason), new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            key ?? Guid.NewGuid().ToString("D"), Path);

    public async Task<StatusGraph> GraphAsync()
    {
        await using var context = Context();
        return new(await context.Parcels.AsNoTracking().SingleAsync(x => x.Id == ParcelId),
            await context.Assignments.AsNoTracking().Where(x => x.ParcelId == ParcelId).OrderBy(x => x.AssignedAt).ThenBy(x => x.Id).ToArrayAsync(),
            await context.BookingHistory.AsNoTracking().Where(x => x.ParcelId == ParcelId).OrderBy(x => x.Timestamp).ThenBy(x => x.Id).ToArrayAsync(),
            await context.OutboxMessages.FromSqlInterpolated($"SELECT * FROM outbox_messages WHERE payload->>'parcelId' = {ParcelId.ToString("D")}")
                .AsNoTracking().OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArrayAsync(),
            await context.IdempotencyRecords.AsNoTracking().Where(x => x.PrincipalId == CourierId.ToString("D") && x.Operation == CourierStatusValidator.Operation)
                .OrderBy(x => x.CreatedAt).ThenBy(x => x.Id).ToArrayAsync(),
            await context.PriceSnapshots.AsNoTracking().SingleAsync(x => x.ParcelId == ParcelId));
    }

    // A test-only dispatcher writer honoring the documented parcel/version/assignment
    // lock contract. This is not a production allocation API or algorithm.
    public async Task<bool> ReassignAsync(long expectedVersion)
    {
        await using var context = Context();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var parcels = await context.Parcels.FromSqlInterpolated($"SELECT * FROM parcels WHERE id = {ParcelId} FOR UPDATE").ToListAsync();
        var parcel = parcels.Single();
        if (parcel.Version != expectedVersion || parcel.Status != ParcelStatus.Assigned) return false;
        var assignments = await context.Assignments.FromSqlInterpolated(
            $"SELECT *, xmin FROM assignments WHERE parcel_id = {ParcelId} AND status = 'Active' FOR UPDATE").ToListAsync();
        var old = assignments.Single();
        old.MarkReassigned();
        context.Entry(parcel).Property(x => x.Version).CurrentValue = checked(parcel.Version + 1);
        context.Entry(parcel).Property(x => x.UpdatedAt).CurrentValue = Clock.UtcNow;
        await context.SaveChangesAsync();
        context.Assignments.Add(new CourierAssignment(Guid.NewGuid(), ParcelId, OtherCourierId,
            new DateOnly(2026, 10, 28), Clock.UtcNow));
        AddDispatchAudit(context, parcel, ParcelStatus.Assigned, Clock.UtcNow);
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
        return true;
    }

    private async Task SeedAsync(ParcelStatus source, bool merchant)
    {
        await using var context = Context();
        await context.Database.MigrateAsync();
        var createdAt = Clock.UtcNow.AddHours(-1);
        var zone = new DeliveryZone(Guid.NewGuid(), "Status " + Guid.NewGuid().ToString("N"));
        var tier = new PricingTier(Guid.NewGuid(), zone.Id, ParcelSize.Small, "status-test-v1", 450m, 60m);
        if (!await context.Couriers.AnyAsync(x => x.Id == CourierId)) context.Couriers.Add(new Api.Domain.Courier(CourierId, true, true, 20));
        context.Couriers.Add(new Api.Domain.Courier(OtherCourierId, true, true, 20));
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var address = JsonSerializer.Serialize(new NormalizedAddress("Private status street", "Colombo", "00100", zone.Id), json);
        var recipient = JsonSerializer.Serialize(new NormalizedRecipient("Private status recipient", "0000000000"), json);
        var details = new ParcelDetails(ParcelSize.Small, 2.5m, zone.Id, address, null, address, recipient);
        var owner = Guid.NewGuid();
        var parcel = merchant
            ? Parcel.ForMerchant(Guid.NewGuid(), owner, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), details, createdAt)
            : Parcel.ForCustomer(Guid.NewGuid(), owner, Guid.NewGuid().ToString("N"), details, createdAt);
        ParcelId = parcel.Id;
        context.AddRange(zone, tier, parcel, new CourierZone(CourierId, zone.Id), new CourierZone(OtherCourierId, zone.Id),
            new PriceSnapshot(Guid.NewGuid(), parcel.Id, tier, 450m, 90m, 540m, createdAt),
            BookingHistory.Created(Guid.NewGuid(), parcel, owner), OutboxMessage.ParcelCreated(Guid.NewGuid(), parcel, "status-fixture-created"));
        await context.SaveChangesAsync();
        if (source == ParcelStatus.Created) return;
        var assignedAt = createdAt.AddMinutes(1);
        context.Entry(parcel).Property(x => x.Status).CurrentValue = ParcelStatus.Assigned;
        context.Entry(parcel).Property(x => x.Version).CurrentValue = 2;
        context.Entry(parcel).Property(x => x.UpdatedAt).CurrentValue = assignedAt;
        var assignment = new CourierAssignment(Guid.NewGuid(), ParcelId, CourierId, new(2026, 10, 28), assignedAt);
        context.Assignments.Add(assignment);
        AddDispatchAudit(context, parcel, ParcelStatus.Created, assignedAt);
        await context.SaveChangesAsync();
        foreach (var stage in new[] { ParcelStatus.PickedUp, ParcelStatus.InTransit, ParcelStatus.OutForDelivery })
        {
            if (source is ParcelStatus.Assigned or ParcelStatus.Failed) break;
            var previous = parcel.Status;
            parcel.AdvanceCourierStatus(stage, parcel.Version, parcel.UpdatedAt.AddMinutes(1));
            if (stage == ParcelStatus.PickedUp) assignment.MarkPickedUp(parcel.UpdatedAt);
            context.AddRange(BookingHistory.CourierChanged(Guid.NewGuid(), parcel, previous, CourierId, "private seeded stage", null),
                OutboxMessage.CourierChanged(Guid.NewGuid(), parcel, previous, "status-fixture-stage"));
            await context.SaveChangesAsync();
            if (stage == source) break;
        }
        if (source == ParcelStatus.Failed)
        {
            parcel.AdvanceCourierStatus(ParcelStatus.Failed, parcel.Version, parcel.UpdatedAt.AddMinutes(1));
            assignment.MarkFailed();
            context.AddRange(BookingHistory.CourierChanged(Guid.NewGuid(), parcel, ParcelStatus.Assigned, CourierId, null, CourierFailureReason.Other),
                OutboxMessage.CourierChanged(Guid.NewGuid(), parcel, ParcelStatus.Assigned, "status-fixture-failed"));
            await context.SaveChangesAsync();
        }
    }

    internal static void AddDispatchAudit(DeliveryDbContext context, Parcel parcel, ParcelStatus previous, DateTimeOffset at)
    {
        var history = BookingHistory.Created(Guid.NewGuid(), parcel, Guid.NewGuid());
        context.BookingHistory.Add(history);
        context.Entry(history).Property(x => x.FromStatus).CurrentValue = previous;
        context.Entry(history).Property(x => x.ToStatus).CurrentValue = ParcelStatus.Assigned;
        context.Entry(history).Property(x => x.ChangeReason).CurrentValue = "test fixture assignment";
        context.Entry(history).Property(x => x.Timestamp).CurrentValue = at;
        var message = OutboxMessage.ParcelCreated(Guid.NewGuid(), parcel, "status-fixture-assigned");
        var payload = JsonNode.Parse(message.Payload)!;
        payload["status"] = "Assigned";
        payload["previousStatus"] = previous.ToString();
        payload["occurredAt"] = JsonValue.Create(at);
        context.OutboxMessages.Add(message);
        context.Entry(message).Property(x => x.CreatedAt).CurrentValue = at;
        context.Entry(message).Property(x => x.Payload).CurrentValue = payload.ToJsonString();
    }

    public ValueTask DisposeAsync()
    {
        Client.Dispose();
        Factory.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class StatusClock : TimeProvider
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 10, 28, 12, 10, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => UtcNow;
}

internal sealed record StatusGraph(Parcel Parcel, CourierAssignment[] Assignments, BookingHistory[] History,
    OutboxMessage[] Events, IdempotencyRecord[] Keys, PriceSnapshot Snapshot)
{
    public string Signature() => JsonSerializer.Serialize(this);
}
