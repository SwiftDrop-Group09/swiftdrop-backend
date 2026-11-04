using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SwiftDrop.Delivery.Api.Infrastructure.CourierProof;

internal static class ProofObjectKeys
{
    public static bool IsValid(string reference)
    {
        var parts = reference.Split('/');
        return parts.Length == 4 && parts[0] == "proofs"
            && parts[1].Length == 32 && Guid.TryParseExact(parts[1], "N", out var parcel) && parcel != Guid.Empty
            && parts[2].Length == 32 && Guid.TryParseExact(parts[2], "N", out var upload) && upload != Guid.Empty
            && parts[3] is "photo.png" or "photo.jpg" or "signature.png" or "signature.jpg";
    }

    public static long LockId(string reference)
    {
        if (!IsValid(reference)) throw new ArgumentException("Only server-generated proof keys may be locked.", nameof(reference));
        return BinaryPrimitives.ReadInt64BigEndian(SHA256.HashData(Encoding.UTF8.GetBytes(reference)));
    }
}
