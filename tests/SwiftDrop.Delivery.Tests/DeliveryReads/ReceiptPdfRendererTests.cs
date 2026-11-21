using System;
using System.Text;
using SwiftDrop.Delivery.Api.Application.DeliveryReads;
using SwiftDrop.Delivery.Api.Application.Quotes;
using Xunit;

namespace SwiftDrop.Delivery.Tests.DeliveryReads;

public sealed class ReceiptPdfRendererTests
{
    [Fact]
    public void Render_ProducesNonEmptyPdfWithPdfHeader()
    {
        var renderer = new SkiaReceiptPdfRenderer();
        var sample = new ParcelReceiptResponse(
            ReceiptNumber: "REC-2026-0001",
            ParcelId: Guid.NewGuid(),
            TrackingNumber: "TRK-987654321",
            Status: "Delivered",
            CreatedAt: DateTimeOffset.UtcNow.AddDays(-2),
            DeliveredAt: DateTimeOffset.UtcNow.AddDays(-1),
            PickupAddress: new NormalizedAddress("123 Main St", "Colombo", "00100", Guid.NewGuid()),
            DeliveryAddress: new NormalizedAddress("456 Galle Rd", "Colombo", "00300", Guid.NewGuid()),
            Recipient: new NormalizedRecipient("Jane Doe", "+94771234567"),
            PackageDetails: new ReadPackageDetails(1.5m, "Small"),
            BaseFee: 450.00m,
            WeightFee: 50.00m,
            Total: 500.00m,
            Currency: "LKR",
            PricingVersion: "v1",
            CalculatedAt: DateTimeOffset.UtcNow.AddDays(-2));

        var bytes = renderer.Render(sample);

        Assert.NotNull(bytes);
        Assert.NotEmpty(bytes);
        var header = Encoding.ASCII.GetString(bytes.AsSpan(0, Math.Min(10, bytes.Length)));
        Assert.StartsWith("%PDF-", header);
        Assert.True(bytes.Length > 1000);
    }
}
