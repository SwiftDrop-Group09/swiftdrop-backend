using System.Buffers.Binary;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.CourierProof;

namespace SwiftDrop.Delivery.Tests.Courier;

public sealed class CourierProofImageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DecodesRealPixelsAndReencodesOnlyTheAcceptedFormat(bool jpeg)
    {
        using var validator = new SkiaProofImageValidator();
        var type = jpeg ? "image/jpeg" : "image/png";
        var result = await validator.ValidateAsync(new(ProofType.Signature, type, ProofTestSupport.Image(jpeg)), default);
        Assert.Equal(ProofType.Signature, result.Type);
        Assert.Equal(type, result.ContentType);
        Assert.NotEmpty(result.Content);
        using var roundTrip = SkiaSharp.SKBitmap.Decode(result.Content);
        Assert.Equal(8, roundTrip.Width);
        Assert.Equal(6, roundTrip.Height);
    }

    [Theory]
    [InlineData("executable")]
    [InlineData("svg")]
    [InlineData("gif")]
    [InlineData("truncated")]
    [InlineData("spoofed-mime")]
    [InlineData("appended-executable")]
    [InlineData("dimensions")]
    public async Task RejectsMalformedExecutableSpoofedOrUnboundedImageInput(string invalid)
    {
        using var validator = new SkiaProofImageValidator();
        var bytes = ProofTestSupport.Image();
        var mime = "image/png";
        switch (invalid)
        {
            case "executable": bytes = "MZ fake executable"u8.ToArray(); break;
            case "svg": bytes = "<svg onload='evil()'/>"u8.ToArray(); break;
            case "gif": bytes = "GIF89a arbitrary bytes"u8.ToArray(); break;
            case "truncated": bytes = bytes[..^18]; break;
            case "spoofed-mime": mime = "image/jpeg"; break;
            case "appended-executable": bytes = [.. bytes, .. "MZ appended payload"u8.ToArray()]; break;
            case "dimensions": BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(16, 4), 100000); break;
        }
        await Assert.ThrowsAsync<InvalidProofException>(() => validator.ValidateAsync(new(ProofType.Photo, mime, bytes), default));
    }

    [Fact]
    public async Task ReencodingStripsPrivateImageCommentsFromStoredPixels()
    {
        var jpeg = ProofTestSupport.Image(true);
        var comment = System.Text.Encoding.ASCII.GetBytes("Private GPS location and camera identity");
        var length = comment.Length + 2;
        byte[] original = [jpeg[0], jpeg[1], 0xff, 0xfe, (byte)(length >> 8), (byte)length, .. comment, .. jpeg[2..]];
        using var validator = new SkiaProofImageValidator();
        var result = await validator.ValidateAsync(new(ProofType.Photo, "image/jpeg", original), default);
        Assert.DoesNotContain("Private GPS", System.Text.Encoding.ASCII.GetString(result.Content));
        using var decoded = SkiaSharp.SKBitmap.Decode(result.Content);
        Assert.Equal(8, decoded.Width);
    }

    [Fact]
    public void FingerprintBindsContentTypeContentRecipientVersionParcelAndProofKind()
    {
        var id = Guid.NewGuid();
        var file = new ProofFile(ProofType.Photo, "image/png", ProofTestSupport.Image());
        var input = new ProofInput(" Recipient ", 5, [file]);
        var original = ProofValidator.Validate(id, input, "one")!;
        Assert.Equal(original.Fingerprint, ProofValidator.Validate(id, input with { RecipientName = "Recipient" }, "two")!.Fingerprint);
        Assert.NotEqual(original.Fingerprint, ProofValidator.Validate(Guid.NewGuid(), input, "one")!.Fingerprint);
        Assert.NotEqual(original.Fingerprint, ProofValidator.Validate(id, input with { ExpectedVersion = 6 }, "one")!.Fingerprint);
        Assert.NotEqual(original.Fingerprint, ProofValidator.Validate(id, input with { RecipientName = "Someone" }, "one")!.Fingerprint);
        Assert.NotEqual(original.Fingerprint, ProofValidator.Validate(id, input with { Files = [file with { Type = ProofType.Signature }] }, "one")!.Fingerprint);
        Assert.NotEqual(original.Fingerprint, ProofValidator.Validate(id, input with { Files = [file with { Content = ProofTestSupport.Image(shade: 20) }] }, "one")!.Fingerprint);
        Assert.NotEqual(original.Fingerprint, ProofValidator.Validate(id, input with { Files = [file with { ContentType = "image/jpeg" }] }, "one")!.Fingerprint);
    }
}
