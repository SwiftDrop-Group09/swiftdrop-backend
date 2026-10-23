using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace SwiftDrop.Delivery.Api.Infrastructure.Persistence;

public enum DeliveryDatabaseFailure
{
    Unknown,
    DuplicateTrackingNumber,
    DuplicateExternalOrder,
    DuplicateIdempotencyKey,
    QuoteAlreadyConsumed,
    ConcurrentWrite,
    ConstraintViolation,
    Unavailable
}

// Application adapters can translate these categories to contract error codes
// without exposing PostgreSQL messages, SQL text, or private row data.
public static class DeliveryDatabaseErrors
{
    public static DeliveryDatabaseFailure Classify(Exception exception)
    {
        if (exception is DbUpdateConcurrencyException)
        {
            return DeliveryDatabaseFailure.ConcurrentWrite;
        }

        var cause = exception.GetBaseException();
        if (cause is PostgresException postgres)
        {
            return postgres.SqlState switch
            {
                PostgresErrorCodes.UniqueViolation => postgres.ConstraintName switch
                {
                    "IX_parcels_tracking_number" => DeliveryDatabaseFailure.DuplicateTrackingNumber,
                    "IX_parcels_merchant_id_external_order_id" => DeliveryDatabaseFailure.DuplicateExternalOrder,
                    "IX_idempotency_keys_principal_id_operation_key" => DeliveryDatabaseFailure.DuplicateIdempotencyKey,
                    "IX_quotes_consumed_by_parcel" => DeliveryDatabaseFailure.QuoteAlreadyConsumed,
                    _ => DeliveryDatabaseFailure.ConstraintViolation
                },
                PostgresErrorCodes.SerializationFailure or PostgresErrorCodes.DeadlockDetected
                    or PostgresErrorCodes.LockNotAvailable => DeliveryDatabaseFailure.ConcurrentWrite,
                PostgresErrorCodes.CheckViolation or PostgresErrorCodes.ForeignKeyViolation
                    or PostgresErrorCodes.NotNullViolation => DeliveryDatabaseFailure.ConstraintViolation,
                _ => DeliveryDatabaseFailure.Unknown
            };
        }

        return cause is NpgsqlException or TimeoutException
            ? DeliveryDatabaseFailure.Unavailable : DeliveryDatabaseFailure.Unknown;
    }
}
