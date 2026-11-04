namespace SwiftDrop.Merchant.Api.Application.Merchants;

/// <summary>Discriminated failure reason returned from merchant use-case handlers.</summary>
public enum MerchantFailure
{
    Unauthenticated,
    Forbidden,
    InvalidInput,
    BusinessNotFound,
    ApiKeyNotFound,
    InvalidStatusTransition,
    BusinessNotApproved,
    DuplicateBusiness,
    Unexpected
}

