using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SwiftDrop.Delivery.Api.Application.Assistant;

namespace SwiftDrop.Delivery.Api.Infrastructure.Assistant;

public sealed class AssistantGeminiClient(HttpClient client, IConfiguration configuration, IOptions<AssistantLimits> limits)
{
    public async Task<string?> ConfirmPublicToolAsync(string tool, CancellationToken ct)
    {
        var description = tool switch
        {
            AssistantToolNames.TrackParcel => "Read the current public status of the parcel selected in the application.",
            AssistantToolNames.EstimateDeliveryFee => "Preview the fee for the route and package selected in the application.",
            _ => null
        };
        var key = (configuration["AI:ApiKey"] ?? configuration["DELIVERY_AI_API_KEY"] ?? configuration["GEMINI_API_KEY"])?.Trim();
        var model = configuration["GEMINI_MODEL"] ?? "gemini-3.5-flash-lite";
        if (description is null || key is not { Length: > 0 and <= 128 } ||
            key.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '_')) ||
            !model.StartsWith("gemini-", StringComparison.Ordinal) || model.Length > 64 ||
            model.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ('-' or '.')))
            return null;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(limits.Value.ProviderTimeoutSeconds));
        using var message = new HttpRequestMessage(HttpMethod.Post,
            $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent");
        message.Headers.Add("x-goog-api-key", key);
        // The application owns the validated input and results. The model receives only this public intent.
        message.Content = JsonContent.Create(new
        {
            contents = new[] { new { role = "user", parts = new[] { new { text = description } } } },
            tools = new[] { new { functionDeclarations = new[] { new { name = tool, description } } } },
            toolConfig = new { functionCallingConfig = new { mode = "ANY", allowedFunctionNames = new[] { tool } } },
            generationConfig = new { maxOutputTokens = 1024 }
        });

        try
        {
            using var response = await client.SendAsync(message, timeout.Token);
            if (!response.IsSuccessStatusCode)
                return null;

            using var payload = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(timeout.Token),
                cancellationToken: timeout.Token);
            return MatchesPublicTool(payload.RootElement, tool, limits.Value.ProviderToolCalls) ? model : null;
        }
        catch (Exception exception) when (!ct.IsCancellationRequested &&
            exception is HttpRequestException or OperationCanceledException or JsonException or IOException or InvalidOperationException)
        {
            return null;
        }
    }

    private static bool MatchesPublicTool(JsonElement root, string tool, int limit)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("candidates", out var candidates) ||
            candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() != 1 ||
            candidates[0].ValueKind != JsonValueKind.Object || !candidates[0].TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Object || !content.TryGetProperty("parts", out var parts) ||
            parts.ValueKind != JsonValueKind.Array)
            return false;

        var calls = 0;
        foreach (var part in parts.EnumerateArray())
        {
            if (part.ValueKind != JsonValueKind.Object)
                return false;
            if (!part.TryGetProperty("functionCall", out var call))
                continue;

            if (++calls > limit || call.ValueKind != JsonValueKind.Object ||
                !call.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String ||
                name.GetString() != tool || call.TryGetProperty("args", out var arguments) &&
                (arguments.ValueKind != JsonValueKind.Object || arguments.EnumerateObject().Any()))
                return false;
        }

        return calls == 1;
    }
}
