using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SwiftDrop.Delivery.Api.Application.Idempotency;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.CourierProof;

public static class ProofValidator
{
    public const string Operation = "CompleteDeliveryWithProof";
    public const int MaxFileBytes = 5 * 1024 * 1024;

    public static ProofCommand? Validate(Guid parcelId, ProofInput input, string? key)
    {
        if (!IdempotencyInput.IsValidKey(key) || string.IsNullOrWhiteSpace(input.RecipientName)
            || input.RecipientName.Length > 120 || input.ExpectedVersion is not >= 1
            || input.Files.Count is < 1 or > 2 || input.Files.Select(x => x.Type).Distinct().Count() != input.Files.Count
            || input.Files.Any(x => !Enum.IsDefined(x.Type) || x.Content.Length is < 1 or > MaxFileBytes
                || x.ContentType is not ("image/jpeg" or "image/png"))) return null;
        var name = input.RecipientName.Trim();
        var canonical = JsonSerializer.Serialize(new
        {
            parcelId = parcelId.ToString("D"), recipientName = name, expectedVersion = input.ExpectedVersion.Value,
            files = input.Files.OrderBy(x => x.Type).Select(x => new
            {
                type = x.Type.ToString(), contentType = x.ContentType,
                sha256 = Convert.ToHexStringLower(SHA256.HashData(x.Content))
            })
        });
        return new(parcelId, name, input.ExpectedVersion.Value, key!,
            Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))), input.Files);
    }
}
