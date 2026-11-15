using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Google.Protobuf;
using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftDrop.Delivery.Api.Application.Bookings;
using SwiftDrop.Delivery.Api.Application.Quotes;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using Wire = SwiftDrop.Delivery.Grpc.V1;

namespace SwiftDrop.Delivery.Api.Infrastructure.Bookings;

/// <summary>Private Merchant workload boundary. Human JWTs and public ownership headers never authorize this service.</summary>
public sealed class MerchantDeliveryGrpcService(IConfiguration configuration, IHostEnvironment environment,
    Func<DeliveryDbContext> databaseFactory, ITrackingNumberGenerator tracking, TimeProvider clock)
    : Wire.DeliveryService.DeliveryServiceBase
{
    public const string CredentialHeader = "x-swiftdrop-merchant-service-key";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private void Authorize(ServerCallContext context)
    {
        var secret = configuration["MerchantIntegration:ServiceKey"];
        var supplied = context.RequestHeaders.Where(entry => entry.Key == CredentialHeader).ToArray();
        if (secret is not { Length: >= 32 and <= 256 } || supplied.Length != 1 || supplied[0].Value.Length > 256
            || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(secret)),
                SHA256.HashData(Encoding.UTF8.GetBytes(supplied[0].Value))))
            throw Error(StatusCode.Unauthenticated);
        // Cleartext HTTP/2 is permitted only on the explicitly configured local development listener.
        if (!context.GetHttpContext().Request.IsHttps
            && !((environment.IsDevelopment() || environment.IsEnvironment("Testing"))
                && configuration.GetValue<bool>("MerchantIntegration:AllowInsecureTransport")))
            throw Error(StatusCode.PermissionDenied);
    }

    public override async Task<Wire.BookingResult> GetBookingByExternalOrder(Wire.GetBookingByExternalOrderRequest request, ServerCallContext context)
    {
        Authorize(context);
        var merchant = Business(request.MerchantId, request.ExternalOrderId);
        try
        {
            var database = databaseFactory();
            return await Lookup(database, merchant, request.ExternalOrderId, context.CancellationToken)
                ?? throw Error(StatusCode.NotFound);
        }
        catch (Exception error) when (StorageFailure(error)) { throw Error(StatusCode.Unavailable); }
    }

    public override async Task<Wire.BookingResult> CreateBooking(Wire.CreateBookingRequest request, ServerCallContext context)
    {
        Authorize(context);
        var merchant = Business(request.MerchantId, request.ExternalOrderId);
        if (request.IdempotencyKey is not { Length: > 0 and <= 128 } || request.IdempotencyKey.Any(char.IsControl)
            || request.RequestFingerprint.Length != 64 || !request.RequestFingerprint.All(Uri.IsHexDigit)
            || request.AcceptedPriceVersion is not { Length: > 0 and <= 128 }
            || request.WeightKg.Length > 32 || !System.Text.RegularExpressions.Regex.IsMatch(request.WeightKg, @"^\d{1,2}(\.\d{1,3})?$")
            || !decimal.TryParse(request.WeightKg, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var weight))
            throw Error(StatusCode.InvalidArgument);
        AddressInput? Address(Wire.Address? value) => value is null ? null : new(value.Street, value.City, value.PostalCode, value.ZoneId);
        var validation = QuoteInputValidator.Validate(new(Address(request.PickupAddress), Address(request.DeliveryAddress),
            new(weight, request.Size), request.Recipient is { } recipient ? new(recipient.Name, recipient.PhoneNumber) : null));
        if (validation.Input is not { } input || request.CalculateSize() > 16 * 1024) throw Error(StatusCode.InvalidArgument);
        var fingerprint = Convert.ToHexString(SHA256.HashData(request.ToByteArray()));
        var token = context.CancellationToken;
        try
        {
            var database = databaseFactory();
            await using var transaction = await database.Database.BeginTransactionAsync(token);
            await database.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout = '2s'", token);
            // Serialize one business's create/replay decisions without trusting an incoming fingerprint alone.
            await database.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({merchant.ToString("D")}, 0))", token);
            var identities = await database.MerchantBookingIdentities.AsNoTracking().Where(row => row.MerchantId == merchant
                && (row.ExternalOrderId == request.ExternalOrderId || row.IdempotencyKey == request.IdempotencyKey)).ToArrayAsync(token);
            if (identities.Length > 1) throw Error(StatusCode.AlreadyExists);
            var identity = identities.SingleOrDefault();
            if (identity is not null)
            {
                if (identity.Fingerprint != fingerprint || identity.ExternalOrderId != request.ExternalOrderId)
                    throw Error(StatusCode.AlreadyExists);
                return await Lookup(database, merchant, request.ExternalOrderId, token) ?? throw Error(StatusCode.Unavailable);
            }
            // Older bookings without a retained payload binding are never silently recreated or adopted.
            if (await database.Parcels.AnyAsync(parcel => parcel.MerchantId == merchant && parcel.ExternalOrderId == request.ExternalOrderId, token))
                throw Error(StatusCode.AlreadyExists);
            var zones = await database.Zones.AsNoTracking().Where(zone => zone.IsActive
                && (zone.Id == input.PickupAddress.ZoneId || zone.Id == input.DeliveryAddress.ZoneId)).Select(zone => zone.Id).ToArrayAsync(token);
            if (!zones.Contains(input.PickupAddress.ZoneId) || !zones.Contains(input.DeliveryAddress.ZoneId)) throw Error(StatusCode.FailedPrecondition);
            var tier = await database.PricingTiers.AsNoTracking().SingleOrDefaultAsync(t => t.IsActive
                && t.ZoneId == input.DeliveryAddress.ZoneId && t.Size == input.PackageDetails.Size, token);
            if (tier is null || tier.PricingVersion != request.AcceptedPriceVersion
                || !PriceCalculator.TryCalculate(tier.BaseRate, tier.PerKgRate, weight, out var price)) throw Error(StatusCode.FailedPrecondition);
            var details = new ParcelDetails(input.PackageDetails.Size, weight, input.DeliveryAddress.ZoneId,
                JsonSerializer.Serialize(input.PickupAddress, Json), null, JsonSerializer.Serialize(input.DeliveryAddress, Json), JsonSerializer.Serialize(input.Recipient, Json));
            var write = BookingAssembler.CreateMerchant(merchant, request.ExternalOrderId, request.AcceptedPriceVersion,
                details, tier, price!, merchant, tracking, clock.GetUtcNow(), Guid.NewGuid().ToString("N"));
            database.MerchantBookingIdentities.Add(new(merchant, request.ExternalOrderId, request.IdempotencyKey, fingerprint, write.Parcel.Id));
            await BookingWritePersistence.SaveAsync(database, write, token);
            var result = await Lookup(database, merchant, request.ExternalOrderId, token) ?? throw Error(StatusCode.Unavailable);
            await transaction.CommitAsync(token);
            return result;
        }
        catch (Exception error) when (StorageFailure(error)) { throw Error(StatusCode.Unavailable); }
    }

    private static Guid Business(string value, string externalOrder)
    {
        if (!Guid.TryParse(value, out var merchant) || merchant == Guid.Empty || string.IsNullOrWhiteSpace(externalOrder)
            || externalOrder.Length > 128 || externalOrder.Any(char.IsControl)) throw Error(StatusCode.InvalidArgument);
        return merchant;
    }

    private static async Task<Wire.BookingResult?> Lookup(DeliveryDbContext database, Guid merchant, string externalOrder, CancellationToken token)
    {
        // One statement binds current status, immutable price and safe failure reason to the owning business.
        var row = await (from parcel in database.Parcels.AsNoTracking()
                         join price in database.PriceSnapshots.AsNoTracking() on parcel.Id equals price.ParcelId
                         where parcel.MerchantId == merchant && parcel.ExternalOrderId == externalOrder
                         select new { parcel.Id, parcel.TrackingNumber, parcel.Status, parcel.Version, parcel.CreatedAt,
                             parcel.UpdatedAt, price.Total, price.Currency, price.PricingVersion,
                             Reason = database.BookingHistory.Where(item => item.ParcelId == parcel.Id && item.ToStatus == ParcelStatus.Failed)
                                 .OrderByDescending(item => item.Timestamp).Select(item => item.ChangeReason).FirstOrDefault() }).SingleOrDefaultAsync(token);
        if (row is null) return null;
        var result = new Wire.BookingResult { ParcelId = row.Id.ToString("D"), TrackingNumber = row.TrackingNumber,
            Status = row.Status.ToString(), Version = row.Version, Total = row.Total.ToString(CultureInfo.InvariantCulture),
            Currency = row.Currency, PricingVersion = row.PricingVersion, CreatedAt = row.CreatedAt.ToString("O"), LastStatusChangedAt = row.UpdatedAt.ToString("O") };
        if (row.Status is ParcelStatus.Delivered or ParcelStatus.Failed or ParcelStatus.Cancelled) result.TerminalAt = row.UpdatedAt.ToString("O");
        if (row.Status == ParcelStatus.Failed && row.Reason is "RecipientUnavailable" or "AddressUnreachable" or "Damaged" or "Other") result.FailureReason = row.Reason;
        return result;
    }

    private static RpcException Error(StatusCode code) => new(new Status(code, "Delivery request could not be completed."));
    private static bool StorageFailure(Exception error) => error is NpgsqlException or DbUpdateException or TimeoutException;
}
