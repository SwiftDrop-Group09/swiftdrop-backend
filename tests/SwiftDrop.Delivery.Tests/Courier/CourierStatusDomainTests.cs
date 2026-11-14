using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Bookings.Reliability;

namespace SwiftDrop.Delivery.Tests.Courier;

public sealed class CourierStatusDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    public static IEnumerable<object[]> Matrix()
    {
        var allowed = new HashSet<(ParcelStatus, ParcelStatus)>
        {
            (ParcelStatus.Assigned, ParcelStatus.PickedUp), (ParcelStatus.PickedUp, ParcelStatus.InTransit),
            (ParcelStatus.InTransit, ParcelStatus.OutForDelivery), (ParcelStatus.Assigned, ParcelStatus.Failed),
            (ParcelStatus.PickedUp, ParcelStatus.Failed), (ParcelStatus.InTransit, ParcelStatus.Failed),
            (ParcelStatus.OutForDelivery, ParcelStatus.Failed)
        };
        foreach (var from in Enum.GetValues<ParcelStatus>())
        foreach (var to in Enum.GetValues<ParcelStatus>()) yield return [from, to, allowed.Contains((from, to))];
    }

    [Theory]
    [MemberData(nameof(Matrix))]
    public void CourierPolicyMatchesTheEntireDocumentedSubmatrix(ParcelStatus from, ParcelStatus to, bool allowed)
    {
        Assert.Equal(allowed, CourierTransitionPolicy.IsAllowed(from, to));
        var parcel = At(from);
        if (allowed)
        {
            parcel.AdvanceCourierStatus(to, 2, Now.AddMinutes(1));
            Assert.Equal(to, parcel.Status);
            Assert.Equal(3, parcel.Version);
        }
        else
        {
            var error = Assert.Throws<CourierTransitionException>(() => parcel.AdvanceCourierStatus(to, 2, Now));
            Assert.Equal(CourierTransitionFailure.InvalidTransition, error.Failure);
            Assert.Equal(from, parcel.Status);
            Assert.Equal(2, parcel.Version);
        }
    }

    [Fact]
    public void StaleVersionDoesNotChangeStatusVersionOrTime()
    {
        var parcel = At(ParcelStatus.Assigned);
        var error = Assert.Throws<CourierTransitionException>(() => parcel.AdvanceCourierStatus(ParcelStatus.PickedUp, 1, Now.AddMinutes(1)));
        Assert.Equal(CourierTransitionFailure.StaleVersion, error.Failure);
        Assert.Equal(2, error.CurrentVersion);
        Assert.Equal(ParcelStatus.Assigned, parcel.Status);
        Assert.Equal(2, parcel.Version);
        Assert.Equal(Now, parcel.UpdatedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ChangedHistoryAndEventRetainCanonicalVersionAndKeepPrivateMetadataOutOfTheEvent(bool failed)
    {
        var parcel = At(ParcelStatus.Assigned);
        var actor = Guid.NewGuid();
        var next = failed ? ParcelStatus.Failed : ParcelStatus.PickedUp;
        parcel.AdvanceCourierStatus(next, 2, Now.AddMinutes(1));
        var history = BookingHistory.CourierChanged(Guid.NewGuid(), parcel, ParcelStatus.Assigned, actor,
            "private stage note", failed ? CourierFailureReason.RecipientUnavailable : null);
        var message = OutboxMessage.CourierChanged(Guid.NewGuid(), parcel, ParcelStatus.Assigned, "status-contract");
        Assert.Equal(actor, history.ChangedBy);
        Assert.Equal("private stage note", history.DeliveryNote);
        Assert.Equal(failed ? "RecipientUnavailable" : null, history.ChangeReason);
        Assert.Equal(parcel.UpdatedAt, history.Timestamp);
        Assert.Equal(ParcelStatus.Assigned, history.FromStatus);
        Assert.Equal(next, history.ToStatus);
        BookingOutboxContractTests.AssertSchema(message.Payload);
        using var json = JsonDocument.Parse(message.Payload);
        Assert.Equal(3, json.RootElement.GetProperty("parcelVersion").GetInt64());
        Assert.Equal(parcel.UpdatedAt, json.RootElement.GetProperty("occurredAt").GetDateTimeOffset());
        Assert.Equal("Assigned", json.RootElement.GetProperty("previousStatus").GetString());
        Assert.Equal(next.ToString(), json.RootElement.GetProperty("status").GetString());
        Assert.DoesNotContain("private", message.Payload, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(actor.ToString(), message.Payload);
        Assert.DoesNotContain("RecipientUnavailable", message.Payload);
    }

    [Fact]
    public void HistoryFactoryCannotBypassRequiredNotesOrFailureReason()
    {
        var parcel = At(ParcelStatus.Assigned);
        parcel.AdvanceCourierStatus(ParcelStatus.PickedUp, 2, Now);
        Assert.Throws<ArgumentException>(() => BookingHistory.CourierChanged(Guid.NewGuid(), parcel, ParcelStatus.Assigned, Guid.NewGuid(), " ", null));
        parcel = At(ParcelStatus.Assigned);
        parcel.AdvanceCourierStatus(ParcelStatus.Failed, 2, Now);
        Assert.Throws<ArgumentException>(() => BookingHistory.CourierChanged(Guid.NewGuid(), parcel, ParcelStatus.Assigned, Guid.NewGuid(), null, null));
        Assert.Throws<ArgumentException>(() => CourierTransitionPolicy.ValidateMetadata(ParcelStatus.Failed, null, (CourierFailureReason)99));
    }

    [Fact]
    public void StatusReplayExtensionRetainsExactBodyWithoutBroadeningBookingCompletion()
    {
        var parcelId = Guid.NewGuid();
        var record = new IdempotencyRecord(Guid.NewGuid(), Guid.NewGuid().ToString(), CourierStatusValidator.Operation, "key", "fingerprint", Now);
        const string body = "{ \"status\": \"PickedUp\" }";
        record.CompleteStatusUpdate(parcelId, body);
        using var json = JsonDocument.Parse(record.StoredResponse!);
        Assert.Equal(200, json.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(body, json.RootElement.GetProperty("body").GetString());
        Assert.Equal(Now.AddDays(30), record.ExpiresAt);
        Assert.Throws<InvalidOperationException>(() => record.CompleteStatusUpdate(parcelId, "{}"));
        var booking = new IdempotencyRecord(Guid.NewGuid(), "owner", "CreateParcel", "key", "fingerprint", Now);
        Assert.Throws<InvalidOperationException>(() => booking.CompleteStatusUpdate(parcelId, "{}"));
        Assert.Throws<ArgumentOutOfRangeException>(() => booking.Complete(parcelId, 200, "{}", "/location"));
    }

    [Fact]
    public void CanonicalFingerprintBindsEveryMutationFieldAndParcel()
    {
        var id = Guid.NewGuid();
        var input = new CourierStatusInput("PickedUp", 2, " private note ");
        var original = CourierStatusValidator.Validate(id, input, "key").Command!;
        Assert.Equal(original.Fingerprint, CourierStatusValidator.Validate(id, input with { Notes = "private note" }, "another-key").Command!.Fingerprint);
        foreach (var changed in new[] { input with { Notes = "other note" }, input with { ExpectedVersion = 3 }, input with { Status = "InTransit" } })
            Assert.NotEqual(original.Fingerprint, CourierStatusValidator.Validate(id, changed, "key").Command!.Fingerprint);
        Assert.NotEqual(original.Fingerprint, CourierStatusValidator.Validate(Guid.NewGuid(), input, "key").Command!.Fingerprint);
        var failed = new CourierStatusInput("Failed", 2, null, "Damaged");
        Assert.NotEqual(CourierStatusValidator.Validate(id, failed, "key").Command!.Fingerprint,
            CourierStatusValidator.Validate(id, failed with { FailureReason = "Other" }, "key").Command!.Fingerprint);
    }

    private static Parcel At(ParcelStatus status)
    {
        var parcel = Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), "domain-status-tracking",
            new(ParcelSize.Small, 2.5m, Guid.NewGuid(), "private pickup", null, "private destination", "private contact"), Now);
        // Arrange an existing dispatch snapshot without inventing a dispatcher domain API.
        using var context = new DeliveryDbContext(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql().Options);
        context.Entry(parcel).Property(x => x.Status).CurrentValue = status;
        context.Entry(parcel).Property(x => x.Version).CurrentValue = 2;
        return parcel;
    }
}
