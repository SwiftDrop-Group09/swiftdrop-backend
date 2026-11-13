using SwiftDrop.Merchant.Api.Domain.Orders;

namespace SwiftDrop.Merchant.Tests.Orders;

/// <summary>Fictional, valid building blocks for inbox job tests.</summary>
internal static class InboxJobTestData
{
    public static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    public const string Payload =
        """{"externalOrderId":"ORD-1001","packageDetails":{"weightKg":"2.50","size":"Small"}}""";

    public static string Fingerprint => OrderFingerprint.Compute(Payload);

    public static InboxJob NewJob(Guid? businessId = null, string externalOrderId = "ORD-1001") =>
        InboxJob.Accept(
            businessId ?? Guid.NewGuid(),
            externalOrderId,
            idempotencyKey: "idem-fictional-1001",
            requestFingerprint: Fingerprint,
            orderPayload: Payload,
            now: Now);

    public static ParcelStatusSnapshot Status(string status, long version, DateTimeOffset? terminalAt = null) =>
        new(status, version, Now.AddMinutes(version), terminalAt);

    public static BookingConfirmation Booking(long version = 1) =>
        new(
            parcelId: Guid.NewGuid(),
            trackingNumber: "SD-FICTIONAL-0001",
            parcelStatus: Status(ParcelStatuses.Created, version),
            total: 750.00m,
            currency: "LKR",
            pricingVersion: "2026-11-v1");

    public static InboxJob CreatedJob()
    {
        var job = NewJob();
        job.MarkCreated(Booking(), Now.AddSeconds(5));
        return job;
    }
}
