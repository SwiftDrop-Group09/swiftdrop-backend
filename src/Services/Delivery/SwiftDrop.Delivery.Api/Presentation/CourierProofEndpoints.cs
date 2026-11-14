using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Net.Http.Headers;
using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Application.CourierStatus;
using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Presentation;

public sealed class CourierProofEndpointMetadata;

public static class CourierProofEndpoints
{
    private const long MaxRequestBytes = 2 * ProofValidator.MaxFileBytes + 64 * 1024;

    public static void MapCourierProof(this WebApplication app)
    {
        app.MapPost("/api/v1/deliveries/{id}/pod", async (string id, HttpContext context,
            ICurrentDeliveryUser actor, CourierProofHandler handler, CancellationToken cancellationToken) =>
        {
            if (!actor.IsAuthenticated || actor.UserId is not { } userId || userId == Guid.Empty)
                return CourierStatusProblems.Failure(context, CourierStatusFailure.Unauthenticated);
            if (!actor.IsCourier) return CourierStatusProblems.Failure(context, CourierStatusFailure.Forbidden);
            if (context.Request.Headers["Idempotency-Key"].Count != 1 || context.Request.Query.Count != 0) return Invalid(context);
            ProofInput input;
            try { input = await ReadAsync(context.Request, cancellationToken); }
            catch (Exception exception) when (exception is InvalidDataException or IOException or BadHttpRequestException or FormatException)
            { return Invalid(context); }
            var result = await handler.ExecuteAsync(id, input, context.Request.Headers["Idempotency-Key"][0],
                QuoteProblems.CorrelationId(context), cancellationToken);
            return result.Response is { } response
                ? Results.Text(response.Body, "application/json", Encoding.UTF8, response.StatusCode)
                : CourierStatusProblems.Failure(context, result.Failure!.Value, result.Errors, result.CurrentVersion);
        }).RequireAuthorization().WithName(ProofValidator.Operation).WithMetadata(new CourierProofEndpointMetadata())
            .Produces<SwiftDrop.Delivery.Api.Application.Bookings.ParcelBookingResponse>()
            .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409).ProducesProblem(503);
    }

    private static async Task<ProofInput> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        if (request.ContentLength > MaxRequestBytes || !MediaTypeHeaderValue.TryParse(request.ContentType, out var media)
            || !string.Equals(media.MediaType.Value, "multipart/form-data", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException();
        var boundary = HeaderUtilities.RemoveQuotes(media.Boundary).Value;
        if (string.IsNullOrWhiteSpace(boundary) || boundary.Length > 128) throw new InvalidDataException();
        var limit = request.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
        if (limit is { IsReadOnly: false }) limit.MaxRequestBodySize = MaxRequestBytes;
        using var body = new LimitedBody(request.Body, MaxRequestBytes);
        var reader = new MultipartReader(boundary, body)
        {
            HeadersCountLimit = 8, HeadersLengthLimit = 4096, BodyLengthLimit = ProofValidator.MaxFileBytes
        };
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var files = new List<ProofFile>();
        string? name = null;
        long? version = null;
        while (await reader.ReadNextSectionAsync(cancellationToken) is { } section)
        {
            if (!ContentDispositionHeaderValue.TryParse(section.ContentDisposition, out var disposition)
                || disposition.DispositionType != "form-data") throw new InvalidDataException();
            var field = HeaderUtilities.RemoveQuotes(disposition.Name).Value;
            if (field is null || !seen.Add(field)) throw new InvalidDataException();
            var isFile = disposition.FileName.HasValue || disposition.FileNameStar.HasValue;
            if (field is "photo" or "signature")
            {
                if (!isFile || files.Count >= 2 || section.ContentType is not ("image/jpeg" or "image/png")) throw new InvalidDataException();
                using var content = new MemoryStream();
                await section.Body.CopyToAsync(content, cancellationToken);
                if (content.Length is < 1 or > ProofValidator.MaxFileBytes) throw new InvalidDataException();
                files.Add(new(field == "photo" ? ProofType.Photo : ProofType.Signature, section.ContentType, content.ToArray()));
            }
            else
            {
                if (isFile || field is not ("recipientName" or "expectedVersion")) throw new InvalidDataException();
                using var value = new MemoryStream();
                var buffer = new byte[512];
                int read;
                while ((read = await section.Body.ReadAsync(buffer, cancellationToken)) != 0)
                {
                    if (value.Length + read > 1024) throw new InvalidDataException();
                    await value.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                string text;
                try { text = new UTF8Encoding(false, true).GetString(value.ToArray()); }
                catch (DecoderFallbackException) { throw new InvalidDataException(); }
                if (field == "recipientName") name = text;
                else
                {
                    if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)) throw new InvalidDataException();
                    version = parsed;
                }
            }
        }
        return new(name, version, files);
    }

    private static IResult Invalid(HttpContext context) => CourierStatusProblems.Failure(context, CourierStatusFailure.InvalidInput,
        new Dictionary<string, string[]> { ["proof"] = ["Supply photo and/or signature (JPEG/PNG, at most 5 MiB each), recipientName and expectedVersion as multipart form data."] });

    private sealed class LimitedBody(Stream inner, long maximum) : Stream
    {
        private long _read;
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var read = await inner.ReadAsync(buffer, cancellationToken);
            _read += read;
            if (_read > maximum) throw new InvalidDataException();
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = inner.Read(buffer, offset, count);
            _read += read;
            if (_read > maximum) throw new InvalidDataException();
            return read;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
