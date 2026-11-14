using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.Admin.Pricing;
using SwiftDrop.Delivery.Api.Application.Admin.Reports;
using SwiftDrop.Delivery.Api.Application.Admin.Zones;
using Xunit;

namespace SwiftDrop.Delivery.Tests.Admin;

public sealed class AdminDeliveryConfigurationAndReportTests
{
    private sealed class TestDeliveryUser(bool isAuthenticated, bool isAdmin, bool isOperations, Guid? userId = null) : ICurrentDeliveryUser
    {
        public bool IsAuthenticated => isAuthenticated;
        public Guid? UserId => userId ?? (isAuthenticated ? Guid.NewGuid() : null);
        public bool IsCourier => false;
        public bool IsCustomer => false;
        public bool IsOperations => isOperations;
        public bool IsAdmin => isAdmin;
    }

    private sealed class MockZoneStore : IZoneAdministrationStore
    {
        private readonly List<ZoneDto> _zones = [];

        public Task<ZonePage> ListZonesAsync(int page, int pageSize, CancellationToken cancellationToken, ZoneQuery? filters = null)
        {
            var paged = _zones.Skip((page - 1) * pageSize).Take(pageSize).ToList();
            return Task.FromResult(new ZonePage(paged, _zones.Count, page, pageSize));
        }

        public Task<ZoneDto?> GetZoneByIdAsync(Guid id, CancellationToken cancellationToken) =>
            Task.FromResult(_zones.FirstOrDefault(z => z.Id == id));

        public Task<ZoneResult<ZoneDto>> CreateZoneAsync(ZoneInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var (_, requestedCodes) = ZoneBoundaryHelper.DeserializeBoundaries(
                ZoneBoundaryHelper.SerializeBoundaries(input.City, input.ServicePostalCodes));

            if (input.IsActive && requestedCodes.Length > 0)
            {
                foreach (var z in _zones.Where(z => z.IsActive))
                {
                    var overlap = requestedCodes.Intersect(z.ServicePostalCodes, StringComparer.OrdinalIgnoreCase).ToArray();
                    if (overlap.Length > 0)
                    {
                        return Task.FromResult(new ZoneResult<ZoneDto>(null, ZoneFailure.OverlappingPostalCodes,
                            new Dictionary<string, string[]> { ["servicePostalCodes"] = ["Overlap detected"] }));
                    }
                }
            }

            var dto = new ZoneDto(Guid.NewGuid(), input.Name, input.City, requestedCodes, input.IsActive, 1);
            _zones.Add(dto);
            return Task.FromResult(new ZoneResult<ZoneDto>(dto));
        }

        public Task<ZoneResult<ZoneDto>> UpdateZoneAsync(Guid id, ZoneInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var existing = _zones.FirstOrDefault(z => z.Id == id);
            if (existing is null)
            {
                return Task.FromResult(new ZoneResult<ZoneDto>(null, ZoneFailure.NotFound));
            }

            var (_, requestedCodes) = ZoneBoundaryHelper.DeserializeBoundaries(
                ZoneBoundaryHelper.SerializeBoundaries(input.City, input.ServicePostalCodes));

            var updated = new ZoneDto(id, input.Name, input.City, requestedCodes, input.IsActive, existing.Version + 1);
            _zones.Remove(existing);
            _zones.Add(updated);
            return Task.FromResult(new ZoneResult<ZoneDto>(updated));
        }

        public Task<ZoneSummaryResponse> GetZoneSummaryAsync(DateTimeOffset? from, DateTimeOffset? to, CancellationToken cancellationToken)
        {
            var items = _zones.Select(z => new ZoneSummaryItemDto(z.Id, z.Name, new Dictionary<string, int>(), 0)).ToList();
            return Task.FromResult(new ZoneSummaryResponse(items));
        }
    }

    private sealed class MockPricingStore : IPricingAdministrationStore
    {
        public Task<PricingOverviewPage> ListOverviewAsync(int page, int pageSize, Guid? zoneId, CancellationToken cancellationToken)
            => Task.FromResult(new PricingOverviewPage([], 0, page, pageSize));
        private readonly List<PricingTierDto> _tiers = [];

        public Task<PricingPage> ListPricingAsync(int page, int pageSize, Guid? zoneId, CancellationToken cancellationToken)
        {
            var filtered = zoneId.HasValue ? _tiers.Where(t => t.ZoneId == zoneId.Value) : _tiers;
            var list = filtered.ToList();
            return Task.FromResult(new PricingPage(list.Skip((page - 1) * pageSize).Take(pageSize).ToList(), list.Count, page, pageSize));
        }

        public Task<PricingResult<PricingTierDto>> CreatePricingTierAsync(PricingTierInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var dto = new PricingTierDto(Guid.NewGuid(), input.ZoneId, "Colombo Metro", input.Size,
                input.PricingVersion, input.BaseRate, input.PerKgRate, input.Currency, input.IsActive);
            _tiers.Add(dto);
            return Task.FromResult(new PricingResult<PricingTierDto>(dto));
        }

        public Task<PricingResult<PricingTierDto>> UpdatePricingTierAsync(Guid id, PricingTierUpdateInput input, Guid actorId, DateTimeOffset now, CancellationToken cancellationToken)
        {
            var tier = _tiers.FirstOrDefault(t => t.Id == id);
            if (tier is null) return Task.FromResult(new PricingResult<PricingTierDto>(null, PricingFailure.NotFound));
            var updated = tier with { IsActive = input.IsActive };
            _tiers.Remove(tier);
            _tiers.Add(updated);
            return Task.FromResult(new PricingResult<PricingTierDto>(updated));
        }
    }

    private sealed class MockReportStore(int delivered, int failed, int cancelled) : IDeliveryReportStore
    {
        public Task<DeliveryReportResponse> GenerateReportAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
        {
            var terminalCount = delivered + failed;
            var successRate = terminalCount > 0 ? decimal.Round((decimal)delivered / terminalCount, 4) : 0m;
            var failureRate = terminalCount > 0 ? decimal.Round((decimal)failed / terminalCount, 4) : 0m;

            return Task.FromResult(new DeliveryReportResponse(
                From: from,
                To: to,
                Delivered: delivered,
                Failed: failed,
                Cancelled: cancelled,
                TerminalCount: terminalCount,
                SuccessRate: successRate,
                FailureRate: failureRate,
                Days: [new DailyReportMetric("2026-10-25", delivered, failed, cancelled)]));
        }
    }

    [Fact]
    public async Task TC_A04_ZoneConfig_EnforcesAdminAuthorization()
    {
        var nonAdmin = new TestDeliveryUser(isAuthenticated: true, isAdmin: false, isOperations: false);
        var handler = new ZoneAdministrationHandler(new MockZoneStore(), nonAdmin);

        var listResult = await handler.ListAsync(1, 20, CancellationToken.None);
        Assert.Equal(ZoneFailure.Forbidden, listResult.Failure);

        var createResult = await handler.CreateAsync(new ZoneInput("Zone A", "Colombo", ["00100"]), CancellationToken.None);
        Assert.Equal(ZoneFailure.Forbidden, createResult.Failure);
    }

    [Fact]
    public async Task TC_A04_ZoneConfig_RejectsOverlappingPostalCodes()
    {
        var admin = new TestDeliveryUser(isAuthenticated: true, isAdmin: true, isOperations: true);
        var store = new MockZoneStore();
        var handler = new ZoneAdministrationHandler(store, admin);

        var zone1 = await handler.CreateAsync(new ZoneInput("Zone 1", "Colombo", ["00100", "00200"]), CancellationToken.None);
        Assert.NotNull(zone1.Response);

        // Attempting to create Zone 2 with overlapping code "00200"
        var zone2 = await handler.CreateAsync(new ZoneInput("Zone 2", "Colombo", ["00200", "00300"]), CancellationToken.None);
        Assert.Equal(ZoneFailure.OverlappingPostalCodes, zone2.Failure);
    }

    [Fact]
    public async Task TC_A05_PricingConfig_RejectsNegativeFees()
    {
        var admin = new TestDeliveryUser(isAuthenticated: true, isAdmin: true, isOperations: true);
        var handler = new PricingAdministrationHandler(new MockPricingStore(), admin);

        var resultNegativeBase = await handler.CreateAsync(new PricingTierInput(Guid.NewGuid(), "Small", "v1", -10m, 50m), CancellationToken.None);
        Assert.Equal(PricingFailure.InvalidInput, resultNegativeBase.Failure);
        Assert.True(resultNegativeBase.Errors?.ContainsKey("baseRate"));

        var resultNegativePerKg = await handler.CreateAsync(new PricingTierInput(Guid.NewGuid(), "Small", "v1", 450m, -5m), CancellationToken.None);
        Assert.Equal(PricingFailure.InvalidInput, resultNegativePerKg.Failure);
        Assert.True(resultNegativePerKg.Errors?.ContainsKey("perKgRate"));
    }

    [Fact]
    public async Task TC_A05_PricingConfig_EnforcesValidSizeAndLkrCurrency()
    {
        var admin = new TestDeliveryUser(isAuthenticated: true, isAdmin: true, isOperations: true);
        var handler = new PricingAdministrationHandler(new MockPricingStore(), admin);

        var invalidSize = await handler.CreateAsync(new PricingTierInput(Guid.NewGuid(), "Giant", "v1", 450m, 50m), CancellationToken.None);
        Assert.Equal(PricingFailure.InvalidInput, invalidSize.Failure);

        var invalidCurrency = await handler.CreateAsync(new PricingTierInput(Guid.NewGuid(), "Small", "v1", 450m, 50m, "USD"), CancellationToken.None);
        Assert.Equal(PricingFailure.InvalidInput, invalidCurrency.Failure);
    }

    [Fact]
    public async Task TC_A06_DeliveryReport_CalculatesTerminalRatesAndExcludesCancelledFromDenominator()
    {
        var admin = new TestDeliveryUser(isAuthenticated: true, isAdmin: true, isOperations: true);
        var store = new MockReportStore(delivered: 90, failed: 10, cancelled: 5);
        var handler = new DeliveryReportHandler(store, admin);

        var result = await handler.ExecuteAsync("2026-10-01T00:00:00Z", "2026-10-31T00:00:00Z", CancellationToken.None);

        Assert.NotNull(result.Response);
        var report = result.Response;
        Assert.Equal(90, report.Delivered);
        Assert.Equal(10, report.Failed);
        Assert.Equal(5, report.Cancelled);
        Assert.Equal(100, report.TerminalCount); // 90 + 10 = 100, cancelled excluded
        Assert.Equal(0.9000m, report.SuccessRate); // 90 / 100
        Assert.Equal(0.1000m, report.FailureRate); // 10 / 100
    }

    [Fact]
    public async Task TC_A06_DeliveryReport_HandlesZeroTerminalDeliveriesWithoutDivideByZero()
    {
        var admin = new TestDeliveryUser(isAuthenticated: true, isAdmin: true, isOperations: true);
        var store = new MockReportStore(delivered: 0, failed: 0, cancelled: 3);
        var handler = new DeliveryReportHandler(store, admin);

        var result = await handler.ExecuteAsync("2026-10-01T00:00:00Z", "2026-10-31T00:00:00Z", CancellationToken.None);

        Assert.NotNull(result.Response);
        var report = result.Response;
        Assert.Equal(0, report.TerminalCount);
        Assert.Equal(0m, report.SuccessRate);
        Assert.Equal(0m, report.FailureRate);
    }

    [Fact]
    public async Task TC_A06_DeliveryReport_RejectsInvalidDateRanges()
    {
        var admin = new TestDeliveryUser(isAuthenticated: true, isAdmin: true, isOperations: true);
        var handler = new DeliveryReportHandler(new MockReportStore(0, 0, 0), admin);

        Assert.Equal(ReportFailure.InvalidDateRange,
            (await handler.ExecuteAsync(null, null, CancellationToken.None)).Failure);
        Assert.Equal(ReportFailure.InvalidDateRange,
            (await handler.ExecuteAsync("2026-10-01T00:00:00Z", null, CancellationToken.None)).Failure);
        Assert.Equal(ReportFailure.InvalidDateRange,
            (await handler.ExecuteAsync(null, "2026-10-02T00:00:00Z", CancellationToken.None)).Failure);

        var localOffset = await handler.ExecuteAsync(
            "2026-10-01T00:00:00+05:30", "2026-10-02T00:00:00+05:30", CancellationToken.None);
        Assert.Equal(ReportFailure.InvalidDateRange, localOffset.Failure);

        var invalidCalendarDate = await handler.ExecuteAsync(
            "2026-02-30T00:00:00Z", "2026-03-01T00:00:00Z", CancellationToken.None);
        Assert.Equal(ReportFailure.InvalidDateRange, invalidCalendarDate.Failure);

        // to <= from
        var reversedDates = await handler.ExecuteAsync("2026-10-31T00:00:00Z", "2026-10-01T00:00:00Z", CancellationToken.None);
        Assert.Equal(ReportFailure.InvalidDateRange, reversedDates.Failure);

        // Range > 366 days
        var overMaxRange = await handler.ExecuteAsync("2024-01-01T00:00:00Z", "2026-01-01T00:00:00Z", CancellationToken.None);
        Assert.Equal(ReportFailure.InvalidDateRange, overMaxRange.Failure);
    }
}
