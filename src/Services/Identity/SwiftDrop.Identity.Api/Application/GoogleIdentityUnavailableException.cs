namespace SwiftDrop.Identity.Api.Application;

public enum GoogleIdentityUnavailableReason
{
    NotConfigured,
    InvalidConfiguration,
    ProviderUnavailable
}

public sealed class GoogleIdentityUnavailableException(GoogleIdentityUnavailableReason reason)
    : Exception("Google sign-in is unavailable.")
{
    public GoogleIdentityUnavailableReason Reason { get; } = reason;
}
