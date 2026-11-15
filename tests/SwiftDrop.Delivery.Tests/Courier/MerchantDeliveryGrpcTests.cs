using Grpc.Core;
using Grpc.Net.Client;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Infrastructure.Bookings;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Tests.Persistence;
using Wire = SwiftDrop.Delivery.Grpc.V1;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class MerchantDeliveryGrpcTests(PostgresFixture fixture)
{
    private const string ServiceKey = "fictional-test-only-service-key-32-characters";
    private static Metadata Headers(string key = ServiceKey) => new() { { MerchantDeliveryGrpcService.CredentialHeader, key } };

    [Fact]
    public async Task RealHttp2ServiceRejectsMissingWrongAndDuplicatedCredentials()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, merchant: true);
        using var factory = Configured(scenario);
        using var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new() { HttpHandler = factory.Server.CreateHandler() });
        var client = new Wire.DeliveryService.DeliveryServiceClient(channel);
        var request = new Wire.GetBookingByExternalOrderRequest { MerchantId = Guid.NewGuid().ToString(), ExternalOrderId = "missing" };
        foreach (var headers in new[] { new Metadata(), Headers("wrong"), new Metadata { { MerchantDeliveryGrpcService.CredentialHeader, ServiceKey }, { MerchantDeliveryGrpcService.CredentialHeader, ServiceKey } } })
        {
            var error = await Assert.ThrowsAsync<RpcException>(async () => await client.GetBookingByExternalOrderAsync(request, headers));
            Assert.Equal(StatusCode.Unauthenticated, error.StatusCode);
        }
    }

    [Fact]
    public async Task BusinessLookupReturnsOnlyCanonicalFailureAndNoPrivateNotes()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture, merchant: true);
        using var failure = await scenario.UpdateAsync("Failed", 2, notes: "PRIVATE COURIER NOTE", reason: "RecipientUnavailable");
        Assert.True(failure.IsSuccessStatusCode);
        var parcel = (await scenario.GraphAsync()).Parcel;
        using var factory = Configured(scenario);
        using var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new() { HttpHandler = factory.Server.CreateHandler() });
        var client = new Wire.DeliveryService.DeliveryServiceClient(channel);
        var request = new Wire.GetBookingByExternalOrderRequest { MerchantId = parcel.MerchantId!.Value.ToString(), ExternalOrderId = parcel.ExternalOrderId };
        var result = await client.GetBookingByExternalOrderAsync(request, Headers());
        Assert.Equal(parcel.Id.ToString(), result.ParcelId);
        Assert.Equal("Failed", result.Status);
        Assert.Equal("RecipientUnavailable", result.FailureReason);
        Assert.True(result.HasTerminalAt);
        Assert.DoesNotContain("PRIVATE", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("deliveryNote", result.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("phoneNumber", result.ToString(), StringComparison.OrdinalIgnoreCase);
        request.MerchantId = Guid.NewGuid().ToString();
        var error = await Assert.ThrowsAsync<RpcException>(async () => await client.GetBookingByExternalOrderAsync(request, Headers()));
        Assert.Equal(StatusCode.NotFound, error.StatusCode);
    }

    [Fact]
    public async Task CreateCommitsOneCompleteBookingAcrossConcurrentReplaysAndRejectsChangedPayload()
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var parcel = (await scenario.GraphAsync()).Parcel;
        using var factory = Configured(scenario);
        using var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new() { HttpHandler = factory.Server.CreateHandler() });
        var client = new Wire.DeliveryService.DeliveryServiceClient(channel);
        var business = Guid.NewGuid();
        var request = new Wire.CreateBookingRequest { MerchantId = business.ToString(), ExternalOrderId = "STORE-" + Guid.NewGuid().ToString("N"),
            IdempotencyKey = Guid.NewGuid().ToString(), RequestFingerprint = new string('a', 64), Size = "Small", WeightKg = "2.500",
            AcceptedPriceVersion = "status-test-v1", Recipient = new() { Name = "Nimal Perera", PhoneNumber = "+94771234567" },
            PickupAddress = new() { Street = "12 Lake Road", City = "Colombo", PostalCode = "00300", ZoneId = parcel.ZoneId.ToString() },
            DeliveryAddress = new() { Street = "48 Station Road", City = "Colombo", PostalCode = "00400", ZoneId = parcel.ZoneId.ToString() } };
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(async _ => await client.CreateBookingAsync(request, Headers())));
        Assert.Single(results.Select(result => result.ParcelId).Distinct());
        Assert.All(results, result => { Assert.Equal("Created", result.Status); Assert.Equal(1, result.Version); Assert.Equal("540.00", result.Total); });
        await using var database = scenario.Context();
        var id = Guid.Parse(results[0].ParcelId);
        Assert.Equal(1, await database.Parcels.CountAsync(p => p.MerchantId == business && p.ExternalOrderId == request.ExternalOrderId));
        Assert.Equal(1, await database.PriceSnapshots.CountAsync(p => p.ParcelId == id));
        Assert.Equal(1, await database.BookingHistory.CountAsync(p => p.ParcelId == id));
        Assert.Equal(1, await database.MerchantBookingIdentities.CountAsync(p => p.ParcelId == id));
        Assert.Equal(1, await database.OutboxMessages.FromSqlInterpolated($"SELECT * FROM outbox_messages WHERE payload->>'parcelId' = {id.ToString()}").CountAsync());
        request.WeightKg = "3";
        var conflict = await Assert.ThrowsAsync<RpcException>(async () => await client.CreateBookingAsync(request, Headers()));
        Assert.Equal(StatusCode.AlreadyExists, conflict.StatusCode);
        Assert.Equal(1, await database.Parcels.CountAsync(p => p.MerchantId == business));
    }

    [Theory]
    [InlineData("obsolete", "2.50", StatusCode.FailedPrecondition)]
    [InlineData("status-test-v1", "2.0001", StatusCode.InvalidArgument)]
    [InlineData("status-test-v1", "NaN", StatusCode.InvalidArgument)]
    public async Task InvalidPricingOrWeightNeverCreatesAParcel(string version, string weight, StatusCode expected)
    {
        await using var scenario = await StatusTestScenario.CreateAsync(fixture);
        var parcel = (await scenario.GraphAsync()).Parcel;
        using var factory = Configured(scenario);
        using var channel = GrpcChannel.ForAddress(factory.Server.BaseAddress, new() { HttpHandler = factory.Server.CreateHandler() });
        var client = new Wire.DeliveryService.DeliveryServiceClient(channel);
        var business = Guid.NewGuid();
        var request = new Wire.CreateBookingRequest { MerchantId = business.ToString(), ExternalOrderId = "ORD-1", IdempotencyKey = "key-1",
            RequestFingerprint = new string('a', 64), Size = "Small", WeightKg = weight, AcceptedPriceVersion = version,
            Recipient = new() { Name = "Recipient", PhoneNumber = "+94771234567" },
            PickupAddress = new() { Street = "Street", City = "Colombo", PostalCode = "00300", ZoneId = parcel.ZoneId.ToString() },
            DeliveryAddress = new() { Street = "Street", City = "Colombo", PostalCode = "00300", ZoneId = parcel.ZoneId.ToString() } };
        var error = await Assert.ThrowsAsync<RpcException>(async () => await client.CreateBookingAsync(request, Headers()));
        Assert.Equal(expected, error.StatusCode);
        await using var database = scenario.Context();
        Assert.False(await database.Parcels.AnyAsync(p => p.MerchantId == business));
        Assert.False(await database.MerchantBookingIdentities.AnyAsync(p => p.MerchantId == business));
    }

    private static Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactory<SwiftDrop.Delivery.Api.Program> Configured(StatusTestScenario scenario) =>
        scenario.Factory.WithWebHostBuilder(builder => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?> { ["MerchantIntegration:ServiceKey"] = ServiceKey, ["MerchantIntegration:AllowInsecureTransport"] = "true" })));
}
