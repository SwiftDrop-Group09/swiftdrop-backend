using SwiftDrop.Merchant.Api.Application.Orders.Dashboard;

namespace SwiftDrop.Merchant.Tests.Orders.Dashboard;

public sealed class DashboardRulesTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 40, 0, TimeSpan.Zero);

    // ── Rate formula (shared with the admin report) ────────────────────────

    [Theory]
    [InlineData(15, 2, 0.8824, 0.1176)]
    [InlineData(2, 1, 0.6667, 0.3333)]
    [InlineData(1, 0, 1.0, 0.0)]
    [InlineData(0, 3, 0.0, 1.0)]
    [InlineData(0, 0, 0.0, 0.0)]
    public void Rates_UseDeliveredAndFailedOnly_RoundedToFourPlaces(int delivered, int failed, double success, double failure)
    {
        var (successRate, failureRate) = DeliveryRates.Calculate(delivered, failed);

        Assert.Equal((decimal)success, successRate);
        Assert.Equal((decimal)failure, failureRate);
    }

    [Fact]
    public void Rates_RejectNegativeCounts() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => DeliveryRates.Calculate(-1, 0));

    // ── DateQuery ──────────────────────────────────────────────────────────

    [Fact]
    public void NoDates_DefaultsToTheCurrentUtcDay()
    {
        var range = DashboardDateRange.Parse(new DashboardQuery(null, null), Now);

        Assert.True(range.IsValid);
        Assert.Equal(new DateTimeOffset(2026, 11, 13, 0, 0, 0, TimeSpan.Zero), range.Value!.From);
        Assert.Equal(new DateTimeOffset(2026, 11, 14, 0, 0, 0, TimeSpan.Zero), range.Value.To);
    }

    [Fact]
    public void ExplicitRange_IsNormalisedToUtc()
    {
        var range = DashboardDateRange.Parse(new DashboardQuery("2026-11-01T05:30:00+05:30", "2026-11-14T00:00:00Z"), Now);

        Assert.True(range.IsValid);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero), range.Value!.From);
        Assert.Equal(TimeSpan.Zero, range.Value.From.Offset);
    }

    [Fact]
    public void ExactlyMaximumLength_IsAllowed() =>
        Assert.True(DashboardDateRange.Parse(new DashboardQuery("2026-01-01T00:00:00Z", "2027-01-02T00:00:00Z"), Now).IsValid);

    [Theory]
    [InlineData("2026-01-01T00:00:00Z", "2027-01-03T00:00:00Z", "to")]   // 367 days
    [InlineData("2026-11-14T00:00:00Z", "2026-11-01T00:00:00Z", "to")]   // reversed
    [InlineData("2026-11-01T00:00:00Z", "2026-11-01T00:00:00Z", "to")]   // empty
    [InlineData("2026-11-01T00:00:00Z", null, "to")]                     // only one bound
    [InlineData("yesterday", "2026-11-14T00:00:00Z", "from")]            // not a timestamp
    public void InvalidRanges_AreRejectedWithAFieldError(string? from, string? to, string field)
    {
        var range = DashboardDateRange.Parse(new DashboardQuery(from, to), Now);

        Assert.False(range.IsValid);
        Assert.True(range.Errors.ContainsKey(field));
    }
}
