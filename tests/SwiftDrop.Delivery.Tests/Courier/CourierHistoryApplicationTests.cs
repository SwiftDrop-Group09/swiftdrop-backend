using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;

namespace SwiftDrop.Delivery.Tests.Courier;

public sealed class CourierHistoryApplicationTests
{
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task AuthorizationPrecedesInvalidPaginationAndStoreAccess(bool authenticated, bool courier)
    {
        var store = new Store();
        var result = await new CourierHistoryHandler(new User(authenticated, Guid.NewGuid(), courier), store)
            .ExecuteAsync(new("invalid", "0", true));
        Assert.Equal(authenticated ? DeliveryReadFailure.Forbidden : DeliveryReadFailure.Unauthenticated, result.Failure);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task DefaultsAndExplicitPagesUseOnlyTheTrustedSubject()
    {
        var actor = Guid.NewGuid();
        var store = new Store();
        var handler = new CourierHistoryHandler(new User(true, actor, true), store);
        var result = await handler.ExecuteAsync(new());
        Assert.Null(result.Failure);
        Assert.Equal(actor, store.Actor);
        Assert.Equal(new DeliveryPageQuery(1, 20), store.Query);
        await handler.ExecuteAsync(new("3", "100"));
        Assert.Equal(new DeliveryPageQuery(3, 100), store.Query);
        Assert.Equal(actor, store.Actor);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task MissingOrEmptyIdentityFailsBeforeStore(string? subject)
    {
        var store = new Store();
        var result = await new CourierHistoryHandler(new User(true, subject is null ? null : Guid.Parse(subject), true), store).ExecuteAsync(new());
        Assert.Equal(DeliveryReadFailure.Unauthenticated, result.Failure);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("0", "20")]
    [InlineData("-1", "20")]
    [InlineData("1.5", "20")]
    [InlineData("", "20")]
    [InlineData("2147483648", "20")]
    [InlineData("2147483647", "100")]
    [InlineData("1", "0")]
    [InlineData("1", "101")]
    public async Task InvalidOrOverflowingPagesNeverReachTheDatabase(string page, string size)
    {
        var store = new Store();
        var result = await new CourierHistoryHandler(new User(true, Guid.NewGuid(), true), store).ExecuteAsync(new(page, size));
        Assert.Equal(DeliveryReadFailure.InvalidInput, result.Failure);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task DependencyErrorsAreSafeAndCancellationKeepsItsOriginalToken()
    {
        var store = new Store { Error = new DeliveryReadUnavailableException("private connection text") };
        var handler = new CourierHistoryHandler(new User(true, Guid.NewGuid(), true), store);
        Assert.Equal(DeliveryReadFailure.Unavailable, (await handler.ExecuteAsync(new())).Failure);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        store.Error = new OperationCanceledException(cancellation.Token);
        var error = await Assert.ThrowsAsync<OperationCanceledException>(() => handler.ExecuteAsync(new(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private sealed record User(bool IsAuthenticated, Guid? UserId, bool IsCourier) : ICurrentDeliveryUser
    {
        public bool IsCustomer => false;
        public bool IsOperations => false;
    }

    private sealed class Store : ICourierHistoryStore
    {
        public int Calls;
        public Guid Actor;
        public DeliveryPageQuery? Query;
        public Exception? Error;
        public Task<CourierHistoryPage> GetHistoryAsync(Guid actor, DeliveryPageQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            Actor = actor;
            Query = query;
            return Error is { } error ? Task.FromException<CourierHistoryPage>(error)
                : Task.FromResult(new CourierHistoryPage([], 0, query.Page, query.PageSize));
        }
    }
}
