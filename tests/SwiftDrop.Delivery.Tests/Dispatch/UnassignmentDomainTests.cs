using System.Text.Json;
using SwiftDrop.Delivery.Api.Application.Dispatch;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Bookings.Reliability;

namespace SwiftDrop.Delivery.Tests.Dispatch;

public sealed class UnassignmentDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static Parcel Parcel() => SwiftDrop.Delivery.Api.Domain.Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), "TRK-UNASSIGN",
        new(ParcelSize.Small, 1, Guid.NewGuid(), "pickup", null, "dropoff", "recipient"), Now);

    [Fact]
    public void UnassignmentRevertsToCreatedAndCreatesPrivateHistoryAndCompatibleEvent()
    {
        var parcel = Parcel();
        parcel.AssignCourier(1, Now);
        var unassignedAt = Now.AddMinutes(5);
        parcel.UnassignCourier(2, unassignedAt);

        var actor = Guid.NewGuid();
        var history = BookingHistory.Unassigned(Guid.NewGuid(), parcel, actor, "  Courier vehicle breakdown  ");

        Assert.Equal(3, parcel.Version);
        Assert.Equal(ParcelStatus.Created, parcel.Status);
        Assert.Equal(unassignedAt, parcel.UpdatedAt);

        Assert.Equal(unassignedAt, history.Timestamp);
        Assert.Equal(actor, history.ChangedBy);
        Assert.Equal("Courier vehicle breakdown", history.ChangeReason);
        Assert.Equal(ParcelStatus.Assigned, history.FromStatus);
        Assert.Equal(ParcelStatus.Created, history.ToStatus);

        var message = OutboxMessage.Unassigned(Guid.NewGuid(), parcel, "unassignment-test");
        BookingOutboxContractTests.AssertSchema(message.Payload);
        using var json = JsonDocument.Parse(message.Payload);
        Assert.Equal("Assigned", json.RootElement.GetProperty("previousStatus").GetString());
        Assert.Equal("Created", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("parcelVersion").GetInt64());
        Assert.DoesNotContain(history.ChangeReason!, message.Payload);
    }

    [Fact]
    public void UnassignmentWithoutReasonCreatesHistoryWithNullReason()
    {
        var parcel = Parcel();
        parcel.AssignCourier(1, Now);
        parcel.UnassignCourier(2, Now.AddMinutes(5));

        var actor = Guid.NewGuid();
        var history = BookingHistory.Unassigned(Guid.NewGuid(), parcel, actor, null);

        Assert.Null(history.ChangeReason);
        Assert.Equal(ParcelStatus.Assigned, history.FromStatus);
        Assert.Equal(ParcelStatus.Created, history.ToStatus);
    }

    [Fact]
    public void InvalidStateStaleVersionAndOverflowDoNotMutateParcel()
    {
        var parcel = Parcel();
        // Parcel is in Created status, cannot unassign
        Assert.Throws<InvalidOperationException>(() => parcel.UnassignCourier(1, Now));

        parcel.AssignCourier(1, Now);
        // Stale version (expected 2, provided 1)
        Assert.Throws<InvalidOperationException>(() => parcel.UnassignCourier(1, Now));

        // Booking history guard: parcel must be Created and Version >= 2
        Assert.Throws<InvalidOperationException>(() => BookingHistory.Unassigned(Guid.NewGuid(), parcel, Guid.NewGuid(), "reason"));
        // Outbox guard: parcel must be Created and Version >= 2
        Assert.Throws<InvalidOperationException>(() => OutboxMessage.Unassigned(Guid.NewGuid(), parcel, "test"));

        // Overflow guard
        typeof(SwiftDrop.Delivery.Api.Domain.Parcel).GetProperty("Version")!.SetValue(parcel, long.MaxValue);
        Assert.Throws<OverflowException>(() => parcel.UnassignCourier(long.MaxValue, Now.AddHours(1)));
        Assert.Equal(Now, parcel.UpdatedAt);
        Assert.Equal(long.MaxValue, parcel.Version);
    }

    [Fact]
    public void CannotUnassignAfterPickup()
    {
        var parcel = Parcel();
        parcel.AssignCourier(1, Now);
        parcel.AdvanceCourierStatus(ParcelStatus.PickedUp, 2, Now.AddMinutes(10));

        Assert.Equal(ParcelStatus.PickedUp, parcel.Status);
        Assert.Throws<InvalidOperationException>(() => parcel.UnassignCourier(3, Now.AddMinutes(15)));
    }

    [Fact]
    public void IdempotencyRecordCompletesUnassignmentWith200AndStoredResponse()
    {
        var actor = Guid.NewGuid();
        var parcelId = Guid.NewGuid();
        var record = new IdempotencyRecord(Guid.NewGuid(), actor.ToString("D"), "UnassignCourier", "idem-key-1", "fingerprint", Now);

        var responseBody = JsonSerializer.Serialize(new { id = parcelId, status = "Created", version = 3 });
        record.CompleteUnassignment(parcelId, responseBody);

        Assert.Equal(parcelId, record.ParcelId);
        Assert.NotNull(record.StoredResponse);
        using var json = JsonDocument.Parse(record.StoredResponse);
        Assert.Equal(200, json.RootElement.GetProperty("statusCode").GetInt32());
        Assert.Equal(responseBody, json.RootElement.GetProperty("body").GetString());
        Assert.Equal($"/api/v1/parcels/{parcelId}", json.RootElement.GetProperty("location").GetString());
    }

    [Fact]
    public void AssignmentValidatorRejectsInvalidUnassignInputs()
    {
        var errors = new Dictionary<string, string[]>();

        // Missing Idempotency-Key
        var result = AssignmentValidator.ValidateUnassign(new UnassignCourierInput(Guid.NewGuid().ToString(), 2), null, errors);
        Assert.Null(result);
        Assert.True(errors.ContainsKey("Idempotency-Key"));

        // Missing body
        errors.Clear();
        result = AssignmentValidator.ValidateUnassign(null, "valid-key", errors);
        Assert.Null(result);
        Assert.True(errors.ContainsKey("body"));

        // Invalid parcelId
        errors.Clear();
        result = AssignmentValidator.ValidateUnassign(new UnassignCourierInput("not-a-guid", 2), "valid-key", errors);
        Assert.Null(result);
        Assert.True(errors.ContainsKey("parcelId"));

        // Version < 1
        errors.Clear();
        result = AssignmentValidator.ValidateUnassign(new UnassignCourierInput(Guid.NewGuid().ToString(), 0), "valid-key", errors);
        Assert.Null(result);
        Assert.True(errors.ContainsKey("expectedVersion"));

        // Reason > 500 chars
        errors.Clear();
        result = AssignmentValidator.ValidateUnassign(new UnassignCourierInput(Guid.NewGuid().ToString(), 2, new string('x', 501)), "valid-key", errors);
        Assert.Null(result);
        Assert.True(errors.ContainsKey("reason"));
    }

    [Fact]
    public void AssignmentValidatorAcceptsValidUnassignInput()
    {
        var errors = new Dictionary<string, string[]>();
        var parcelId = Guid.NewGuid();

        var command = AssignmentValidator.ValidateUnassign(
            new UnassignCourierInput(parcelId.ToString(), 2, "  Courier unavailable  "),
            "valid-key-123",
            errors);

        Assert.NotNull(command);
        Assert.Empty(errors);
        Assert.Equal(parcelId, command.ParcelId);
        Assert.Equal(2, command.ExpectedVersion);
        Assert.Equal("valid-key-123", command.Key);
        Assert.Equal("Courier unavailable", command.Reason);
        Assert.NotEmpty(command.Fingerprint);
    }
}
