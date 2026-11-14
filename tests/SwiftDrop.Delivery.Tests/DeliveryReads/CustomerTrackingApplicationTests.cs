using System.Text;
using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Tests.DeliveryReads;

public sealed class CustomerTrackingApplicationTests
{
    private static readonly Guid CustomerId = Guid.NewGuid();
    private static readonly Guid ParcelId = Guid.NewGuid();

    [Theory]
    [InlineData(false, false, false, DeliveryReadFailure.Unauthenticated)]
    [InlineData(true, false, false, DeliveryReadFailure.Forbidden)]
    public async Task ListParcelsAuthorizationPrecedesValidationAndStore(
        bool authenticated, bool isCustomer, bool isOperations, DeliveryReadFailure expected)
    {
        var store = new FakeCustomerReadStore();
        var handler = CreateHandler(new FakeUser(authenticated, CustomerId, isCustomer, isOperations), store);

        var result = await handler.ListParcelsAsync(new(Status: "not_a_status"));

        Assert.Equal(expected, result.Failure);
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task ListParcelsRejectsInvalidDateRangeAndStatus()
    {
        var store = new FakeCustomerReadStore();
        var handler = CreateHandler(new FakeUser(true, CustomerId, isCustomer: true), store);

        var result = await handler.ListParcelsAsync(new(
            Status: "invalid",
            From: "2026-10-20T00:00:00Z",
            To: "2026-10-19T00:00:00Z",
            Page: "0",
            PageSize: "500"));

        Assert.Equal(DeliveryReadFailure.InvalidInput, result.Failure);
        Assert.NotNull(result.Errors);
        Assert.True(result.Errors!.ContainsKey("status"));
        Assert.True(result.Errors.ContainsKey("from"));
        Assert.True(result.Errors.ContainsKey("page"));
        Assert.True(result.Errors.ContainsKey("pageSize"));
        Assert.Equal(0, store.Calls);
    }

    [Fact]
    public async Task ListParcelsForwardsValidatedFiltersAndScope()
    {
        var store = new FakeCustomerReadStore();
        var handler = CreateHandler(new FakeUser(true, CustomerId, isCustomer: true), store);

        var result = await handler.ListParcelsAsync(new(
            Status: "delivered",
            From: "2026-10-01T00:00:00Z",
            To: "2026-10-31T00:00:00Z",
            Search: "TRK-123",
            Page: "2",
            PageSize: "10"));

        Assert.Null(result.Failure);
        Assert.NotNull(result.Response);
        Assert.NotNull(store.LastScope);
        Assert.Equal(CustomerId, store.LastScope!.UserId);
        Assert.True(store.LastScope.IsCustomer);
        Assert.Equal(ParcelStatus.Delivered, store.LastQuery!.Status);
        Assert.Equal("TRK-123", store.LastQuery.Search);
        Assert.Equal(2, store.LastQuery.Page);
        Assert.Equal(10, store.LastQuery.PageSize);
    }

    [Fact]
    public async Task PublicTrackingReturnsSummaryWithoutAuthentication()
    {
        var store = new FakeCustomerReadStore();
        var handler = CreateHandler(new FakeUser(false, null), store);

        var result = await handler.GetPublicTrackingAsync("TRK-PUBLIC-1");

        Assert.Null(result.Failure);
        Assert.NotNull(result.Response);
        Assert.Equal("TRK-PUBLIC-1", result.Response!.TrackingNumber);
        Assert.Equal("InTransit", result.Response.Status);
    }

    [Fact]
    public async Task ReceiptReturnsConflictWhenParcelIsNotDelivered()
    {
        var store = new FakeCustomerReadStore
        {
            ReceiptLookup = new ParcelReceiptLookup(ParcelStatus.InTransit, null)
        };
        var handler = CreateHandler(new FakeUser(true, CustomerId, isCustomer: true), store);

        var result = await handler.GetReceiptAsync(ParcelId.ToString());

        Assert.Equal(DeliveryReadFailure.Conflict, result.Failure);
    }

    [Fact]
    public async Task ReceiptPdfRendersValidPdfBytesWhenDelivered()
    {
        var store = new FakeCustomerReadStore
        {
            ReceiptLookup = new ParcelReceiptLookup(ParcelStatus.Delivered, SampleReceipt())
        };
        var handler = CreateHandler(new FakeUser(true, CustomerId, isCustomer: true), store);

        var result = await handler.GetReceiptPdfAsync(ParcelId.ToString());

        Assert.Null(result.Failure);
        Assert.NotNull(result.Response);
        Assert.Equal("swiftdrop-receipt-TRK-TEST-001.pdf", result.Response!.FileName);
        var header = Encoding.ASCII.GetString(result.Response.Content, 0, 5);
        Assert.Equal("%PDF-", header);
    }

    [Fact]
    public async Task ProofReturnsConflictWhenNotDeliveredAndBytesWhenDelivered()
    {
        var store = new FakeCustomerReadStore
        {
            ProofLookup = new ParcelProofLookup(ParcelStatus.OutForDelivery, "TRK-TEST-001", [])
        };
        var reader = new FakeProofReader();
        var handler = CreateHandler(new FakeUser(true, CustomerId, isCustomer: true), store, reader);

        var conflict = await handler.GetProofAsync(ParcelId.ToString());
        Assert.Equal(DeliveryReadFailure.Conflict, conflict.Failure);

        store.ProofLookup = new ParcelProofLookup(ParcelStatus.Delivered, "TRK-TEST-001",
            [new ProofOfDeliveryItem(Guid.NewGuid(), "Photo", "Nimali Perera", DateTimeOffset.UtcNow, "proofs/test.jpg")]);
        var image = await handler.GetProofImageAsync(ParcelId.ToString());

        Assert.Null(image.Failure);
        Assert.NotNull(image.Response);
        Assert.Equal("image/jpeg", image.Response!.ContentType);
        Assert.Equal([1, 2, 3, 4], image.Response.Content);
    }

    [Fact]
    public async Task DispatcherCanTrackButCannotReadReceiptOrProofInAnyFormat()
    {
        var store = new FakeCustomerReadStore();
        var handler = CreateHandler(new FakeUser(true, CustomerId, isOperations: true), store);
        Assert.Null((await handler.ListParcelsAsync(new())).Failure);
        Assert.True(store.LastScope!.IsOperations);
        var calls = store.Calls;
        Assert.Equal(DeliveryReadFailure.Forbidden, (await handler.GetReceiptAsync("invalid")).Failure);
        Assert.Equal(DeliveryReadFailure.Forbidden, (await handler.GetReceiptPdfAsync(ParcelId.ToString())).Failure);
        Assert.Equal(DeliveryReadFailure.Forbidden, (await handler.GetProofAsync(ParcelId.ToString())).Failure);
        Assert.Equal(DeliveryReadFailure.Forbidden, (await handler.GetProofImageAsync(ParcelId.ToString())).Failure);
        Assert.Equal(calls, store.Calls);
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    public async Task ReceiptAndProofUseAdminOrOwnerScopeEvenForCombinedDispatcherCustomer(
        bool admin, bool customer, bool unrestricted)
    {
        var store = new FakeCustomerReadStore();
        var handler = CreateHandler(new FakeUser(true, CustomerId, customer, true, isAdmin: admin), store);
        await handler.GetReceiptAsync(ParcelId.ToString());
        Assert.Equal(unrestricted, store.LastScope!.IsOperations);
        Assert.Equal(customer, store.LastScope.IsCustomer);
        Assert.False(store.LastScope.IsCourier);
        await handler.GetProofAsync(ParcelId.ToString());
        Assert.Equal(unrestricted, store.LastScope!.IsOperations);
        Assert.Equal(customer, store.LastScope.IsCustomer);
    }

    private static CustomerTrackingHandler CreateHandler(
        ICurrentDeliveryUser user,
        ICustomerDeliveryReadStore store,
        IPrivateProofReader? proofReader = null) =>
        new(user, store, new SkiaReceiptPdfRenderer(), proofReader ?? new FakeProofReader());

    private static ParcelReceiptResponse SampleReceipt() => new(
        "RCP-TRK-TEST-001",
        ParcelId,
        "TRK-TEST-001",
        "Delivered",
        DateTimeOffset.UtcNow.AddHours(-3),
        DateTimeOffset.UtcNow,
        new NormalizedAddress("12 Galle Road", "Colombo", "00300", Guid.NewGuid()),
        new NormalizedAddress("45 Peradeniya Road", "Kandy", "20000", Guid.NewGuid()),
        new NormalizedRecipient("Nimali Perera", "+94771234567"),
        new ReadPackageDetails(2.5m, "Medium"),
        450.00m,
        90.00m,
        540.00m,
        "LKR",
        "v1",
        DateTimeOffset.UtcNow.AddHours(-3));

    private sealed class FakeUser(
        bool isAuthenticated,
        Guid? userId,
        bool isCustomer = false,
        bool isOperations = false,
        bool isCourier = false, bool isAdmin = false) : ICurrentDeliveryUser
    {
        public bool IsAuthenticated { get; } = isAuthenticated;
        public Guid? UserId { get; } = userId;
        public bool IsCourier { get; } = isCourier;
        public bool IsCustomer { get; } = isCustomer;
        public bool IsOperations { get; } = isOperations;
        public bool IsAdmin { get; } = isAdmin;
    }

    private sealed class FakeProofReader : IPrivateProofReader
    {
        public Task<(byte[] Content, string ContentType)?> ReadObjectAsync(string storageRef, CancellationToken cancellationToken) =>
            Task.FromResult<(byte[] Content, string ContentType)?>(([1, 2, 3, 4], "image/jpeg"));
    }

    private sealed class FakeCustomerReadStore : ICustomerDeliveryReadStore
    {
        public int Calls { get; private set; }
        public ParcelReadScope? LastScope { get; private set; }
        public CustomerParcelQuery? LastQuery { get; private set; }
        public ParcelReceiptLookup? ReceiptLookup { get; set; }
        public ParcelProofLookup? ProofLookup { get; set; }

        public Task<CustomerParcelPage> ListParcelsAsync(ParcelReadScope scope, CustomerParcelQuery query, CancellationToken cancellationToken)
        {
            Calls++;
            LastScope = scope;
            LastQuery = query;
            return Task.FromResult(new CustomerParcelPage([], 0, query.Page, query.PageSize));
        }

        public Task<PublicTrackingSummary?> GetPublicTrackingAsync(string trackingNumber, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<PublicTrackingSummary?>(new PublicTrackingSummary(
                trackingNumber, "InTransit", "Colombo", "Kandy", DateTimeOffset.UtcNow,
                [new PublicTimelineEntry("Created", DateTimeOffset.UtcNow.AddHours(-1), "Booked")]));
        }

        public Task<ParcelEventsResponse?> GetParcelEventsAsync(ParcelReadScope scope, Guid parcelId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<ParcelEventsResponse?>(null);
        }

        public Task<ParcelEventSnapshot?> GetParcelSnapshotAsync(ParcelReadScope scope, Guid parcelId, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult<ParcelEventSnapshot?>(null);
        }

        public Task<ParcelReceiptLookup?> GetReceiptAsync(ParcelReadScope scope, Guid parcelId, CancellationToken cancellationToken)
        {
            Calls++;
            LastScope = scope;
            return Task.FromResult(ReceiptLookup);
        }

        public Task<ParcelProofLookup?> GetProofAsync(ParcelReadScope scope, Guid parcelId, CancellationToken cancellationToken)
        {
            Calls++;
            LastScope = scope;
            return Task.FromResult(ProofLookup);
        }
    }
}

