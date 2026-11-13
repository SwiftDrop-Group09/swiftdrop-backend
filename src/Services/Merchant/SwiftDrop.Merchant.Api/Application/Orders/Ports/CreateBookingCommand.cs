using System.Globalization;
using System.Text.Json;
using SwiftDrop.Merchant.Api.Domain.Orders;

namespace SwiftDrop.Merchant.Api.Application.Orders.Ports;

/// <summary>
/// Everything Delivery's <c>CreateBooking</c> needs, mirroring the ten fields of
/// <c>CreateBookingRequest</c> in delivery.proto (plus a correlation id).
/// Built only from the stored job, so every retry sends identical data with
/// the same idempotency key and fingerprint.
/// </summary>
public sealed record CreateBookingCommand(
    Guid MerchantId,
    string ExternalOrderId,
    string IdempotencyKey,
    string RequestFingerprint,
    OrderAddress PickupAddress,
    OrderAddress DeliveryAddress,
    OrderRecipient Recipient,
    string WeightKg,
    string Size,
    string AcceptedPriceVersion,
    string CorrelationId)
{
    /// <summary>Rebuilds the command from the job's stored payload.</summary>
    /// <exception cref="JsonException">The stored payload is unreadable.</exception>
    public static CreateBookingCommand FromJob(InboxJob job)
    {
        ArgumentNullException.ThrowIfNull(job);
        var order = JsonSerializer.Deserialize<MerchantOrderPayload>(job.OrderPayload, SubmitMerchantOrderHandler.PayloadJson)
            ?? throw new JsonException("The stored order payload is empty.");

        return new CreateBookingCommand(
            job.BusinessId,
            job.ExternalOrderId,
            job.IdempotencyKey,
            job.RequestFingerprint,
            order.PickupAddress,
            order.DeliveryAddress,
            order.Recipient,
            // Exact decimal string: the decimal keeps its scale, so "2.50" stays "2.50" (never rounded).
            order.PackageDetails.WeightKg.ToString(CultureInfo.InvariantCulture),
            order.PackageDetails.Size,
            order.AcceptedPriceVersion,
            job.Id.ToString("N"));
    }
}
