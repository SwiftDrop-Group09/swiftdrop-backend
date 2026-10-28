using System.Text.Json;

using Microsoft.EntityFrameworkCore;

using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Authentication;
using SwiftDrop.Delivery.Tests.Bookings.Reliability;

namespace SwiftDrop.Delivery.Tests.ClientHarness;

public sealed record CourierClientVerification(int Parcels, int Delivered, int Failed,
    int Proofs, int HistoryRows, int OutboxRows, int ReplayRows);

public sealed partial class BookingClientHarness
{
    /// <summary>Read-only acceptance of the two existing client smoke workflows.
    /// These fixed counts describe this owned TESTDATA fixture, never production rules.</summary>
    public async Task<CourierClientVerification> VerifyCompletedCourierWorkflowAsync()
    {
        if (_courierStorage is null) throw new InvalidOperationException("Courier fixture mode is required.");
        await using var database = OpenContext();
        var assignments = await database.Assignments.AsNoTracking()
            .Where(x => x.CourierId == DeliveryTestAuthentication.NonCustomerId).ToArrayAsync();
        var ids = assignments.Select(x => x.ParcelId).Distinct().ToArray();
        Assert.Equal(5, ids.Length);
        var parcels = await database.Parcels.AsNoTracking().Where(x => ids.Contains(x.Id)).ToArrayAsync();
        Assert.Equal(2, parcels.Count(x => x.Status == ParcelStatus.Delivered));
        Assert.Equal(3, parcels.Count(x => x.Status == ParcelStatus.Failed));
        Assert.DoesNotContain(assignments, x => x.Status == AssignmentStatus.Active);
        var history = await database.BookingHistory.AsNoTracking().Where(x => ids.Contains(x.ParcelId)).ToArrayAsync();
        var events = await database.OutboxMessages.AsNoTracking().ToArrayAsync();
        Assert.Equal(27, events.Length);
        Assert.Equal(27, history.Length);
        static (Guid ParcelId, long Version) EventVersion(OutboxMessage message)
        {
            using var payload = JsonDocument.Parse(message.Payload);
            return (payload.RootElement.GetProperty("parcelId").GetGuid(), payload.RootElement.GetProperty("parcelVersion").GetInt64());
        }
        var eventVersions = events.Select(EventVersion).ToArray();
        Assert.All(events, x => { Assert.Null(x.PublishedAt); BookingOutboxContractTests.AssertSchema(x.Payload); });
        foreach (var parcel in parcels)
        {
            Assert.Equal(parcel.Version, history.Count(x => x.ParcelId == parcel.Id));
            var versions = eventVersions.Where(x => x.ParcelId == parcel.Id).Select(x => x.Version).Order().ToArray();
            Assert.Equal(Enumerable.Range(1, checked((int)parcel.Version)).Select(x => (long)x), versions);
            Assert.Equal(parcel.Status == ParcelStatus.Delivered ? AssignmentStatus.Delivered : AssignmentStatus.Failed,
                Assert.Single(assignments, x => x.ParcelId == parcel.Id).Status);
        }
        foreach (var message in events)
        {
            using var payload = JsonDocument.Parse(message.Payload);
            Assert.Equal(message.Id, payload.RootElement.GetProperty("eventId").GetGuid());
            Assert.DoesNotContain("TESTDATA", message.Payload, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("proofs/", message.Payload, StringComparison.OrdinalIgnoreCase);
        }
        var prices = await database.PriceSnapshots.AsNoTracking().Where(x => ids.Contains(x.ParcelId)).ToArrayAsync();
        Assert.Equal(5, prices.Length);
        Assert.All(prices, x => { Assert.Equal(540m, x.Total); Assert.Equal("LKR", x.Currency); Assert.Equal("status-test-v1", x.PricingVersion); });
        var proofs = await database.ProofMetadata.AsNoTracking().Where(x => ids.Contains(x.ParcelId)).ToArrayAsync();
        Assert.Equal(2, proofs.Length);
        Assert.Contains(proofs, x => x.Type == ProofType.Photo);
        Assert.Contains(proofs, x => x.Type == ProofType.Signature);
        Assert.Equal(proofs.Select(x => x.StorageRef).Order(), _courierStorage.Objects.Keys.Order());
        Assert.All(proofs, x => Assert.Equal(DeliveryTestAuthentication.NonCustomerId, x.UploadedBy));
        var replayCount = await database.IdempotencyRecords.AsNoTracking()
            .CountAsync(x => x.PrincipalId == DeliveryTestAuthentication.NonCustomerId.ToString("D"));
        Assert.Equal(10, replayCount);
        return new(parcels.Length, 2, 3, proofs.Length, history.Length, events.Length, replayCount);
    }
}
