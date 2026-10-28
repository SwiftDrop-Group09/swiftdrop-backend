using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Tests.Courier;

public sealed class AssignmentDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 28, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void AssignmentRetainsItsOwnerServiceDayAndUtcLifecycleTimes()
    {
        var parcelId = Guid.NewGuid();
        var courierId = Guid.NewGuid();
        var serviceDate = new DateOnly(2026, 10, 27);
        var assignment = new CourierAssignment(Guid.NewGuid(), parcelId, courierId, serviceDate,
            Now.ToOffset(TimeSpan.FromHours(5.5)));

        Assert.Equal(parcelId, assignment.ParcelId);
        Assert.Equal(courierId, assignment.CourierId);
        Assert.Equal(serviceDate, assignment.ServiceDate);
        Assert.Equal(AssignmentStatus.Active, assignment.Status);
        Assert.Equal(Now, assignment.AssignedAt);
        Assert.Equal(TimeSpan.Zero, assignment.AssignedAt.Offset);
        Assert.Null(assignment.PickedUpAt);
        Assert.Null(assignment.DeliveredAt);

        assignment.MarkPickedUp(Now.AddMinutes(1).ToOffset(TimeSpan.FromHours(5.5)));
        Assert.Equal(AssignmentStatus.Active, assignment.Status);
        assignment.MarkDelivered(Now.AddMinutes(2).ToOffset(TimeSpan.FromHours(5.5)));

        Assert.Equal(AssignmentStatus.Delivered, assignment.Status);
        Assert.Equal(Now.AddMinutes(1), assignment.PickedUpAt);
        Assert.Equal(Now.AddMinutes(2), assignment.DeliveredAt);
        Assert.Equal(TimeSpan.Zero, assignment.PickedUpAt!.Value.Offset);
        Assert.Equal(TimeSpan.Zero, assignment.DeliveredAt!.Value.Offset);
    }

    [Fact]
    public void AssignmentRequiresAllIdentifiers()
    {
        Assert.Throws<ArgumentException>(() => new CourierAssignment(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), new(2026, 10, 28), Now));
        Assert.Throws<ArgumentException>(() => new CourierAssignment(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), new(2026, 10, 28), Now));
        Assert.Throws<ArgumentException>(() => new CourierAssignment(Guid.NewGuid(), Guid.NewGuid(), Guid.Empty, new(2026, 10, 28), Now));
    }

    [Fact]
    public void InvalidOrRepeatedPickupCannotChangeRecordedTimes()
    {
        var assignment = Assignment();
        Assert.Throws<ArgumentOutOfRangeException>(() => assignment.MarkPickedUp(Now.AddTicks(-1)));
        Assert.Null(assignment.PickedUpAt);
        assignment.MarkPickedUp(Now.AddMinutes(1));
        Assert.Throws<InvalidOperationException>(() => assignment.MarkPickedUp(Now.AddMinutes(2)));
        Assert.Equal(Now.AddMinutes(1), assignment.PickedUpAt);
    }

    [Fact]
    public void DeliveryNeedsPickupAndCannotPrecedeIt()
    {
        var assignment = Assignment();
        Assert.Throws<InvalidOperationException>(() => assignment.MarkDelivered(Now));
        assignment.MarkPickedUp(Now.AddMinutes(1));
        Assert.Throws<ArgumentOutOfRangeException>(() => assignment.MarkDelivered(Now));
        Assert.Equal(AssignmentStatus.Active, assignment.Status);
        Assert.Null(assignment.DeliveredAt);
    }

    [Theory]
    [InlineData(AssignmentStatus.Reassigned)]
    [InlineData(AssignmentStatus.Cancelled)]
    public void ReleaseIsOnlyAllowedBeforePickup(AssignmentStatus status)
    {
        var assignment = Assignment();
        assignment.MarkPickedUp(Now);
        Assert.Throws<InvalidOperationException>(() => Close(assignment, status));
        Assert.Equal(AssignmentStatus.Active, assignment.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void FailureMayCloseAnAssignmentBeforeOrAfterPickup(bool pickedUp)
    {
        var assignment = Assignment();
        if (pickedUp) assignment.MarkPickedUp(Now);
        assignment.MarkFailed();
        Assert.Equal(AssignmentStatus.Failed, assignment.Status);
        Assert.Null(assignment.DeliveredAt);
    }

    [Theory]
    [InlineData(AssignmentStatus.Reassigned)]
    [InlineData(AssignmentStatus.Delivered)]
    [InlineData(AssignmentStatus.Failed)]
    [InlineData(AssignmentStatus.Cancelled)]
    public void ClosedAssignmentsRetainOwnershipAndCannotBeReopened(AssignmentStatus status)
    {
        var assignment = Assignment();
        var courier = assignment.CourierId;
        var parcel = assignment.ParcelId;
        Close(assignment, status);

        Assert.Equal(status, assignment.Status);
        Assert.Equal(courier, assignment.CourierId);
        Assert.Equal(parcel, assignment.ParcelId);
        Assert.Throws<InvalidOperationException>(() => assignment.MarkPickedUp(Now));
        Assert.Throws<InvalidOperationException>(assignment.MarkReassigned);
        Assert.Throws<InvalidOperationException>(assignment.MarkCancelled);
        Assert.Throws<InvalidOperationException>(assignment.MarkFailed);
        Assert.Throws<InvalidOperationException>(() => assignment.MarkDelivered(Now));
    }

    private static CourierAssignment Assignment() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new(2026, 10, 28), Now);

    private static void Close(CourierAssignment assignment, AssignmentStatus status)
    {
        switch (status)
        {
            case AssignmentStatus.Reassigned: assignment.MarkReassigned(); break;
            case AssignmentStatus.Delivered:
                assignment.MarkPickedUp(Now);
                assignment.MarkDelivered(Now);
                break;
            case AssignmentStatus.Failed: assignment.MarkFailed(); break;
            case AssignmentStatus.Cancelled: assignment.MarkCancelled(); break;
            default: throw new ArgumentOutOfRangeException(nameof(status));
        }
    }
}
