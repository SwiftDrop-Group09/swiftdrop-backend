using System.Security.Cryptography;

using SwiftDrop.Delivery.Api.Application.Bookings;

namespace SwiftDrop.Delivery.Api.Infrastructure.Bookings;

public sealed class CryptographicTrackingNumberGenerator : ITrackingNumberGenerator
{
    public string Generate() => "TRK-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
}
