namespace SwiftDrop.Merchant.Api.Infrastructure.Authentication.ApiKeys;

/// <summary>Names shared by the API key handler, the caller resolver and the policies.</summary>
public static class ApiKeyAuthenticationDefaults
{
    public const string SchemeName = "MerchantApiKey";
    public const string HeaderName = "X-API-Key";
    public const string AuthenticationMethod = "api_key";
}

/// <summary>
/// Claims issued for an API key. There is deliberately no <c>sub</c> claim, so
/// a merchant system can never be mistaken for a human user.
/// </summary>
public static class ApiKeyClaimTypes
{
    public const string BusinessId = "business_id";
    public const string ApiKeyId = "api_key_id";
    public const string Scope = "scope";
    public const string AuthenticationMethod = "auth";
}
