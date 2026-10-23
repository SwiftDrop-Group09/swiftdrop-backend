using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Bookings;

public interface IBookingStore
{
    Task<IBookingTransaction> BeginAsync(CancellationToken cancellationToken);
}

public interface IBookingTransaction : IAsyncDisposable
{
    Task<BookingReservation> ReserveAsync(
        Guid ownerId, string key, string fingerprint, DateTimeOffset now, CancellationToken cancellationToken);

    Task<BookingQuote?> LoadOwnedQuoteForUpdateAsync(
        Guid ownerId, Guid quoteId, CancellationToken cancellationToken);

    Task SaveAsync(Quote quote, BookingWriteSet write, IdempotencyRecord reservation, CancellationToken cancellationToken);
    Task CommitAsync(CancellationToken cancellationToken);
}

public enum BookingStorageFailure
{
    Unavailable,
    InProgress,
    TrackingCollision,
    ConcurrentWrite
}

public sealed class BookingStoreException : Exception
{
    public BookingStoreException(BookingStorageFailure failure, Exception? innerException = null)
        : base("Delivery booking storage could not complete the operation.", innerException) => Failure = failure;

    public BookingStorageFailure Failure { get; }
}
