using Microsoft.Extensions.Configuration;
using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;

namespace SwiftDrop.Delivery.Tests.Courier;

public sealed class DeliveryReadApplicationTests
{
    private static readonly Guid ActorId = Guid.NewGuid();
    private static readonly DateTimeOffset Boundary = new(2026, 10, 23, 20, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(false, true, DeliveryReadFailure.Unauthenticated)]
    [InlineData(true, false, DeliveryReadFailure.Forbidden)]
    public async Task ManifestAuthorizationPrecedesValidationAndStore(bool authenticated, bool courier, DeliveryReadFailure failure)
    {
        var store = new Store();
        var result = await Handler(new User(authenticated, ActorId, courier), store).GetAssignmentsAsync(new(Date: "invalid"));
        Assert.Equal(failure, result.Failure);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task DefaultServiceDayUsesConfiguredTimezoneRatherThanUtcOrClientDate()
    {
        var store = new Store();
        var result = await Handler(new User(true, ActorId, true), store).GetAssignmentsAsync(new());
        Assert.Null(result.Failure);
        Assert.Equal(new AssignmentQuery(new(2026, 10, 24), true, 1, 20), store.Query);
        Assert.Equal(ActorId, store.CourierId);
        var utc = new Store();
        await Handler(new User(true, ActorId, true), utc, "UTC").GetAssignmentsAsync(new());
        Assert.Equal(new DateOnly(2026, 10, 23), utc.Query!.Date);
    }

    [Fact]
    public async Task ExplicitServiceDayAndPaginationArePassedWithoutIdentityOverrides()
    {
        var store = new Store();
        await Handler(new User(true, ActorId, true), store).GetAssignmentsAsync(new("2026-10-20", "false", "3", "100"));
        Assert.Equal(new AssignmentQuery(new(2026, 10, 20), false, 3, 100), store.Query);
        Assert.Equal(ActorId, store.CourierId);
    }

    [Fact]
    public async Task InvalidTimezoneReturnsSafeDependencyFailureWithoutCallingStore()
    {
        var store = new Store();
        var result = await Handler(new User(true, ActorId, true), store, "not-a-timezone").GetAssignmentsAsync(new());
        Assert.Equal(DeliveryReadFailure.Unavailable, result.Failure);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task SharedDetailScopesCustomerCourierAndOperationsSeparately()
    {
        var store = new Store();
        var result = await Handler(new User(true, ActorId, false, true), store).GetParcelAsync(Guid.NewGuid().ToString());
        Assert.Equal(DeliveryReadFailure.NotFound, result.Failure);
        Assert.Equal(new ParcelReadScope(ActorId, false, true, false), store.Scope);
        store = new Store();
        await Handler(new User(true, ActorId, true, false, true), store).GetParcelAsync(Guid.NewGuid().ToString());
        Assert.Equal(new ParcelReadScope(ActorId, true, false, true), store.Scope);
    }

    [Theory]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task MalformedParcelIdsDoNotReachStorage(string id)
    {
        var store = new Store();
        var result = await Handler(new User(true, ActorId, true), store).GetParcelAsync(id);
        Assert.Equal(DeliveryReadFailure.NotFound, result.Failure);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task DependencyFailuresAreMappedAndCancellationPropagates()
    {
        var store = new Store { Error = new DeliveryReadUnavailableException("private dependency details") };
        var handler = Handler(new User(true, ActorId, true), store);
        Assert.Equal(DeliveryReadFailure.Unavailable, (await handler.GetAssignmentsAsync(new())).Failure);
        Assert.Equal(DeliveryReadFailure.Unavailable, (await handler.GetParcelAsync(Guid.NewGuid().ToString())).Failure);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        store.Error = new OperationCanceledException(cancellation.Token);
        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.GetAssignmentsAsync(new(), cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(() => handler.GetParcelAsync(Guid.NewGuid().ToString(), cancellationToken: cancellation.Token));
    }

    private static DeliveryReadHandler Handler(User user, Store store, string timezone = "Asia/Colombo") =>
        new(user, store, new OperationsClock(new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["Operations:TimeZone"] = timezone }).Build(), new Clock()));

    private sealed record User(bool IsAuthenticated, Guid? UserId, bool IsCourier, bool IsCustomer = false,
        bool IsOperations = false) : ICurrentDeliveryUser;
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Boundary; }
    private sealed class Store : IDeliveryReadStore
    {
        public int Calls { get; private set; }
        public Guid CourierId { get; private set; }
        public AssignmentQuery? Query { get; private set; }
        public ParcelReadScope? Scope { get; private set; }
        public Exception? Error { get; set; }

        public Task<AssignedParcelPage> GetAssignmentsAsync(Guid courierId, AssignmentQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            if (Error is not null) throw Error;
            CourierId = courierId;
            Query = query;
            return Task.FromResult(new AssignedParcelPage([], 0, query.Page, query.PageSize));
        }

        public Task<ParcelDetailResponse?> GetParcelAsync(ParcelReadScope scope, Guid parcelId, CancellationToken cancellationToken)
        {
            Calls++;
            if (Error is not null) throw Error;
            Scope = scope;
            return Task.FromResult<ParcelDetailResponse?>(null);
        }
    }
}
