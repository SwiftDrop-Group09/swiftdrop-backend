namespace SwiftDrop.Merchant.Api.Application.Orders;

/// <summary>Why an order use case did not succeed; mapped to ProblemDetails by OrderProblems.</summary>
public enum OrderFailure
{
    /// <summary>400 — malformed body, field validation or bad query.</summary>
    InvalidInput,

    /// <summary>403 — authenticated, but not allowed (defence in depth behind the policies).</summary>
    Forbidden,

    /// <summary>404 — missing or belongs to another business (never 403, to prevent enumeration).</summary>
    NotFound,

    /// <summary>409 — this externalOrderId was already submitted with different content.</summary>
    OrderConflict,

    /// <summary>409 — this Idempotency-Key was already used for a different order.</summary>
    IdempotencyConflict
}
