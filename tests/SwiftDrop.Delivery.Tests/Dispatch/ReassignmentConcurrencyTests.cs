using System.Net;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Courier;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Dispatch;

[Collection("Postgres")]
public sealed class ReassignmentConcurrencyTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task OppositeDirectionCourierSwapsUseConsistentDatabaseLockOrder(int capacity)
    {
        await using var a = await StatusTestScenario.CreateAsync(fixture);
        await using var b = await StatusTestScenario.CreateAsync(fixture);
        await using (var db = a.Context())
        {
            (await db.Couriers.SingleAsync(x => x.Id == a.CourierId)).Configure(true, true, capacity);
            (await db.Couriers.SingleAsync(x => x.Id == b.CourierId)).Configure(true, true, capacity);
            var zoneA = (await db.Parcels.SingleAsync(x => x.Id == a.ParcelId)).ZoneId;
            var zoneB = (await db.Parcels.SingleAsync(x => x.Id == b.ParcelId)).ZoneId;
            db.CourierZones.AddRange(new CourierZone(a.CourierId, zoneB), new CourierZone(b.CourierId, zoneA));
            await db.SaveChangesAsync();
        }
        using var factory = a.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var responses = await Task.WhenAll(
            ReassignmentApiTests.Send(client, a.ParcelId, b.CourierId),
            ReassignmentApiTests.Send(client, b.ParcelId, a.CourierId));
        try
        {
            foreach (var response in responses)
                Assert.Equal(capacity == 2 ? HttpStatusCode.OK : HttpStatusCode.UnprocessableEntity, response.StatusCode);
            var ga = await a.GraphAsync();
            var gb = await b.GraphAsync();
            Assert.Equal(capacity == 2 ? b.CourierId : a.CourierId, Assert.Single(ga.Assignments, x => x.Status == AssignmentStatus.Active).CourierId);
            Assert.Equal(capacity == 2 ? a.CourierId : b.CourierId, Assert.Single(gb.Assignments, x => x.Status == AssignmentStatus.Active).CourierId);
            await using var db = a.Context();
            Assert.Equal(1, await db.Assignments.CountAsync(x => x.CourierId == a.CourierId && x.Status == AssignmentStatus.Active));
            Assert.Equal(1, await db.Assignments.CountAsync(x => x.CourierId == b.CourierId && x.Status == AssignmentStatus.Active));
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task InitialAssignmentAndReplacementCompeteForTheSameLastDailySlot()
    {
        await using var a = await StatusTestScenario.CreateAsync(fixture);
        await using var b = await StatusTestScenario.CreateAsync(fixture, ParcelStatus.Created);
        var target = a.OtherCourierId;
        await using (var db = a.Context())
        {
            (await db.Couriers.SingleAsync(x => x.Id == target)).Configure(true, true, 1);
            var zone = (await db.Parcels.SingleAsync(x => x.Id == b.ParcelId)).ZoneId;
            db.CourierZones.Add(new CourierZone(target, zone));
            await db.SaveChangesAsync();
        }
        using var factory = a.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var responses = await Task.WhenAll(
            ReassignmentApiTests.Send(client, a.ParcelId, target, date: "2026-10-28"),
            ReassignmentApiTests.Send(client, b.ParcelId, target, version: 1, reason: null, reassign: false, date: "2026-10-28"));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.UnprocessableEntity);
            await using var db = a.Context();
            Assert.Equal(1, await db.Assignments.CountAsync(x => x.CourierId == target && x.Status == AssignmentStatus.Active && x.ServiceDate == new DateOnly(2026, 10, 28)));
            var ga = await a.GraphAsync();
            Assert.Single(ga.Assignments, x => x.Status == AssignmentStatus.Active);
            Assert.Equal(responses[0].IsSuccessStatusCode ? 3 : 2, ga.Parcel.Version);
            Assert.Equal(responses[0].IsSuccessStatusCode ? AssignmentStatus.Reassigned : AssignmentStatus.Active,
                ga.Assignments.Single(x => x.CourierId == a.CourierId).Status);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }

    [Fact]
    public async Task CompetingReassignmentsOfOneParcelCommitExactlyOnce()
    {
        await using var s = await StatusTestScenario.CreateAsync(fixture);
        var third = Guid.NewGuid();
        await using (var db = s.Context())
        {
            var zone = (await db.Parcels.SingleAsync(x => x.Id == s.ParcelId)).ZoneId;
            db.Couriers.Add(new Api.Domain.Courier(third, true, true, 2));
            db.CourierZones.Add(new CourierZone(third, zone));
            await db.SaveChangesAsync();
        }
        using var factory = s.CreateFactory(Guid.NewGuid(), "Dispatcher");
        using var client = factory.CreateClient();
        var before = await s.GraphAsync();
        var responses = await Task.WhenAll(ReassignmentApiTests.Send(client, s.ParcelId, s.OtherCourierId),
            ReassignmentApiTests.Send(client, s.ParcelId, third));
        try
        {
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.OK);
            Assert.Single(responses, r => r.StatusCode == HttpStatusCode.Conflict);
            var after = await s.GraphAsync();
            Assert.Equal(3, after.Parcel.Version);
            Assert.Single(after.Assignments, x => x.Status == AssignmentStatus.Active);
            Assert.Single(after.Assignments, x => x.Status == AssignmentStatus.Reassigned);
            Assert.Equal(before.History.Length + 1, after.History.Length);
            Assert.Equal(before.Events.Length + 1, after.Events.Length);
        }
        finally { foreach (var response in responses) response.Dispose(); }
    }
}
