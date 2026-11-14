using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using SwiftDrop.Merchant.Api.Application.Orders.Ports;
using SwiftDrop.Merchant.Api.Domain.Orders;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Api.Application.Orders;

/// <summary>
/// FR-M04: durably accepts a merchant order and answers immediately (202).
/// No Delivery call happens here; the inbox processor (PR 4) creates the parcel.
/// <list type="bullet">
/// <item>Same business + externalOrderId + same content → the existing job (safe retry).</item>
/// <item>Same business + externalOrderId + different content → 409 order_conflict.</item>
/// <item>Same Idempotency-Key reused for a different order → 409 idempotency_conflict.</item>
/// <item>Concurrent identical submissions → one row; the loser re-reads the winner.</item>
/// </list>
/// </summary>
public sealed class SubmitMerchantOrderHandler(
    MerchantDbContext database,
    ICurrentOrderCaller currentCaller,
    TimeProvider clock)
{
    /// <summary>
    /// Serialisation of the validated order; its canonical form is fingerprinted and stored.
    /// Reading is strict (numbers must be numbers, every field present and non-null), so a
    /// damaged stored payload fails loudly instead of reaching Delivery with missing data.
    /// The strict settings only affect reading; written bytes are unchanged.
    /// </summary>
    internal static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web)
    {
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.Strict,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true
    };

    public async Task<OrderResult<OrderJobResponse>> SubmitAsync(
        MerchantOrderInput? input, string? idempotencyKey, CancellationToken cancellationToken)
    {
        var caller = await currentCaller.ResolveAsync(cancellationToken);
        if (!caller.CanWriteOrders || caller.BusinessId is not { } businessId)
        {
            return OrderResult<OrderJobResponse>.Fail(OrderFailure.Forbidden);
        }

        var keyErrors = OrderInputValidator.ValidateIdempotencyKey(idempotencyKey);
        var validation = OrderInputValidator.ValidateOrder(input);
        if (keyErrors.Count > 0 || !validation.IsValid)
        {
            var errors = validation.Errors.Concat(keyErrors).ToDictionary(pair => pair.Key, pair => pair.Value);
            return OrderResult<OrderJobResponse>.Fail(OrderFailure.InvalidInput, errors);
        }

        var order = validation.Value!;
        var payloadJson = JsonSerializer.Serialize(order, PayloadJson);
        var fingerprint = OrderFingerprint.Compute(payloadJson);

        // 1. Resubmission of a known external order: replay or conflict, never a second job.
        if (await FindByExternalOrderAsync(businessId, order.ExternalOrderId, cancellationToken) is { } existing)
        {
            return ReplayOrConflict(existing, fingerprint);
        }

        // 2. An Idempotency-Key identifies one operation; it cannot be reused for another order.
        if (await IsKeyUsedByAnotherOrderAsync(businessId, idempotencyKey!, order.ExternalOrderId, cancellationToken))
        {
            return OrderResult<OrderJobResponse>.Fail(OrderFailure.IdempotencyConflict);
        }

        // 3. New order.
        var job = InboxJob.Accept(businessId, order.ExternalOrderId, idempotencyKey!, fingerprint, payloadJson, clock.GetUtcNow());
        database.InboxJobs.Add(job);
        try
        {
            await database.SaveChangesAsync(cancellationToken);
            return OrderResult<OrderJobResponse>.Ok(OrderJobMapper.ToResponse(job));
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            // Lost a race with a concurrent identical submission: answer from the winner's row.
            database.Entry(job).State = EntityState.Detached;
            var winner = await FindByExternalOrderAsync(businessId, order.ExternalOrderId, cancellationToken)
                ?? throw new InvalidOperationException("A unique violation occurred but no existing order was found.", exception);
            return ReplayOrConflict(winner, fingerprint);
        }
    }

    private static OrderResult<OrderJobResponse> ReplayOrConflict(InboxJob existing, string fingerprint) =>
        existing.HasSameContentAs(fingerprint)
            ? OrderResult<OrderJobResponse>.Ok(OrderJobMapper.ToResponse(existing))
            : OrderResult<OrderJobResponse>.Fail(OrderFailure.OrderConflict);

    private Task<InboxJob?> FindByExternalOrderAsync(Guid businessId, string externalOrderId, CancellationToken cancellationToken) =>
        database.InboxJobs.AsNoTracking()
            .FirstOrDefaultAsync(job => job.BusinessId == businessId && job.ExternalOrderId == externalOrderId, cancellationToken);

    private Task<bool> IsKeyUsedByAnotherOrderAsync(
        Guid businessId, string idempotencyKey, string externalOrderId, CancellationToken cancellationToken) =>
        database.InboxJobs.AsNoTracking().AnyAsync(job =>
            job.BusinessId == businessId && job.IdempotencyKey == idempotencyKey && job.ExternalOrderId != externalOrderId,
            cancellationToken);

    private static bool IsUniqueViolation(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };
}
