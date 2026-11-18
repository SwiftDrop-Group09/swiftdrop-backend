namespace SwiftDrop.Merchant.Api.Domain.Orders;

/// <summary>
/// Lifecycle of a merchant order inside the Merchant inbox.
/// Accepted → (Retrying)* → Created | Failed.
/// </summary>
public enum InboxJobState
{
    /// <summary>Stored durably and answered with 202; no Delivery call has finished yet.</summary>
    Accepted,

    /// <summary>A Delivery call failed transiently; the job is waiting for its next attempt.</summary>
    Retrying,

    /// <summary>Delivery confirmed the parcel; tracking number and parcel status are known.</summary>
    Created,

    /// <summary>Delivery permanently rejected the order; <c>ErrorCode</c> explains why.</summary>
    Failed
}
