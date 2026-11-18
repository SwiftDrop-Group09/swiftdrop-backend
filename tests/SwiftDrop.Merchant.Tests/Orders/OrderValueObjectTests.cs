using SwiftDrop.Merchant.Api.Domain.Orders;
using static SwiftDrop.Merchant.Tests.Orders.InboxJobTestData;

namespace SwiftDrop.Merchant.Tests.Orders;

public sealed class OrderValueObjectTests
{
    [Theory]
    [InlineData(1, 5)]
    [InlineData(2, 30)]
    [InlineData(3, 120)]
    [InlineData(4, 600)]
    [InlineData(5, 3600)]
    [InlineData(20, 3600)]
    public void RetryPolicy_EscalatesThenSettlesHourly(int failedAttempts, int expectedSeconds) =>
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), InboxJobRetryPolicy.DelayAfter(failedAttempts));

    [Fact]
    public void RetryPolicy_RejectsZeroAttempts() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => InboxJobRetryPolicy.DelayAfter(0));

    [Fact]
    public void RetryPolicy_AlertsFromTenthFailure()
    {
        Assert.False(InboxJobRetryPolicy.ShouldAlert(9));
        Assert.True(InboxJobRetryPolicy.ShouldAlert(10));
    }

    [Fact]
    public void ParcelStatuses_ClassifyTerminalStates()
    {
        Assert.True(ParcelStatuses.IsTerminal(ParcelStatuses.Delivered));
        Assert.True(ParcelStatuses.IsTerminal(ParcelStatuses.Failed));
        Assert.True(ParcelStatuses.IsTerminal(ParcelStatuses.Cancelled));
        Assert.False(ParcelStatuses.IsTerminal(ParcelStatuses.InTransit));
        Assert.False(ParcelStatuses.IsKnown("delivered"));
        Assert.Equal(8, ParcelStatuses.All.Count);
    }

    [Theory]
    [InlineData("Unknown", 1)]
    [InlineData("delivered", 1)]
    [InlineData(ParcelStatuses.Created, 0)]
    public void ParcelStatusSnapshot_RejectsUnknownStatusOrVersion(string status, long version) =>
        Assert.ThrowsAny<ArgumentException>(() => new ParcelStatusSnapshot(status, version, Now));

    [Theory]
    [InlineData("lkr")]
    [InlineData("LK")]
    [InlineData("LKRS")]
    public void BookingConfirmation_RejectsInvalidCurrency(string currency) =>
        Assert.Throws<ArgumentException>(() =>
            new BookingConfirmation(Guid.NewGuid(), "SD-1", Status(ParcelStatuses.Created, 1), 10m, currency, "v1"));

    [Fact]
    public void BookingConfirmation_RejectsNegativeTotalAndEmptyParcel()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new BookingConfirmation(Guid.NewGuid(), "SD-1", Status(ParcelStatuses.Created, 1), -1m, "LKR", "v1"));
        Assert.Throws<ArgumentException>(() =>
            new BookingConfirmation(Guid.Empty, "SD-1", Status(ParcelStatuses.Created, 1), 1m, "LKR", "v1"));
    }
}
