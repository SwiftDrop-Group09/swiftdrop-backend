using System.Text.Json;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Bookings.Reliability;

namespace SwiftDrop.Delivery.Tests.Dispatch;

public sealed class ReassignmentDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);
    private static Parcel Parcel() => SwiftDrop.Delivery.Api.Domain.Parcel.ForCustomer(Guid.NewGuid(), Guid.NewGuid(), "TRK-REASSIGN",
        new(ParcelSize.Small, 1, Guid.NewGuid(), "pickup", null, "dropoff", "recipient"), Now);

    [Fact]
    public void ReassignmentRetainsStatusAndCreatesPrivateHistoryAndCompatibleEvent()
    {
        var parcel = Parcel();
        parcel.AssignCourier(1, Now);
        parcel.ReassignCourier(2, Now.AddMinutes(1));
        var actor = Guid.NewGuid();
        var history = BookingHistory.Reassigned(Guid.NewGuid(), parcel, actor, "  Courier unavailable  ");
        Assert.Equal(3, parcel.Version);
        Assert.Equal(ParcelStatus.Assigned, parcel.Status);
        Assert.Equal(Now.AddMinutes(1), history.Timestamp);
        Assert.Equal(actor, history.ChangedBy);
        Assert.Equal("Courier unavailable", history.ChangeReason);
        Assert.Equal(ParcelStatus.Assigned, history.FromStatus);
        Assert.Equal(ParcelStatus.Assigned, history.ToStatus);
        var message = OutboxMessage.Reassigned(Guid.NewGuid(), parcel, "reassignment-test");
        BookingOutboxContractTests.AssertSchema(message.Payload);
        using var json = JsonDocument.Parse(message.Payload);
        Assert.Equal("Assigned", json.RootElement.GetProperty("previousStatus").GetString());
        Assert.Equal("Assigned", json.RootElement.GetProperty("status").GetString());
        Assert.Equal(3, json.RootElement.GetProperty("parcelVersion").GetInt64());
        Assert.DoesNotContain(history.ChangeReason!,  message.Payload);
    }

    [Fact]
    public void InvalidStateStaleVersionAndOverflowDoNotMutateParcel()
    {
        var parcel = Parcel();
        Assert.Throws<InvalidOperationException>(() => parcel.ReassignCourier(1, Now));
        parcel.AssignCourier(1, Now);
        Assert.Throws<InvalidOperationException>(() => parcel.ReassignCourier(1, Now));
        Assert.Throws<InvalidOperationException>(() => BookingHistory.Reassigned(Guid.NewGuid(), parcel, Guid.NewGuid(), "reason"));
        Assert.Throws<InvalidOperationException>(() => OutboxMessage.Reassigned(Guid.NewGuid(), parcel, "test"));
        typeof(SwiftDrop.Delivery.Api.Domain.Parcel).GetProperty("Version")!.SetValue(parcel, long.MaxValue);
        Assert.Throws<OverflowException>(() => parcel.ReassignCourier(long.MaxValue, Now.AddHours(1)));
        Assert.Equal(Now, parcel.UpdatedAt);
        Assert.Equal(long.MaxValue, parcel.Version);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ReasonMustBeNonblank(string? reason) => Assert.Throws<ArgumentException>(() => ReassignmentReason.Normalize(reason));

    [Fact]
    public void ReasonLimitAppliesAfterTrimmingAndPreservesCaseAndInternalWhitespace()
    {
        Assert.Equal(new string('a', 500), ReassignmentReason.Normalize(" " + new string('a', 500) + " "));
        Assert.Throws<ArgumentException>(() => ReassignmentReason.Normalize(new string('a', 501)));
        Assert.Equal("Keep  Case", ReassignmentReason.Normalize(" Keep  Case "));
    }
}
