using System.Text.Json.Serialization;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Bookings;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfirmInput(string? QuoteId, decimal? AcceptTotal);

public sealed record StoredBookingResponse(int StatusCode, string Body, string Location);

public sealed record BookingResult(
    StoredBookingResponse? Response,
    BookingFailure? Failure,
    IReadOnlyDictionary<string, string[]>? Errors = null);

public enum BookingFailure
{
    Unauthenticated,
    Forbidden,
    InvalidInput,
    NotFound,
    QuoteExpired,
    QuoteConsumed,
    PriceChanged,
    QuoteConflict,
    KeyConflict,
    KeyExpired,
    OperationInProgress,
    PersistenceUnavailable
}

public sealed record BookingReservation(bool IsNew, IdempotencyRecord Record);
public sealed record BookingQuote(Quote Quote, PricingTier Tier);
public sealed record BookingWriteSet(Parcel Parcel, PriceSnapshot Snapshot, BookingHistory History, OutboxMessage Outbox);

public sealed record BookingPriceSnapshotResponse(decimal Total, string Currency, string PricingVersion);
public sealed record ParcelBookingResponse(
    Guid Id,
    string TrackingNumber,
    string Status,
    long Version,
    BookingPriceSnapshotResponse PriceSnapshot,
    DateTimeOffset CreatedAt);
