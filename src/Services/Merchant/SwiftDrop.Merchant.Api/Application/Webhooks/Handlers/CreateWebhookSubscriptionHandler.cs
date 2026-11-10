using SwiftDrop.Merchant.Api.Application.Webhooks.Contracts;
using SwiftDrop.Merchant.Api.Application.Webhooks.Ports;
using SwiftDrop.Merchant.Api.Domain.Webhooks;

namespace SwiftDrop.Merchant.Api.Application.Webhooks.Handlers;

public sealed class CreateWebhookSubscriptionHandler(
    ICurrentMerchantContext merchantContext,
    IWebhookSubscriptionStore subscriptionStore,
    IWebhookDestinationValidator destinationValidator,
    IWebhookSecretProtector secretProtector,
    IWebhookClock clock)
{
    private const int MaxActiveSubscriptionsPerBusiness = 10;

    public async Task<(OneTimeWebhookResponse? Response, WebhookFailure? Failure)> HandleAsync(
        CreateWebhookRequest request,
        CancellationToken ct)
    {
        if (!merchantContext.IsAuthenticated)
        {
            return (null, WebhookFailure.Unauthenticated("Authentication is required."));
        }

        if (!merchantContext.IsApprovedMerchant || !merchantContext.CanManageWebhooks || !merchantContext.BusinessId.HasValue)
        {
            return (null, WebhookFailure.Forbidden("Only approved merchants may manage webhooks."));
        }

        var destinationUrl = request.DestinationUrl;
        if (string.IsNullOrWhiteSpace(destinationUrl))
        {
            return (null, WebhookFailure.InvalidInput("Destination URL is required."));
        }

        if (!Uri.TryCreate(destinationUrl, UriKind.Absolute, out var parsedUri) || parsedUri.Scheme != Uri.UriSchemeHttps)
        {
            return (null, WebhookFailure.InvalidInput("Endpoint URL must be an absolute HTTPS URL."));
        }

        if (request.Events is null || request.Events.Count == 0)
        {
            return (null, WebhookFailure.InvalidInput("At least one event must be selected."));
        }

        IReadOnlySet<string> validatedEvents;
        try
        {
            validatedEvents = WebhookEvents.ValidateAndNormalize(request.Events);
        }
        catch (ArgumentException ex)
        {
            return (null, WebhookFailure.InvalidInput(ex.Message));
        }

        var isDestinationSafe = await destinationValidator.ValidateAsync(destinationUrl, ct);
        if (!isDestinationSafe)
        {
            return (null, WebhookFailure.InvalidInput("Endpoint URL is unsafe, unresolvable, or violates destination security policy."));
        }

        var businessId = merchantContext.BusinessId.Value;
        var existingSubscriptions = await subscriptionStore.ListByBusinessIdAsync(businessId, ct);
        var activeCount = existingSubscriptions.Count(s => s.IsActive);
        if (activeCount >= MaxActiveSubscriptionsPerBusiness)
        {
            return (null, WebhookFailure.Conflict($"Maximum active webhooks ({MaxActiveSubscriptionsPerBusiness}) reached."));
        }

        var (plaintextSecret, ciphertext, nonce, tag, keyVersion) = secretProtector.GenerateAndEncrypt();
        var subscription = WebhookSubscription.Create(
            businessId,
            destinationUrl,
            validatedEvents,
            ciphertext,
            nonce,
            tag,
            keyVersion,
            clock.UtcNow);

        await subscriptionStore.AddAsync(subscription, ct);

        var response = new OneTimeWebhookResponse(
            subscription.Id,
            subscription.EndpointUrl,
            subscription.SubscribedEvents.OrderBy(e => e).ToList(),
            plaintextSecret,
            subscription.SecretVersion,
            subscription.CreatedAt);

        return (response, null);
    }
}
