using SwiftDrop.Merchant.Api.Domain.Orders;

namespace SwiftDrop.Merchant.Api.Application.Orders.Ports;

/// <summary>
/// Outcome of one Delivery booking call, already translated from the transport
/// (gRPC status codes, timeouts) into what the order processor must do next.
/// Error codes are safe snake_case values that may be shown to merchants.
/// </summary>
public abstract record BookingCallResult
{
    private BookingCallResult() { }

    /// <summary>Delivery has the parcel (created now, or by an earlier call).</summary>
    public sealed record Found(BookingConfirmation Booking) : BookingCallResult;

    /// <summary>Definitely no parcel for this order yet (lookup only).</summary>
    public sealed record NotFound : BookingCallResult;

    /// <summary>Unknown outcome or temporary failure (deadline, unavailable): retry later.</summary>
    public sealed record Transient(string ErrorCode) : BookingCallResult;

    /// <summary>Permanent rejection (invalid order, conflicting payload, pricing changed).</summary>
    public sealed record Rejected(string ErrorCode) : BookingCallResult;

    /// <summary>Merchant's service credential was refused: a configuration problem, not the order's fault.</summary>
    public sealed record Unauthorized(string ErrorCode) : BookingCallResult;
}

/// <summary>Safe error codes produced by Delivery clients and the processor.</summary>
public static class DeliveryErrorCodes
{
    // Transient
    public const string Timeout = "delivery_timeout";
    public const string Unavailable = "delivery_unavailable";
    public const string NotConfigured = "delivery_not_configured";
    public const string UnexpectedError = "delivery_error";

    // Permanent
    public const string InvalidOrder = "invalid_order";
    public const string OrderConflict = "order_conflict";
    public const string PricingChanged = "pricing_changed";
    public const string InvalidStoredOrder = "invalid_stored_order";

    // Configuration
    public const string Unauthorized = "delivery_unauthorized";
}
