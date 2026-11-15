using System.Globalization;
using Grpc.Core;
using Grpc.Net.Client;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Domain.Orders;
using Wire = SwiftDrop.Delivery.Grpc.V1;

namespace SwiftDrop.Merchant.Api.Infrastructure.Orders.Delivery;

/// <summary>The only cross-service access is the versioned Delivery RPC; no Delivery database connection is used.</summary>
public sealed class GrpcDeliveryBookingClient : IDeliveryBookingClient, IDisposable
{
    private readonly GrpcChannel channel;
    private readonly Wire.DeliveryService.DeliveryServiceClient client;
    private readonly string serviceKey;
    public GrpcDeliveryBookingClient(IConfiguration configuration, IHostEnvironment environment)
    {
        var section = configuration.GetSection("Merchant:Orders:Delivery");
        serviceKey = section["ServiceKey"] ?? "";
        if (serviceKey is not { Length: >= 32 and <= 256 } || serviceKey.Any(c => c < '!' || c > '~')
            || !Uri.TryCreate(section["Address"], UriKind.Absolute, out var address)
            || address.UserInfo.Length != 0 || address.AbsolutePath != "/" || address.Query.Length != 0 || address.Fragment.Length != 0
            || (address.Scheme != "https" && !(address.Scheme == "http"
                && (environment.IsDevelopment() || environment.IsEnvironment("Testing")) && section.GetValue<bool>("AllowInsecureTransport"))))
            throw new InvalidOperationException("Merchant Delivery RPC requires an HTTPS address and a service key (32–256 printable characters). Local HTTP/2 requires an explicit development option.");
        channel = GrpcChannel.ForAddress(address);
        client = new(channel);
    }

    // An injectable call invoker also permits transport-level HTTP/2 tests without a live database.
    public GrpcDeliveryBookingClient(CallInvoker invoker, string key)
    {
        channel = null!;
        client = new(invoker);
        serviceKey = key;
    }

    public Task<BookingCallResult> GetByExternalOrderAsync(Guid merchantId, string externalOrderId, CancellationToken cancellationToken) =>
        Call(() => client.GetBookingByExternalOrderAsync(new Wire.GetBookingByExternalOrderRequest
        { MerchantId = merchantId.ToString("D"), ExternalOrderId = externalOrderId }, Headers(), cancellationToken: cancellationToken), cancellationToken);

    public Task<BookingCallResult> CreateBookingAsync(CreateBookingCommand command, CancellationToken cancellationToken)
    {
        Wire.Address Address(Application.Orders.OrderAddress address) => new()
        { Street = address.Street, City = address.City, PostalCode = address.PostalCode, ZoneId = address.ZoneId.ToString("D") };
        var request = new Wire.CreateBookingRequest { MerchantId = command.MerchantId.ToString("D"), ExternalOrderId = command.ExternalOrderId,
            IdempotencyKey = command.IdempotencyKey, RequestFingerprint = command.RequestFingerprint,
            PickupAddress = Address(command.PickupAddress), DeliveryAddress = Address(command.DeliveryAddress),
            Recipient = new() { Name = command.Recipient.Name, PhoneNumber = command.Recipient.PhoneNumber },
            WeightKg = command.WeightKg, Size = command.Size, AcceptedPriceVersion = command.AcceptedPriceVersion };
        return Call(() => client.CreateBookingAsync(request, Headers(), cancellationToken: cancellationToken), cancellationToken);
    }

    private Metadata Headers() => new() { { "x-swiftdrop-merchant-service-key", serviceKey } };
    private static async Task<BookingCallResult> Call(Func<AsyncUnaryCall<Wire.BookingResult>> send, CancellationToken cancellationToken)
    {
        try
        {
            using var call = send();
            var result = await call.ResponseAsync;
            if (!Guid.TryParse(result.ParcelId, out var id) || id == Guid.Empty
                || !decimal.TryParse(result.Total, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var total)
                || total < 0 || result.Version < 1 || result.Currency != "LKR") return new BookingCallResult.Transient(DeliveryErrorCodes.UnexpectedError);
            var changed = DateTimeOffset.ParseExact(result.LastStatusChangedAt, "O", CultureInfo.InvariantCulture);
            var terminal = result.HasTerminalAt ? DateTimeOffset.ParseExact(result.TerminalAt, "O", CultureInfo.InvariantCulture) : (DateTimeOffset?)null;
            return new BookingCallResult.Found(new(id, result.TrackingNumber,
                new(result.Status, result.Version, changed, terminal, result.HasFailureReason ? result.FailureReason : null),
                total, result.Currency, result.PricingVersion));
        }
        catch (RpcException error) when (!cancellationToken.IsCancellationRequested)
        {
            return error.StatusCode switch
            {
                StatusCode.NotFound => new BookingCallResult.NotFound(),
                StatusCode.Unauthenticated or StatusCode.PermissionDenied => new BookingCallResult.Unauthorized(DeliveryErrorCodes.Unauthorized),
                StatusCode.InvalidArgument => new BookingCallResult.Rejected(DeliveryErrorCodes.InvalidOrder),
                StatusCode.AlreadyExists => new BookingCallResult.Rejected(DeliveryErrorCodes.OrderConflict),
                StatusCode.FailedPrecondition => new BookingCallResult.Rejected(DeliveryErrorCodes.PricingChanged),
                StatusCode.DeadlineExceeded => new BookingCallResult.Transient(DeliveryErrorCodes.Timeout),
                _ => new BookingCallResult.Transient(DeliveryErrorCodes.Unavailable)
            };
        }
        catch (Exception error) when (error is FormatException or ArgumentException or OverflowException)
        { return new BookingCallResult.Transient(DeliveryErrorCodes.UnexpectedError); }
    }
    public void Dispose() => channel?.Dispose();
}
