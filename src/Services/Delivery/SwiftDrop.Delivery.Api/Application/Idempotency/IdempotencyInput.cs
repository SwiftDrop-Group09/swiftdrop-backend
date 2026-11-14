namespace SwiftDrop.Delivery.Api.Application.Idempotency;

public static class IdempotencyInput
{
    public static bool IsValidKey(string? key) => !string.IsNullOrWhiteSpace(key)
        && key.Length <= 128 && !key.Any(char.IsControl);
}
