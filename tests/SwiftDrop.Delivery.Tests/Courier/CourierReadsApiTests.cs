using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.DeliveryReads;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Authentication;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class CourierReadsApiTests(PostgresFixture fixture) : IAsyncLifetime
{
    private const string Manifest = "/api/v1/couriers/me/assignments";
    private static readonly DateTimeOffset Now = QuoteApiFactory.Now;
    private static readonly DateOnly Today = DateOnly.FromDateTime(Now.UtcDateTime);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync()
    {
        await using var context = Context();
        await context.Database.MigrateAsync();
    }
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task RealCustomerBookingLocationResolvesToTheSameOwnerBoundParcel()
    {
        var seed = await SeedAsync();
        using var factory = Factory(seed.OwnerId, "Customer");
        using var client = factory.CreateClient();
        var input = new QuoteInput(new AddressInput("Fictional pickup street", "Colombo", "00100", seed.ZoneId.ToString()),
            new AddressInput("Fictional delivery street", "Kandy", "20000", seed.ZoneId.ToString()),
            new PackageDetailsInput(2.5m, "Small"), new RecipientInput("Fictional Recipient", "0000000000"));
        using var estimate = await client.PostAsJsonAsync("/api/v1/quotes", input);
        Assert.Equal(HttpStatusCode.OK, estimate.StatusCode);
        using var quote = JsonDocument.Parse(await estimate.Content.ReadAsStringAsync());
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/parcels")
        {
            Content = JsonContent.Create(new ConfirmInput(quote.RootElement.GetProperty("quoteId").GetString(),
                quote.RootElement.GetProperty("total").GetDecimal()))
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString());
        using var confirmed = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, confirmed.StatusCode);
        using var created = JsonDocument.Parse(await confirmed.Content.ReadAsStringAsync());
        var before = await StateAsync();
        using var detail = await GetJsonAsync(client, confirmed.Headers.Location!.OriginalString);
        foreach (var field in new[] { "id", "trackingNumber", "status", "version", "priceSnapshot", "createdAt" })
            Assert.True(JsonElement.DeepEquals(created.RootElement.GetProperty(field), detail.RootElement.GetProperty(field)));
        Assert.Single(detail.RootElement.GetProperty("history").EnumerateArray());
        Assert.Equal("Fictional pickup street", detail.RootElement.GetProperty("pickupAddress").GetProperty("street").GetString());
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task ManifestOnlyReturnsCurrentOwnActiveWorkForTodayAndOverdueDaysWithoutWriting()
    {
        var seed = await SeedAsync();
        var today = await ParcelAsync(seed, seed.CourierA, Today);
        var overdue = await ParcelAsync(seed, seed.CourierA, Today.AddDays(-2), ParcelStatus.InTransit);
        await ParcelAsync(seed, seed.CourierA, Today.AddDays(1));
        await ParcelAsync(seed, seed.CourierB, Today);
        await ParcelAsync(seed, seed.CourierB, Today.AddDays(-1));
        await ParcelAsync(seed, null, null, ParcelStatus.Created);
        await ParcelAsync(seed, seed.CourierA, Today, ParcelStatus.Delivered);
        await ParcelAsync(seed, seed.CourierA, Today, ParcelStatus.Failed);
        await ParcelAsync(seed, seed.CourierA, Today, ParcelStatus.Cancelled);
        await ParcelAsync(seed, seed.CourierA, Today, ParcelStatus.Created);
        var before = await StateAsync();
        using var factory = Factory(seed.CourierA);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", seed.CourierB.ToString());
        client.DefaultRequestHeaders.Add("X-User-Role", "Admin");
        using var response = await client.GetAsync(Manifest);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.Private);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var page = json.RootElement;
        Fields(page, "items", "totalCount", "page", "pageSize");
        Assert.Equal(2, page.GetProperty("totalCount").GetInt32());
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        Assert.Equal(20, page.GetProperty("pageSize").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToArray();
        Assert.Equal(new[] { today, overdue }.Order(), items.Select(x => x.GetProperty("id").GetGuid()).Order());
        var older = Assert.Single(items, x => x.GetProperty("id").GetGuid() == overdue);
        Assert.True(older.GetProperty("isOverdue").GetBoolean());
        Assert.Equal(Today.AddDays(-2).ToString("yyyy-MM-dd"), older.GetProperty("serviceDate").GetString());
        var current = Assert.Single(items, x => x.GetProperty("id").GetGuid() == today);
        Assert.False(current.GetProperty("isOverdue").GetBoolean());
        Assert.Equal("Fictional pickup street", current.GetProperty("pickupAddress").GetProperty("street").GetString());
        Assert.Equal("Fictional delivery street", current.GetProperty("deliveryAddress").GetProperty("street").GetString());
        Assert.Equal("Fictional Recipient", current.GetProperty("recipient").GetProperty("name").GetString());
        Assert.Equal(2, current.GetProperty("version").GetInt64());
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task DateAndOverdueFiltersUseTheSelectedServiceDay()
    {
        var seed = await SeedAsync();
        await ParcelAsync(seed, seed.CourierA, Today.AddDays(-1));
        var current = await ParcelAsync(seed, seed.CourierA, Today);
        var tomorrow = await ParcelAsync(seed, seed.CourierA, Today.AddDays(1));
        using var factory = Factory(seed.CourierA);
        using var client = factory.CreateClient();
        using var today = await GetJsonAsync(client, Manifest + "?includeOverdue=false");
        Assert.Equal(current, Assert.Single(today.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using var future = await GetJsonAsync(client, Manifest + $"?date={Today.AddDays(1):yyyy-MM-dd}&includeOverdue=false");
        Assert.Equal(tomorrow, Assert.Single(future.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        using var withOlder = await GetJsonAsync(client, Manifest + $"?date={Today.AddDays(1):yyyy-MM-dd}");
        Assert.Equal(3, withOlder.RootElement.GetProperty("totalCount").GetInt32());
        Assert.Equal(2, withOlder.RootElement.GetProperty("items").EnumerateArray().Count(x => x.GetProperty("isOverdue").GetBoolean()));
    }

    [Fact]
    public async Task ServerTimezoneDeterminesTodayAcrossTheUtcDayBoundary()
    {
        var seed = await SeedAsync();
        var selected = await ParcelAsync(seed, seed.CourierA, Today.AddDays(1));
        using var factory = Factory(seed.CourierA, configure: services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(new ReadClock(Now.AddHours(8)));
        });
        using var client = factory.CreateClient();
        using var json = await GetJsonAsync(client, Manifest + "?includeOverdue=false");
        Assert.Equal(selected, Assert.Single(json.RootElement.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
    }

    [Fact]
    public async Task PaginationIsStableAndScopesTheTotalToCurrentCourier()
    {
        var seed = await SeedAsync();
        var ids = new List<Guid>();
        for (var i = 0; i < 5; i++) ids.Add(await ParcelAsync(seed, seed.CourierA, Today));
        await ParcelAsync(seed, seed.CourierB, Today);
        using var factory = Factory(seed.CourierA);
        using var client = factory.CreateClient();
        var observed = new List<Guid>();
        for (var page = 1; page <= 3; page++)
        {
            var path = Manifest + $"?page={page}&pageSize=2";
            using var result = await GetJsonAsync(client, path);
            Assert.Equal(5, result.RootElement.GetProperty("totalCount").GetInt32());
            Assert.Equal(page, result.RootElement.GetProperty("page").GetInt32());
            observed.AddRange(result.RootElement.GetProperty("items").EnumerateArray().Select(x => x.GetProperty("id").GetGuid()));
            using var repeated = await GetJsonAsync(client, path);
            Assert.True(JsonElement.DeepEquals(result.RootElement, repeated.RootElement));
        }

        Assert.Equal(5, observed.Distinct().Count());
        Assert.Equal(ids.OrderDescending(), observed);
        using var beyond = await GetJsonAsync(client, Manifest + "?page=4&pageSize=2");
        Assert.Empty(beyond.RootElement.GetProperty("items").EnumerateArray());
    }

    [Fact]
    public async Task DetailReturnsOnlyDocumentedPrivateFieldsAndRealHistoryWithoutProofReferences()
    {
        var seed = await SeedAsync();
        var id = await ParcelAsync(seed, seed.CourierA, Today, proof: true);
        var before = await StateAsync();
        using var factory = Factory(seed.CourierA);
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(Detail(id));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var parcel = json.RootElement;
        Fields(parcel, "id", "trackingNumber", "status", "version", "priceSnapshot", "createdAt", "pickupAddress",
            "deliveryAddress", "packageDetails", "recipient", "assignment", "history");
        Fields(parcel.GetProperty("priceSnapshot"), "total", "currency", "pricingVersion");
        Assert.Equal(540m, parcel.GetProperty("priceSnapshot").GetProperty("total").GetDecimal());
        Fields(parcel.GetProperty("assignment"), "status", "serviceDate", "assignedAt", "pickedUpAt", "deliveredAt");
        Assert.Equal("Active", parcel.GetProperty("assignment").GetProperty("status").GetString());
        var history = parcel.GetProperty("history").EnumerateArray().ToArray();
        Assert.Equal(2, history.Length);
        Assert.Equal("Created", history[0].GetProperty("toStatus").GetString());
        Assert.Equal("Assigned", history[1].GetProperty("toStatus").GetString());
        Assert.Equal("private delivery note", history[1].GetProperty("deliveryNote").GetString());
        Fields(history[1], "fromStatus", "toStatus", "changeReason", "deliveryNote", "timestamp");
        foreach (var secret in new[] { "private-object-reference", "storageRef", "uploadedBy", "outbox", "requestFingerprint",
                     "customerId", "merchantId", "courierId", "changedBy", seed.OwnerId.ToString(), seed.CourierB.ToString() })
            Assert.DoesNotContain(secret, body, StringComparison.OrdinalIgnoreCase);
        using var manifest = await GetJsonAsync(client, Manifest);
        var manifestBody = manifest.RootElement.GetRawText();
        Assert.DoesNotContain("private delivery note", manifestBody);
        Assert.DoesNotContain("private-object-reference", manifestBody);
        Assert.DoesNotContain("history", manifestBody);
        Assert.Equal(before, await StateAsync());
    }

    [Fact]
    public async Task ForeignUnassignedMissingAndMalformedParcelsHaveTheSameSafe404()
    {
        var seed = await SeedAsync();
        var foreign = await ParcelAsync(seed, seed.CourierB, Today);
        var unassigned = await ParcelAsync(seed, null, null, ParcelStatus.Created);
        using var factory = Factory(seed.CourierA);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Correlation-ID", "same-denial");
        string? expected = null;
        foreach (var path in new[] { Detail(foreign), Detail(unassigned), Detail(Guid.NewGuid()), "/api/v1/parcels/malformed" })
        {
            using var response = await client.GetAsync(path);
            await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.NotFound, "not_found");
            var body = await response.Content.ReadAsStringAsync();
            expected ??= body;
            Assert.Equal(expected, body);
            Assert.DoesNotContain("Fictional", body);
        }
    }

    [Fact]
    public async Task ReassignedCourierLosesManifestAndDetailAccessOnSubsequentRequests()
    {
        var seed = await SeedAsync();
        var id = await ParcelAsync(seed, seed.CourierA, Today);
        using var original = Factory(seed.CourierA);
        using var oldClient = original.CreateClient();
        using (var before = await GetJsonAsync(oldClient, Detail(id))) Assert.Equal(id, before.RootElement.GetProperty("id").GetGuid());
        await ReassignAsync(id, seed.CourierB);
        using var denied = await oldClient.GetAsync(Detail(id));
        await DeliveryReadAuthenticationTests.AssertProblemAsync(denied, HttpStatusCode.NotFound, "not_found");
        using var oldManifest = await GetJsonAsync(oldClient, Manifest);
        Assert.Equal(0, oldManifest.RootElement.GetProperty("totalCount").GetInt32());
        using var replacement = Factory(seed.CourierB);
        using var newClient = replacement.CreateClient();
        using var detail = await GetJsonAsync(newClient, Detail(id));
        Assert.Equal(id, detail.RootElement.GetProperty("id").GetGuid());
        using var newManifest = await GetJsonAsync(newClient, Manifest);
        Assert.Equal(1, newManifest.RootElement.GetProperty("totalCount").GetInt32());
    }

    [Fact]
    public async Task SharedDetailAllowsOwnerCustomerButRejectsForeignCustomerAndCourierManifestAccess()
    {
        var seed = await SeedAsync();
        var id = await ParcelAsync(seed, null, null, ParcelStatus.Created);
        using var owner = Factory(seed.OwnerId, "Customer");
        using var ownerClient = owner.CreateClient();
        using var result = await GetJsonAsync(ownerClient, Detail(id));
        Assert.Equal(JsonValueKind.Null, result.RootElement.GetProperty("assignment").ValueKind);
        using var manifest = await ownerClient.GetAsync(Manifest);
        Assert.Equal(HttpStatusCode.Forbidden, manifest.StatusCode);
        using var foreign = Factory(Guid.NewGuid(), "Customer");
        using var foreignClient = foreign.CreateClient();
        using var response = await foreignClient.GetAsync(Detail(id));
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("Dispatcher")]
    [InlineData("Admin")]
    public async Task DocumentedOperationsRolesCanReadUnassignedDetailsButNotCourierManifest(string role)
    {
        var seed = await SeedAsync();
        var id = await ParcelAsync(seed, null, null, ParcelStatus.Created);
        using var factory = Factory(Guid.NewGuid(), role);
        using var client = factory.CreateClient();
        using var response = await GetJsonAsync(client, Detail(id));
        Assert.Equal(id, response.RootElement.GetProperty("id").GetGuid());
        using var manifest = await client.GetAsync(Manifest);
        Assert.Equal(HttpStatusCode.Forbidden, manifest.StatusCode);
    }

    [Fact]
    public async Task MerchantOriginDoesNotCreateCustomerOwnershipButAssignedCourierCanRead()
    {
        var seed = await SeedAsync();
        var id = await ParcelAsync(seed, seed.CourierA, Today, merchant: true);
        using var courier = Factory(seed.CourierA);
        using var courierClient = courier.CreateClient();
        using var success = await GetJsonAsync(courierClient, Detail(id));
        Assert.Equal(id, success.RootElement.GetProperty("id").GetGuid());
        using var customer = Factory(seed.OwnerId, "Customer");
        using var customerClient = customer.CreateClient();
        using var denied = await customerClient.GetAsync(Detail(id));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
        using var merchant = Factory(seed.OwnerId, "Merchant");
        using var merchantClient = merchant.CreateClient();
        using var forbidden = await merchantClient.GetAsync(Detail(id));
        Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
    }

    [Fact]
    public async Task InvalidStoredPrivateDataIsNotReturnedOrInspectedForForeignCallers()
    {
        var seed = await SeedAsync();
        var id = await ParcelAsync(seed, seed.CourierA, Today, malformed: true);
        using var own = Factory(seed.CourierA);
        using var ownClient = own.CreateClient();
        using var response = await ownClient.GetAsync(Detail(id));
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.ServiceUnavailable, "delivery_unavailable");
        Assert.DoesNotContain("private-malformed-address", await response.Content.ReadAsStringAsync());
        using var other = Factory(seed.CourierB);
        using var otherClient = other.CreateClient();
        using var denied = await otherClient.GetAsync(Detail(id));
        Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
    }

    [Fact]
    public async Task CountAndPageRemainConsistentWhenReassignmentCommitsBetweenTheirQueries()
    {
        var seed = await SeedAsync();
        var id = await ParcelAsync(seed, seed.CourierA, Today);
        var interceptor = new ReassignAfterCount(() => ReassignAsync(id, seed.CourierB));
        await using var context = new DeliveryDbContext(new DbContextOptionsBuilder<DeliveryDbContext>()
            .UseNpgsql(fixture.ConnectionString).AddInterceptors(interceptor).Options);
        var store = new PostgresDeliveryReadStore(() => context);
        var query = new AssignmentQuery(Today, true, 1, 20);
        var snapshot = await store.GetAssignmentsAsync(seed.CourierA, query, CancellationToken.None);
        Assert.True(interceptor.Fired);
        Assert.Equal(1, snapshot.TotalCount);
        Assert.Equal(id, Assert.Single(snapshot.Items).Id);
        // The in-flight read was authorized at its snapshot; a new read sees revocation.
        Assert.Empty((await store.GetAssignmentsAsync(seed.CourierA, query, CancellationToken.None)).Items);
        Assert.Null(await store.GetParcelAsync(new(seed.CourierA, true, false, false), id, CancellationToken.None));
        Assert.Equal(id, Assert.Single((await store.GetAssignmentsAsync(seed.CourierB, query, CancellationToken.None)).Items).Id);
    }

    private DeliveryDbContext Context() => new(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(fixture.ConnectionString).Options);
    private QuoteApiFactory Factory(Guid id, string role = "Courier", Action<IServiceCollection>? configure = null) =>
        new(fixture.ConnectionString, DeliveryTestAuthentication.Principal(id.ToString(), role), configureServices: configure);
    private static string Detail(Guid id) => "/api/v1/parcels/" + id;

    private async Task<Scenario> SeedAsync()
    {
        await using var context = Context();
        var zone = new DeliveryZone(Guid.NewGuid(), "Reads " + Guid.NewGuid().ToString("N"));
        var tier = new PricingTier(Guid.NewGuid(), zone.Id, ParcelSize.Small, "read-test-v1", 450m, 60m);
        var courierA = new Api.Domain.Courier(Guid.NewGuid(), true, true, 20);
        var courierB = new Api.Domain.Courier(Guid.NewGuid(), true, true, 20);
        context.AddRange(zone, tier, courierA, courierB);
        await context.SaveChangesAsync();
        return new(Guid.NewGuid(), courierA.Id, courierB.Id, zone.Id, tier);
    }

    private async Task<Guid> ParcelAsync(Scenario seed, Guid? courierId, DateOnly? day,
        ParcelStatus status = ParcelStatus.Assigned, bool proof = false, bool malformed = false, bool merchant = false)
    {
        // Fixture-only operational snapshots; no Phase 3 dispatcher/status handler is simulated.
        await using var context = Context();
        var pickup = JsonSerializer.Serialize(new NormalizedAddress("Fictional pickup street", "Colombo", "00100", seed.ZoneId), Json);
        var delivery = JsonSerializer.Serialize(new NormalizedAddress("Fictional delivery street", "Kandy", "20000", seed.ZoneId), Json);
        var recipient = JsonSerializer.Serialize(new NormalizedRecipient("Fictional Recipient", "0000000000"), Json);
        var details = new ParcelDetails(ParcelSize.Small, 2.5m, seed.ZoneId,
            malformed ? "private-malformed-address" : pickup, null, delivery, recipient);
        var parcel = merchant
            ? Parcel.ForMerchant(Guid.NewGuid(), seed.OwnerId, Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"), details, Now)
            : Parcel.ForCustomer(Guid.NewGuid(), seed.OwnerId, Guid.NewGuid().ToString("N"), details, Now);
        context.AddRange(parcel, new PriceSnapshot(Guid.NewGuid(), parcel.Id, seed.Tier, 450m, 90m, 540m, Now),
            BookingHistory.Created(Guid.NewGuid(), parcel, seed.OwnerId), OutboxMessage.ParcelCreated(Guid.NewGuid(), parcel, "read-fixture"));
        await context.SaveChangesAsync();
        CourierAssignment? assignment = null;
        if (courierId is { } courier)
        {
            assignment = new CourierAssignment(Guid.NewGuid(), parcel.Id, courier, day!.Value, Now);
            context.Assignments.Add(assignment);
            await context.SaveChangesAsync();
        }

        if (status != ParcelStatus.Created)
        {
            context.Entry(parcel).Property(x => x.Status).CurrentValue = status;
            context.Entry(parcel).Property(x => x.Version).CurrentValue = 2;
            context.Entry(parcel).Property(x => x.UpdatedAt).CurrentValue = Now.AddMinutes(4);
            var history = BookingHistory.Created(Guid.NewGuid(), parcel, seed.OwnerId);
            context.BookingHistory.Add(history);
            context.Entry(history).Property(x => x.FromStatus).CurrentValue = ParcelStatus.Created;
            context.Entry(history).Property(x => x.ToStatus).CurrentValue = status;
            context.Entry(history).Property(x => x.DeliveryNote).CurrentValue = "private delivery note";
            context.Entry(history).Property(x => x.Timestamp).CurrentValue = Now.AddMinutes(4);
            if (assignment is not null)
            {
                if (status is ParcelStatus.PickedUp or ParcelStatus.InTransit or ParcelStatus.OutForDelivery or ParcelStatus.Delivered)
                    assignment.MarkPickedUp(Now.AddMinutes(1));
                if (status == ParcelStatus.Delivered) assignment.MarkDelivered(Now.AddMinutes(2));
                if (status == ParcelStatus.Failed) assignment.MarkFailed();
                if (status == ParcelStatus.Cancelled) assignment.MarkCancelled();
            }
        }
        if (proof) context.ProofMetadata.Add(new ProofMetadata(Guid.NewGuid(), parcel.Id, ProofType.Photo,
            "proofs/private-object-reference-" + Guid.NewGuid().ToString("N"), "Fictional Recipient", seed.CourierA, Now));
        await context.SaveChangesAsync();
        return parcel.Id;
    }

    private async Task ReassignAsync(Guid parcelId, Guid replacement)
    {
        await using var context = Context();
        await using var transaction = await context.Database.BeginTransactionAsync();
        var old = await context.Assignments.SingleAsync(x => x.ParcelId == parcelId && x.Status == AssignmentStatus.Active);
        old.MarkReassigned();
        await context.SaveChangesAsync();
        context.Assignments.Add(new CourierAssignment(Guid.NewGuid(), parcelId, replacement, Today, Now.AddMinutes(1)));
        await context.SaveChangesAsync();
        await transaction.CommitAsync();
    }

    private async Task<string> StateAsync()
    {
        await using var context = Context();
        var parcels = await context.Parcels.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.Status, x.Version, x.UpdatedAt }).ToArrayAsync();
        var assignments = await context.Assignments.AsNoTracking().OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.CourierId, x.Status, x.ServiceDate, x.PickedUpAt, x.DeliveredAt }).ToArrayAsync();
        return JsonSerializer.Serialize(new { parcels, assignments,
            history = await context.BookingHistory.CountAsync(), outbox = await context.OutboxMessages.CountAsync(),
            quotes = await context.Quotes.CountAsync(), keys = await context.IdempotencyRecords.CountAsync(),
            proofs = await context.ProofMetadata.CountAsync(), snapshots = await context.PriceSnapshots.CountAsync() });
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, body);
        return JsonDocument.Parse(body);
    }

    private static void Fields(JsonElement value, params string[] fields) => Assert.Equal(fields.Order(StringComparer.Ordinal),
        value.EnumerateObject().Select(x => x.Name).Order(StringComparer.Ordinal));
    private sealed record Scenario(Guid OwnerId, Guid CourierA, Guid CourierB, Guid ZoneId, PricingTier Tier);
    private sealed class ReadClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }

    private sealed class ReassignAfterCount(Func<Task> reassign) : DbCommandInterceptor
    {
        private int _fired;
        public bool Fired => _fired != 0;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase) && Interlocked.Exchange(ref _fired, 1) == 0)
                await reassign();
            return result;
        }
    }
}
