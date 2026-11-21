using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Application.Dispatch;

namespace SwiftDrop.Delivery.Tests.Dispatch;

public sealed class CourierWorkloadApplicationTests
{
    [Theory]
    [InlineData(false, true, DeliveryReadFailure.Unauthenticated)]
    [InlineData(true, false, DeliveryReadFailure.Forbidden)]
    public async Task AuthorizationPrecedesInputValidationAndAnyStorageAccess(bool authenticated, bool operations,
        DeliveryReadFailure failure)
    {
        var store = new Store();
        var handler = new CourierWorkloadHandler(new User(authenticated, operations), store);
        Assert.Equal(failure, (await handler.GetWorkloadAsync(new(Page: "bad"))).Failure);
        Assert.Equal(failure, (await handler.GetAssignmentsAsync("bad", new(InvalidParameters: true))).Failure);
        Assert.Equal(0, store.Calls);
    }

    [Theory]
    [InlineData("0", "20")]
    [InlineData("-1", "20")]
    [InlineData("bad", "20")]
    [InlineData("1", "0")]
    [InlineData("1", "101")]
    [InlineData("2147483647", "100")]
    public async Task InvalidPagesNeverReachStorage(string page, string size)
    {
        var store = new Store();
        var handler = new CourierWorkloadHandler(new User(), store);
        Assert.Equal(DeliveryReadFailure.InvalidInput, (await handler.GetWorkloadAsync(new(page, size))).Failure);
        Assert.Equal(DeliveryReadFailure.InvalidInput, (await handler.GetAssignmentsAsync(Guid.NewGuid().ToString(), new(page, size))).Failure);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task DefaultsAndExplicitPagesAreSharedByBothReads()
    {
        var store = new Store();
        var handler = new CourierWorkloadHandler(new User(), store);
        var response = await handler.GetWorkloadAsync(new());
        Assert.Null(response.Failure);
        Assert.Equal((1, 20), store.Page);
        var id = Guid.NewGuid();
        Assert.Null((await handler.GetAssignmentsAsync(id.ToString(), new("3", "100"))).Failure);
        Assert.Equal((3, 100), store.Page);
        Assert.Equal(id, store.Courier);
    }

    [Theory]
    [InlineData("bad")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task MalformedCourierReturnsNotFoundWithoutStorage(string id)
    {
        var store = new Store();
        Assert.Equal(DeliveryReadFailure.NotFound,
            (await new CourierWorkloadHandler(new User(), store).GetAssignmentsAsync(id, new())).Failure);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task StorageFailureAndUnknownCourierHaveSafeDistinctResults()
    {
        var store = new Store { Missing = true };
        var handler = new CourierWorkloadHandler(new User(), store);
        Assert.Equal(DeliveryReadFailure.NotFound, (await handler.GetAssignmentsAsync(Guid.NewGuid().ToString(), new())).Failure);
        store.Unavailable = true;
        Assert.Equal(DeliveryReadFailure.Unavailable, (await handler.GetWorkloadAsync(new())).Failure);
        Assert.Equal(DeliveryReadFailure.Unavailable, (await handler.GetAssignmentsAsync(Guid.NewGuid().ToString(), new())).Failure);
    }

    private sealed class User(bool authenticated = true, bool operations = true) : ICurrentDeliveryUser
    {
        public bool IsAuthenticated => authenticated;
        public Guid? UserId => Guid.Parse("00000000-0000-4000-8000-000000000001");
        public bool IsCourier => false;
        public bool IsCustomer => false;
        public bool IsOperations => operations;
    }

    private sealed class Store : IWorkloadReadStore
    {
        public int Calls;
        public (int, int) Page;
        public Guid Courier;
        public bool Unavailable;
        public bool Missing;
        public Task<WorkloadPage> GetWorkloadAsync(int page, int pageSize, CancellationToken cancellationToken)
        {
            Calls++;
            Page = (page, pageSize);
            if (Unavailable) throw new DeliveryReadUnavailableException("private storage diagnostic");
            return Task.FromResult(new WorkloadPage([], 0, page, pageSize));
        }
        public Task<DispatchAssignmentsPage?> GetAssignmentsAsync(Guid courierId, int page, int pageSize, CancellationToken cancellationToken)
        {
            Calls++;
            Courier = courierId;
            Page = (page, pageSize);
            if (Unavailable) throw new DeliveryReadUnavailableException("private storage diagnostic");
            return Task.FromResult(Missing ? null : new DispatchAssignmentsPage(courierId, [], 0, page, pageSize));
        }
    }
}
