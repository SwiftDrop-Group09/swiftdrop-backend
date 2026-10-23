using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Bookings;

public sealed class CustomerBookingHandler(
    ICurrentCustomer currentCustomer,
    IBookingStore store,
    ITrackingNumberGenerator trackingNumbers,
    TimeProvider timeProvider)
{
    private const int TrackingAttempts = 3;

    public async Task<BookingResult> ExecuteAsync(ConfirmInput? input, string? idempotencyKey,
        string correlationId, CancellationToken cancellationToken = default)
    {
        if (!currentCustomer.IsAuthenticated || currentCustomer.CustomerId is not { } ownerId || ownerId == Guid.Empty)
        {
            return new(null, BookingFailure.Unauthenticated);
        }

        if (!currentCustomer.IsCustomer)
        {
            return new(null, BookingFailure.Forbidden);
        }

        var validation = BookingInputValidator.Validate(input, idempotencyKey);
        if (validation.Input is not { } confirmation)
        {
            return new(null, BookingFailure.InvalidInput, validation.Errors);
        }

        for (var attempt = 0; attempt < TrackingAttempts; attempt++)
        {
            try
            {
                await using var transaction = await store.BeginAsync(cancellationToken);
                var reservation = await transaction.ReserveAsync(ownerId, confirmation.IdempotencyKey,
                    confirmation.Fingerprint, UtcNow(), cancellationToken);
                var record = reservation.Record;
                if (record.PrincipalId != ownerId.ToString("D") || record.Operation != "CreateParcel"
                    || record.Key != confirmation.IdempotencyKey)
                {
                    return new(null, BookingFailure.PersistenceUnavailable);
                }

                if (!reservation.IsNew && UtcNow() >= record.ExpiresAt)
                {
                    return new(null, BookingFailure.KeyExpired);
                }

                if (record.RequestFingerprint != confirmation.Fingerprint)
                {
                    return new(null, BookingFailure.KeyConflict);
                }

                if (!reservation.IsNew)
                {
                    if (record.StoredResponse is null && record.ParcelId is null)
                    {
                        return new(null, BookingFailure.OperationInProgress);
                    }

                    return BookingResponseCodec.TryRead(record, confirmation.AcceptTotal, out var stored)
                        ? new(stored, null)
                        : new(null, BookingFailure.PersistenceUnavailable);
                }

                if (record.StoredResponse is not null || record.ParcelId is not null)
                {
                    return new(null, BookingFailure.PersistenceUnavailable);
                }

                var bookingQuote = await transaction.LoadOwnedQuoteForUpdateAsync(ownerId, confirmation.QuoteId, cancellationToken);
                if (bookingQuote is null || bookingQuote.Quote.OwnerId != ownerId)
                {
                    return new(null, BookingFailure.NotFound);
                }

                var quote = bookingQuote.Quote;
                var now = UtcNow();
                if (now >= quote.ExpiresAt)
                {
                    return new(null, BookingFailure.QuoteExpired);
                }

                if (quote.ConsumedByParcelId is not null)
                {
                    return new(null, BookingFailure.QuoteConsumed);
                }

                if (confirmation.AcceptTotal != quote.Total)
                {
                    return new(null, BookingFailure.PriceChanged);
                }

                if (quote.Id != confirmation.QuoteId
                    || !BookingQuoteMaterializer.TryMaterialize(bookingQuote, out var details, out var price))
                {
                    return new(null, BookingFailure.QuoteConflict);
                }

                var parcel = Parcel.ForCustomer(Guid.NewGuid(), ownerId, trackingNumbers.Generate(), details!, now);
                var write = BookingAssembler.Create(parcel, bookingQuote.Tier, price, ownerId, correlationId);
                var response = BookingResponseCodec.Create(write);
                quote.Consume(parcel, now);
                record.Complete(parcel.Id, response.StatusCode, response.Body, response.Location);
                await transaction.SaveAsync(quote, write, record, cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return new(response, null);
            }
            catch (BookingStoreException exception)
            {
                if (exception.Failure == BookingStorageFailure.TrackingCollision && attempt < TrackingAttempts - 1)
                {
                    // A new transaction reserves the same key after the failed attempt rolled back.
                    continue;
                }

                return new(null, exception.Failure is BookingStorageFailure.InProgress or BookingStorageFailure.ConcurrentWrite
                    ? BookingFailure.OperationInProgress : BookingFailure.PersistenceUnavailable);
            }
        }

        return new(null, BookingFailure.PersistenceUnavailable);
    }

    private DateTimeOffset UtcNow()
    {
        var now = timeProvider.GetUtcNow().ToUniversalTime();
        return new(now.Ticks - (now.Ticks % 10), TimeSpan.Zero);
    }
}
