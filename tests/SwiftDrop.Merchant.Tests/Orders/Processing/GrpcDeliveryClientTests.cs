using Grpc.Core;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Orders.Delivery;
using Wire = SwiftDrop.Delivery.Grpc.V1;

namespace SwiftDrop.Merchant.Tests.Orders.Processing;

public sealed class GrpcDeliveryClientTests
{
    [Fact]
    public async Task CreateUsesTheBusinessContextExactWeightAndServiceCredential()
    {
        var command = CreateBookingCommand.FromJob(ProcessingTestData.Job());
        var invoker = new Invoker((request, options) =>
        {
            var create = Assert.IsType<Wire.CreateBookingRequest>(request);
            Assert.Equal(command.MerchantId.ToString("D"), create.MerchantId);
            Assert.Equal(command.IdempotencyKey, create.IdempotencyKey);
            Assert.Equal(command.RequestFingerprint, create.RequestFingerprint);
            Assert.Equal("2.50", create.WeightKg);
            Assert.Equal("fictional-service-key", options.Headers!.Single().Value);
            return Result();
        });
        using var client = new GrpcDeliveryBookingClient(invoker, "fictional-service-key");
        var response = Assert.IsType<BookingCallResult.Found>(await client.CreateBookingAsync(command, CancellationToken.None));
        Assert.Equal(9223372036854775806, response.Booking.ParcelStatus.Version);
        Assert.Equal("RecipientUnavailable", response.Booking.ParcelStatus.FailureReason);
    }

    [Theory]
    [InlineData(StatusCode.NotFound, typeof(BookingCallResult.NotFound))]
    [InlineData(StatusCode.Unauthenticated, typeof(BookingCallResult.Unauthorized))]
    [InlineData(StatusCode.PermissionDenied, typeof(BookingCallResult.Unauthorized))]
    [InlineData(StatusCode.Unavailable, typeof(BookingCallResult.Transient))]
    [InlineData(StatusCode.DeadlineExceeded, typeof(BookingCallResult.Transient))]
    [InlineData(StatusCode.InvalidArgument, typeof(BookingCallResult.Rejected))]
    [InlineData(StatusCode.AlreadyExists, typeof(BookingCallResult.Rejected))]
    [InlineData(StatusCode.FailedPrecondition, typeof(BookingCallResult.Rejected))]
    public async Task RpcFailuresAreTranslatedWithoutLeakingInternalMessages(StatusCode code, Type expected)
    {
        using var client = new GrpcDeliveryBookingClient(new Invoker((_, _) => throw new RpcException(new(code, "private service details"))), "key");
        var result = await client.GetByExternalOrderAsync(Guid.NewGuid(), "ORD-1", CancellationToken.None);
        Assert.IsType(expected, result);
        Assert.DoesNotContain("private", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task UnrecognizedFailureReasonIsUnverifiedRatherThanCopiedIntoMerchantRecords()
    {
        using var client = new GrpcDeliveryBookingClient(new Invoker((_, _) => { var result = Result(); result.FailureReason = "private free text"; return result; }), "key");
        Assert.IsType<BookingCallResult.Transient>(await client.GetByExternalOrderAsync(Guid.NewGuid(), "ORD-1", CancellationToken.None));
    }

    [Fact]
    public void SameVersionBackfillAddsOnlyMissingCanonicalReasonAndNeverRegressesTheStatus()
    {
        var job = InboxJobTestData.CreatedJob();
        var at = InboxJobTestData.Now.AddMinutes(5);
        Assert.True(job.ApplyStatus(new("Failed", 4, at, at), at));
        Assert.Null(job.FailureReason);
        Assert.True(job.ApplyStatus(new("Failed", 4, at, at, "AddressUnreachable"), at.AddMinutes(1)));
        Assert.Equal("AddressUnreachable", job.FailureReason);
        Assert.False(job.ApplyStatus(new("Failed", 3, at, at, "Other"), at.AddMinutes(2)));
        Assert.False(job.ApplyStatus(new("OutForDelivery", 4, at), at.AddMinutes(3)));
        Assert.Equal("Failed", job.ParcelStatus);
        Assert.Equal("AddressUnreachable", job.FailureReason);
    }

    private static Wire.BookingResult Result() => new()
    {
        ParcelId = Guid.NewGuid().ToString("D"), TrackingNumber = "SD-2026-1001", Status = "Failed",
        Version = 9223372036854775806, Total = "540.00", Currency = "LKR", PricingVersion = "2026-11-v1",
        LastStatusChangedAt = InboxJobTestData.Now.ToString("O"), TerminalAt = InboxJobTestData.Now.ToString("O"),
        CreatedAt = InboxJobTestData.Now.AddDays(-1).ToString("O"), FailureReason = "RecipientUnavailable"
    };

    private sealed class Invoker(Func<object, CallOptions, Wire.BookingResult> send) : CallInvoker
    {
        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request)
        {
            Task<TResponse> response;
            try { response = Task.FromResult((TResponse)(object)send(request!, options)); }
            catch (Exception error) { response = Task.FromException<TResponse>(error); }
            return new(response, Task.FromResult(new Metadata()), () => Status.DefaultSuccess, () => new Metadata(), () => { });
        }
        public override TResponse BlockingUnaryCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options, TRequest request) => throw new NotSupportedException();
        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(Method<TRequest, TResponse> method, string? host, CallOptions options) => throw new NotSupportedException();
    }
}
