using SkiaSharp;
using SwiftDrop.Delivery.Api.Application.CourierProof;

namespace SwiftDrop.Delivery.Api.Infrastructure.CourierProof;

public sealed class SkiaProofImageValidator : IProofImageValidator, IDisposable
{
    // Technical decode budget: 16 megapixels / 64 MiB RGBA, two decodes at a time.
    // Never allocate a bitmap based solely on untrusted dimensions.
    private readonly SemaphoreSlim _decoders = new(2);

    public async Task<DecodedProof> ValidateAsync(ProofFile file, CancellationToken cancellationToken)
    {
        if (file.Content.Length is < 1 or > ProofValidator.MaxFileBytes) throw new InvalidProofException();
        await _decoders.WaitAsync(cancellationToken);
        try
        {
            using var data = SKData.CreateCopy(file.Content);
            using var codec = SKCodec.Create(data);
            if (codec is null || codec.EncodedFormat is not (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg)
                || codec.Info.Width < 1 || codec.Info.Height < 1 || (long)codec.Info.Width * codec.Info.Height > 16 * 1024 * 1024
                || codec.FrameCount > 1) throw new InvalidProofException();
            var png = codec.EncodedFormat == SKEncodedImageFormat.Png;
            if (file.ContentType != (png ? "image/png" : "image/jpeg")
                || (png ? !HasPngEnd(file.Content) : file.Content.Length < 4 || file.Content[^2] != 0xff || file.Content[^1] != 0xd9))
                throw new InvalidProofException();
            var info = new SKImageInfo(codec.Info.Width, codec.Info.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var bitmap = new SKBitmap(info);
            if (bitmap.GetPixels() == IntPtr.Zero || codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success)
                throw new InvalidProofException();
            cancellationToken.ThrowIfCancellationRequested();
            using var image = SKImage.FromBitmap(bitmap);
            using var encoded = image.Encode(png ? SKEncodedImageFormat.Png : SKEncodedImageFormat.Jpeg, 90);
            if (encoded is null) throw new InvalidProofException();
            // Encode pixels afresh: no EXIF/GPS, filenames, arbitrary chunks or
            // appended executable bytes from the original container are retained.
            return new(file.Type, file.ContentType, encoded.ToArray());
        }
        finally { _decoders.Release(); }
    }

    private static bool HasPngEnd(byte[] bytes) => bytes.Length >= 20
        && bytes.AsSpan(bytes.Length - 12).SequenceEqual(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 });

    public void Dispose() => _decoders.Dispose();
}
