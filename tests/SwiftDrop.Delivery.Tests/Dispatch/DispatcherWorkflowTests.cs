using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SwiftDrop.Delivery.Api.Application.Admin.Zones;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Application.Dispatch;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Bookings.Reliability;
using SwiftDrop.Delivery.Tests.Courier;
using SwiftDrop.Delivery.Tests.Persistence;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Dispatch;

[Collection("Postgres")]
public sealed class DispatcherWorkflowTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("Dispatcher", false)]
    [InlineData("Admin", false)]
    [InlineData("Dispatcher", true)]
    [InlineData("Admin", true)]
    public async Task BookingThroughTerminalOutcomeKeepsDispatcherProjectionsAndReplayConsistent(string role, bool reassign)
    {
        // Only reference data is seeded. Every parcel/assignment/status/proof write
        // below passes through the existing public API in an isolated test host.
        await using var booking = await BookingReliabilityScenario.CreateAsync(fixture);
        var courierA = Guid.NewGuid();
        var courierB = Guid.NewGuid();
        var actor = Guid.NewGuid();
        var storage = new MemoryProofStorage { Now = booking.Clock.UtcNow };
        await using (var db = booking.OpenContext())
        {
            db.AddRange(new Api.Domain.Courier(courierA, true, true, 1), new Api.Domain.Courier(courierB, true, true, 1),
                new CourierZone(courierA, booking.Delivery.Id), new CourierZone(courierB, booking.Delivery.Id));
            await db.SaveChangesAsync();
        }
        using var operationsHost = Host(actor, role);
        using var operations = operationsHost.CreateClient();
        using var courierHostA = Host(courierA, "Courier");
        using var firstCourier = courierHostA.CreateClient();
        using var courierHostB = Host(courierB, "Courier");
        using var secondCourier = courierHostB.CreateClient();
        using var strangerHost = Host(Guid.NewGuid(), "Customer");
        using var stranger = strangerHost.CreateClient();

        var bookingKey = Guid.NewGuid().ToString();
        using var created = await booking.ConfirmAsync(bookingKey);
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var createdBody = await created.Content.ReadAsStringAsync();
        using var receipt = JsonDocument.Parse(createdBody);
        var parcelId = receipt.RootElement.GetProperty("id").GetGuid();
        var tracking = receipt.RootElement.GetProperty("trackingNumber").GetString();
        var queuePath = $"/api/v1/parcels?status=Created&zoneId={booking.Delivery.Id}&search={tracking}";
        var queue = await Read<CustomerParcelPage>(operations, queuePath);
        Assert.Equal(parcelId, Assert.Single(queue.Items).Id);
        Assert.Equal(parcelId, Assert.Single((await Read<CustomerParcelPage>(booking.Client, "/api/v1/parcels")).Items).Id);
        Assert.Empty((await Read<CustomerParcelPage>(stranger, queuePath)).Items);
        await AssertProjection("Created", 1, 0, 0);

        var options = await Read<AssignmentOptions>(operations, $"/api/v1/deliveries/{parcelId}/assignment-options");
        Assert.Equal(1, options.ParcelVersion);
        Assert.Contains(options.Items, x => x.CourierId == courierA);
        var assignKey = Guid.NewGuid().ToString();
        var assignmentBody = JsonSerializer.Serialize(new
        {
            parcelId, courierId = courierA, expectedVersion = 1, serviceDate = options.ServiceDate
        });
        var assignedBody = await Assign(assignmentBody, assignKey);
        Assert.Empty((await Read<CustomerParcelPage>(operations, queuePath)).Items);
        Assert.Equal(parcelId, Assert.Single((await Read<AssignedParcelPage>(firstCourier, "/api/v1/couriers/me/assignments")).Items).Id);
        await AssertProjection("Assigned", 2, 1, 0);

        var current = firstCourier;
        long version = 2;
        if (reassign)
        {
            var replacement = await Read<AssignmentOptions>(operations,
                $"/api/v1/deliveries/{parcelId}/assignment-options?reassign=true");
            Assert.Equal(courierA, replacement.CurrentAssignment!.CourierId);
            Assert.Equal(courierB, Assert.Single(replacement.Items).CourierId);
            await Assign(JsonSerializer.Serialize(new
            {
                parcelId, courierId = courierB, expectedVersion = version, reassign = true,
                reason = "Private replacement reason", serviceDate = replacement.ServiceDate
            }), Guid.NewGuid().ToString());
            version++;
            current = secondCourier;
            await AssertProjection("Assigned", version, 0, 1);
            Assert.Empty((await Read<AssignedParcelPage>(firstCourier, "/api/v1/couriers/me/assignments")).Items);
            Assert.Equal(parcelId, Assert.Single((await Read<AssignedParcelPage>(secondCourier, "/api/v1/couriers/me/assignments")).Items).Id);
            var beforeDenied = await Signature();
            using var denied = await Update(firstCourier, "PickedUp", version);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.Equal(beforeDenied, await Signature());
        }

        var beforeWrongRole = await Signature();
        using (var denied = await Update(operations, "PickedUp", version))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        using (var denied = await ProofTestSupport.Send(operations, parcelId, ProofTestSupport.Form(version), Guid.NewGuid().ToString()))
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Empty(storage.Objects);
        Assert.Equal(beforeWrongRole, await Signature());

        if (reassign)
        {
            using var failed = await Update(current, "Failed", version, "RecipientUnavailable");
            Assert.Equal(HttpStatusCode.OK, failed.StatusCode);
            version++;
        }
        else
        {
            foreach (var stage in new[] { "PickedUp", "InTransit", "OutForDelivery" })
            {
                using var advanced = await Update(current, stage, version);
                Assert.Equal(HttpStatusCode.OK, advanced.StatusCode);
                await AssertProjection(stage, ++version, 1, 0);
            }
            using var delivered = await ProofTestSupport.Send(current, parcelId,
                ProofTestSupport.Form(version), Guid.NewGuid().ToString());
            Assert.Equal(HttpStatusCode.OK, delivered.StatusCode);
            version++;
            Assert.Single(storage.Objects);
        }
        var finalStatus = reassign ? "Failed" : "Delivered";
        await AssertProjection(finalStatus, version, 0, 0);
        Assert.Empty((await Read<AssignedParcelPage>(current, "/api/v1/couriers/me/assignments")).Items);
        var terminalHistory = await Read<JsonElement>(current, "/api/v1/couriers/me/history");
        Assert.Equal(parcelId, Assert.Single(terminalHistory.GetProperty("items").EnumerateArray()).GetProperty("id").GetGuid());
        if (reassign)
        {
            var oldHistory = await Read<JsonElement>(firstCourier, "/api/v1/couriers/me/history");
            Assert.Empty(oldHistory.GetProperty("items").EnumerateArray());
        }

        var beforeReplay = await Signature();
        Assert.Equal(assignedBody, await Assign(assignmentBody, assignKey));
        using var bookingReplay = await booking.ConfirmAsync(bookingKey);
        Assert.Equal(HttpStatusCode.Created, bookingReplay.StatusCode);
        Assert.Equal(createdBody, await bookingReplay.Content.ReadAsStringAsync());
        Assert.Equal(beforeReplay, await Signature());

        await using var verification = booking.OpenContext();
        var parcel = await verification.Parcels.SingleAsync(x => x.Id == parcelId);
        Assert.Equal(version, parcel.Version);
        var history = await verification.BookingHistory.Where(x => x.ParcelId == parcelId).ToArrayAsync();
        Assert.Equal(version, history.LongLength);
        var events = await verification.OutboxMessages.FromSqlInterpolated(
            $"SELECT * FROM outbox_messages WHERE payload->>'parcelId' = {parcelId.ToString()}").ToArrayAsync();
        Assert.Equal(version, events.LongLength);
        foreach (var message in events)
        {
            BookingOutboxContractTests.AssertSchema(message.Payload);
            Assert.DoesNotContain("Private", message.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("proofs/", message.Payload);
        }
        var assignments = await verification.Assignments.Where(x => x.ParcelId == parcelId).ToArrayAsync();
        Assert.DoesNotContain(assignments, x => x.Status == AssignmentStatus.Active);
        Assert.Single(assignments, x => x.Status == (reassign ? AssignmentStatus.Failed : AssignmentStatus.Delivered));
        if (reassign)
        {
            Assert.Single(assignments, x => x.CourierId == courierA && x.Status == AssignmentStatus.Reassigned);
            Assert.Contains(history, x => x.ChangeReason == "Private replacement reason");
        }
        Assert.Equal(reassign ? 0 : 1, await verification.ProofMetadata.CountAsync(x => x.ParcelId == parcelId));

        QuoteApiFactory Host(Guid id, string identityRole) => new(booking.ConnectionString,
            QuoteApiFactory.Principal(id.ToString(), identityRole), configureServices: services =>
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(booking.Clock);
                ProofTestSupport.UseStorage(services, storage);
            });

        async Task<string> Assign(string body, string key)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/deliveries/assign")
            { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") };
            request.Headers.Add("Idempotency-Key", key);
            using var response = await operations.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            return await response.Content.ReadAsStringAsync();
        }

        Task<HttpResponseMessage> Update(HttpClient client, string status, long expectedVersion, string? reason = null) =>
            CourierStatusAuthenticationTests.Send(client,
                JsonSerializer.Serialize(new { status, expectedVersion, failureReason = reason }),
                Guid.NewGuid().ToString(), $"/api/v1/deliveries/{parcelId}/status");

        async Task AssertProjection(string status, long expectedVersion, int countA, int countB)
        {
            var detail = await Read<ParcelDetailResponse>(operations, $"/api/v1/parcels/{parcelId}");
            Assert.Equal(status, detail.Status);
            Assert.Equal(expectedVersion, detail.Version);
            foreach (var (courier, count) in new[] { (courierA, countA), (courierB, countB) })
            {
                var parcels = await Read<DispatchAssignmentsPage>(operations, $"/api/v1/couriers/{courier}/assignments");
                Assert.Equal(count, parcels.TotalCount);
                if (count != 0) Assert.Equal(status, Assert.Single(parcels.Items).Status);
                var page = 1;
                WorkloadPage workload;
                do
                {
                    workload = await Read<WorkloadPage>(operations, $"/api/v1/couriers/workload?page={page++}&pageSize=100");
                } while (workload.Items.All(x => x.CourierId != courier) && (page - 1) * 100 < workload.TotalCount);
                Assert.Equal(count, Assert.Single(workload.Items, x => x.CourierId == courier).ActiveCount);
            }
            var day = booking.Clock.UtcNow.ToString("yyyy-MM-dd");
            foreach (var range in new[] { "", $"?from={day}T00:00:00Z&to={booking.Clock.UtcNow.AddDays(1):yyyy-MM-dd}T00:00:00Z" })
            {
                var summary = await Read<ZoneSummaryResponse>(operations, "/api/v1/zones/summary" + range);
                var zone = Assert.Single(summary.Zones, x => x.ZoneId == booking.Delivery.Id);
                Assert.Equal(1, zone.Total);
                Assert.Equal(new Dictionary<string, int> { [status] = 1 }, zone.StatusCounts);
            }
        }

        async Task<string> Signature()
        {
            await using var db = booking.OpenContext();
            return JsonSerializer.Serialize(new
            {
                parcel = await db.Parcels.AsNoTracking().SingleAsync(x => x.Id == parcelId),
                assignments = await db.Assignments.AsNoTracking().Where(x => x.ParcelId == parcelId).OrderBy(x => x.Id).ToArrayAsync(),
                history = await db.BookingHistory.AsNoTracking().Where(x => x.ParcelId == parcelId).OrderBy(x => x.Id).ToArrayAsync(),
                events = await db.OutboxMessages.FromSqlInterpolated($"SELECT * FROM outbox_messages WHERE payload->>'parcelId' = {parcelId.ToString()}")
                    .AsNoTracking().OrderBy(x => x.Id).ToArrayAsync(),
                keys = await db.IdempotencyRecords.AsNoTracking().Where(x => x.ParcelId == parcelId).OrderBy(x => x.Id).ToArrayAsync(),
                proofs = await db.ProofMetadata.AsNoTracking().Where(x => x.ParcelId == parcelId).OrderBy(x => x.Id).ToArrayAsync()
            });
        }
    }

    private static async Task<T> Read<T>(HttpClient client, string path)
    {
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
}
