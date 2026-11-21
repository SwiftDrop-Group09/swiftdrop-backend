using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using SwiftDrop.Contracts.Http.V1;

namespace SwiftDrop.Delivery.Api.Infrastructure.Assistant;

public sealed class MerchantAssistantOrderClient(
    HttpClient client, IHttpContextAccessor request, IConfiguration settings)
{
    public async Task<(MerchantAssistantOrderPage? Page, bool Forbidden)> ReadAsync(CancellationToken ct)
    {
        var header = request.HttpContext?.Request.Headers.Authorization;
        if (header is not { Count: 1 } ||
            !AuthenticationHeaderValue.TryParse(header.Value[0], out var credential) ||
            !credential.Scheme.Equals("Bearer", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(credential.Parameter))
        {
            return (null, true);
        }

        if (!Uri.TryCreate(settings["AI:MerchantApiOrigin"], UriKind.Absolute, out var origin) ||
            origin.Scheme is not ("http" or "https") || origin.UserInfo.Length != 0 ||
            origin.AbsolutePath != "/" || origin.Query.Length != 0 || origin.Fragment.Length != 0)
        {
            return (null, false);
        }

        using var message = new HttpRequestMessage(HttpMethod.Get, new Uri(origin, MerchantOrderApi.AssistantOrdersPath));
        message.Headers.Authorization = credential;
        try
        {
            using var response = await client.SendAsync(message, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                return (null, true);
            }
            if (!response.IsSuccessStatusCode)
            {
                return (null, false);
            }

            var page = await response.Content.ReadFromJsonAsync<MerchantAssistantOrderPage>(cancellationToken: ct);
            return page is { Items: { } items } && page.TotalCount >= items.Count &&
                items.Count <= MerchantOrderApi.AssistantPageSize ? (page, false) : (null, false);
        }
        catch (Exception exception) when (!ct.IsCancellationRequested &&
            exception is HttpRequestException or OperationCanceledException or JsonException or IOException)
        {
            return (null, false);
        }
    }
}
