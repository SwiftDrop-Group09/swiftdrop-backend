using System.Globalization;
using SkiaSharp;

namespace SwiftDrop.Delivery.Api.Application.DeliveryReads;

public sealed class SkiaReceiptPdfRenderer : IReceiptPdfRenderer
{
    public byte[] Render(ParcelReceiptResponse receipt)
    {
        using var stream = new MemoryStream();
        using var document = SKDocument.CreatePdf(stream);
        using var canvas = document.BeginPage(595, 842); // A4 points

        canvas.Clear(SKColors.White);

        void Draw(string text, float x, float y, SKFont font, SKPaint paint) =>
            canvas.DrawText(text, x, y, SKTextAlign.Left, font, paint);

        using var headerPaint = new SKPaint { Color = new SKColor(15, 23, 42), IsAntialias = true };
        canvas.DrawRect(0, 0, 595, 96, headerPaint);

        static SKTypeface SafeTypeface(bool bold)
        {
            var candidates = bold
                ? new[]
                {
                    "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf",
                    "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf",
                    @"C:\Windows\Fonts\arialbd.ttf",
                    @"C:\Windows\Fonts\segoeuib.ttf",
                }
                : new[]
                {
                    "/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf",
                    "/usr/share/fonts/dejavu/DejaVuSans.ttf",
                    @"C:\Windows\Fonts\arial.ttf",
                    @"C:\Windows\Fonts\segoeui.ttf",
                };

            foreach (var path in candidates)
            {
                if (File.Exists(path))
                {
                    var tf = SKTypeface.FromFile(path);
                    if (tf is not null) return tf;
                }
            }

            return SKTypeface.FromFamilyName("Helvetica", bold ? SKFontStyle.Bold : SKFontStyle.Normal)
                ?? SKTypeface.FromFamilyName("DejaVu Sans", bold ? SKFontStyle.Bold : SKFontStyle.Normal)
                ?? SKTypeface.Default;
        }

        using var boldTypeface = SafeTypeface(true);
        using var regularTypeface = SafeTypeface(false);

        using var brandFont = new SKFont(boldTypeface, 24);
        using var whitePaint = new SKPaint { Color = SKColors.White, IsAntialias = true };
        Draw("SwiftDrop", 40, 48, brandFont, whitePaint);

        using var subtitleFont = new SKFont(regularTypeface, 11);
        using var mutedLightPaint = new SKPaint { Color = new SKColor(203, 213, 225), IsAntialias = true };
        Draw("Official Delivery Receipt", 40, 70, subtitleFont, mutedLightPaint);

        using var titleFont = new SKFont(boldTypeface, 14);
        using var labelFont = new SKFont(boldTypeface, 10);
        using var bodyFont = new SKFont(regularTypeface, 10);
        using var darkPaint = new SKPaint { Color = new SKColor(15, 23, 42), IsAntialias = true };
        using var mutedPaint = new SKPaint { Color = new SKColor(100, 116, 139), IsAntialias = true };
        using var linePaint = new SKPaint { Color = new SKColor(226, 232, 240), StrokeWidth = 1 };

        float y = 126;
        Draw("Receipt Reference:", 40, y, labelFont, mutedPaint);
        y += 18;
        Draw(receipt.ReceiptNumber, 40, y, titleFont, darkPaint);
        y += 22;
        Draw($"Tracking Number: {receipt.TrackingNumber}", 40, y, bodyFont, mutedPaint);
        Draw($"Status: {receipt.Status}", 380, y, labelFont, darkPaint);
        y += 16;
        Draw($"Booked (UTC): {receipt.CreatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC", 40, y, bodyFont, mutedPaint);
        if (receipt.DeliveredAt is { } deliveredAt)
        {
            Draw($"Delivered (UTC): {deliveredAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC", 320, y, bodyFont, mutedPaint);
        }

        y += 20;
        canvas.DrawLine(40, y, 555, y, linePaint);
        y += 24;

        Draw("PICKUP ADDRESS", 40, y, labelFont, mutedPaint);
        Draw("DELIVERY ADDRESS & RECIPIENT", 310, y, labelFont, mutedPaint);
        y += 16;
        Draw(receipt.PickupAddress.Street, 40, y, bodyFont, darkPaint);
        Draw(receipt.Recipient.Name, 310, y, labelFont, darkPaint);
        y += 15;
        Draw($"{receipt.PickupAddress.City}, {receipt.PickupAddress.PostalCode}", 40, y, bodyFont, darkPaint);
        Draw(receipt.Recipient.PhoneNumber, 310, y, bodyFont, mutedPaint);
        y += 15;
        Draw(receipt.DeliveryAddress.Street, 310, y, bodyFont, darkPaint);
        y += 15;
        Draw($"{receipt.DeliveryAddress.City}, {receipt.DeliveryAddress.PostalCode}", 310, y, bodyFont, darkPaint);

        y += 24;
        canvas.DrawLine(40, y, 555, y, linePaint);
        y += 24;

        Draw("PARCEL DETAILS", 40, y, labelFont, mutedPaint);
        y += 16;
        Draw($"Size Tier: {receipt.PackageDetails.Size}", 40, y, bodyFont, darkPaint);
        Draw($"Weight: {receipt.PackageDetails.WeightKg.ToString("0.00", CultureInfo.InvariantCulture)} kg", 200, y, bodyFont, darkPaint);
        Draw($"Pricing Version: {receipt.PricingVersion}", 360, y, bodyFont, mutedPaint);

        y += 28;
        canvas.DrawLine(40, y, 555, y, linePaint);
        y += 24;

        Draw("ITEMIZED FEE BREAKDOWN", 40, y, labelFont, mutedPaint);
        y += 22;

        void DrawFeeRow(string label, decimal amount, bool bold = false)
        {
            var font = bold ? titleFont : bodyFont;
            Draw(label, 40, y, font, darkPaint);
            var formatted = $"{receipt.Currency} {amount.ToString("0.00", CultureInfo.InvariantCulture)}";
            Draw(formatted, 430, y, font, darkPaint);
            y += bold ? 24 : 18;
        }

        DrawFeeRow("Base Delivery Fee", receipt.BaseFee);
        DrawFeeRow("Weight Surcharge", receipt.WeightFee);
        y += 4;
        canvas.DrawLine(40, y, 555, y, linePaint);
        y += 20;
        DrawFeeRow("Total Charged", receipt.Total, bold: true);

        y += 24;
        Draw($"Price Calculated: {receipt.CalculatedAt.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC", 40, y, bodyFont, mutedPaint);

        document.EndPage();
        document.Close();
        return stream.ToArray();
    }
}
