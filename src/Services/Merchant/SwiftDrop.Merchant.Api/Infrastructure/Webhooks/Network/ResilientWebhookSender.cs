using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Net.Sockets;
using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Domain.Webhooks;
using SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Security;

namespace SwiftDrop.Merchant.Api.Infrastructure.Webhooks.Network;

public sealed class ResilientWebhookSender : IWebhookSender, IDisposable
{
    private const int MaxResponseBytes = 64 * 1024; // 64 KiB
    private const int TimeoutSeconds = 10;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsClient;

    public ResilientWebhookSender(HttpClient? httpClient = null)
    {
        if (httpClient != null)
        {
            _httpClient = httpClient;
            _ownsClient = false;
        }
        else
        {
            var handler = new SocketsHttpHandler
            {
                AllowAutoRedirect = false,
                ConnectTimeout = TimeSpan.FromSeconds(TimeoutSeconds),
                ResponseDrainTimeout = TimeSpan.FromSeconds(2),
                ConnectCallback = async (context, ct) =>
                {
                    var host = context.DnsEndPoint.Host;
                    var port = context.DnsEndPoint.Port;

                    var addresses = await Dns.GetHostAddressesAsync(host, ct);
                    var safeAddress = addresses.FirstOrDefault(a => !SsrfDestinationValidator.IsBlockedAddress(a))
                        ?? throw new InvalidOperationException($"All resolved addresses for '{host}' were blocked by SSRF policy.");

                    var socket = new Socket(safeAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(safeAddress, port), ct);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }
            };

            _httpClient = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(TimeoutSeconds)
            };
            _ownsClient = true;
        }
    }

    public async Task<WebhookSendResult> SendAsync(
        string endpointUrl,
        byte[] bodyBytes,
        byte[] rawSecret,
        string eventId,
        int secretVersion,
        CancellationToken ct)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        var signature = HmacSignatureProvider.ComputeSignature(rawSecret, timestamp, bodyBytes);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
        var content = new ByteArrayContent(bodyBytes);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        request.Content = content;

        request.Headers.Add("X-SwiftDrop-Event-Id", eventId);
        request.Headers.Add("X-SwiftDrop-Timestamp", timestamp);
        request.Headers.Add("X-SwiftDrop-Key-Version", secretVersion.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add("X-SwiftDrop-Signature", signature);
        request.Headers.Add("User-Agent", "SwiftDrop-Webhook/1.0");

        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(TimeoutSeconds));

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token);
            stopwatch.Stop();

            // Read up to MaxResponseBytes safely without logging or persisting the body
            if (response.Content != null)
            {
                var buffer = new byte[MaxResponseBytes];
                await using var stream = await response.Content.ReadAsStreamAsync(timeoutCts.Token);
                _ = await stream.ReadAsync(buffer.AsMemory(0, MaxResponseBytes), timeoutCts.Token);
            }

            var statusCode = (int)response.StatusCode;
            var isSuccess = response.IsSuccessStatusCode;

            TimeSpan? retryAfter = null;
            if (response.Headers.RetryAfter != null)
            {
                if (response.Headers.RetryAfter.Delta.HasValue)
                {
                    retryAfter = response.Headers.RetryAfter.Delta.Value;
                }
                else if (response.Headers.RetryAfter.Date.HasValue)
                {
                    var delta = response.Headers.RetryAfter.Date.Value - DateTimeOffset.UtcNow;
                    if (delta > TimeSpan.Zero)
                    {
                        retryAfter = delta;
                    }
                }
            }

            var canRetry = !isSuccess && WebhookRetryPolicy.IsRetryableStatusCode(statusCode);
            var failureReason = isSuccess ? null : $"HTTP {statusCode} {response.ReasonPhrase}";

            return new WebhookSendResult(
                Success: isSuccess,
                StatusCode: statusCode,
                DurationMs: stopwatch.ElapsedMilliseconds,
                FailureReason: failureReason,
                CanRetry: canRetry,
                RetryAfter: retryAfter);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            stopwatch.Stop();
            return new WebhookSendResult(
                Success: false,
                StatusCode: null,
                DurationMs: stopwatch.ElapsedMilliseconds,
                FailureReason: "Request timed out after 10 seconds.",
                CanRetry: true);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();
            var sanitizedReason = ex.Message.Length > 200 ? ex.Message[..200] : ex.Message;
            return new WebhookSendResult(
                Success: false,
                StatusCode: null,
                DurationMs: stopwatch.ElapsedMilliseconds,
                FailureReason: sanitizedReason,
                CanRetry: true);
        }
    }

    public void Dispose()
    {
        if (_ownsClient)
        {
            _httpClient.Dispose();
        }
    }
}
