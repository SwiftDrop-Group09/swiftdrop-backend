using SwiftDrop.Merchant.Api.Domain.Orders;

namespace SwiftDrop.Merchant.Api.Application.Orders;

/// <summary>Outcome of an order use case: either a value, or a failure with optional field errors.</summary>
public sealed record OrderResult<T>(T? Value, OrderFailure? Failure, IReadOnlyDictionary<string, string[]>? Errors = null)
    where T : class
{
    public static OrderResult<T> Ok(T value) => new(value, null);

    public static OrderResult<T> Fail(OrderFailure failure, IReadOnlyDictionary<string, string[]>? errors = null) =>
        new(null, failure, errors);
}

internal static class OrderJobMapper
{
    public static OrderJobResponse ToResponse(InboxJob job) => new(
        job.Id,
        job.ExternalOrderId,
        job.State.ToString(),
        job.Attempts,
        job.NextRetryAt,
        job.ParcelId,
        job.TrackingNumber,
        job.ParcelStatus,
        job.ParcelVersion,
        job.LastStatusChangedAt,
        job.TerminalAt,
        job.ErrorCode,
        job.LastRefreshedAt,
        job.CreatedAt);
}
