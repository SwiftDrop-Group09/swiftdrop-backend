namespace SwiftDrop.Merchant.Api.Domain.Webhooks;

public static class WebhookGuard
{
    public static Guid Id(Guid value, string name) => value != Guid.Empty
        ? value : throw new ArgumentException("An identifier is required.", name);

    public static string HttpsUrl(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        var trimmed = value.Trim();
        if (trimmed.Length > 2048)
        {
            throw new ArgumentOutOfRangeException(name, "Endpoint URL cannot exceed 2048 characters.");
        }

        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Webhook endpoint must be an absolute HTTPS URL.", name);
        }

        if (uri.Port != 443 && !uri.IsDefaultPort)
        {
            throw new ArgumentException("Webhook endpoint must connect to HTTPS port 443.", name);
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ArgumentException("Webhook endpoint cannot contain userinfo credentials.", name);
        }

        if (!string.IsNullOrEmpty(uri.Fragment))
        {
            throw new ArgumentException("Webhook endpoint cannot contain URL fragments.", name);
        }

        return uri.ToString();
    }

    public static byte[] NonEmptyBytes(byte[] value, string name)
    {
        ArgumentNullException.ThrowIfNull(value, name);
        if (value.Length == 0)
        {
            throw new ArgumentException("Byte payload cannot be empty.", name);
        }
        return value;
    }
}
