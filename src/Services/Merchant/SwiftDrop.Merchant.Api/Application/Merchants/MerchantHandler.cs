using Microsoft.EntityFrameworkCore;
using SwiftDrop.Merchant.Api.Domain;
using SwiftDrop.Merchant.Api.Infrastructure.Persistence;

namespace SwiftDrop.Merchant.Api.Application.Merchants;

public sealed class MerchantHandler(
    ICurrentUser currentUser,
    MerchantDbContext db,
    TimeProvider timeProvider)
{
    // ── Registration ────────────────────────────────────────────────────────────

    public async Task<(BusinessResponse? Response, MerchantFailure? Failure,
        IReadOnlyDictionary<string, string[]>? Errors)>
        RegisterAsync(RegisterMerchantInput? input, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return Fail(MerchantFailure.Unauthenticated);
        }

        var errors = ValidateRegisterInput(input);
        if (errors is not null)
        {
            return (null, MerchantFailure.InvalidInput, errors);
        }

        // Guard: one business per user
        var already = await db.Businesses.AnyAsync(b => b.UserId == userId, cancellationToken);
        if (already)
        {
            return Fail(MerchantFailure.DuplicateBusiness);
        }

        var business = Business.Register(
            userId,
            input!.BusinessName!.Trim(),
            input.ContactEmail!.Trim(),
            input.ContactPhone!.Trim(),
            input.PickupAddress!.Trim(),
            timeProvider.GetUtcNow());

        db.Businesses.Add(business);
        await db.SaveChangesAsync(cancellationToken);

        return (ToResponse(business), null, null);
    }

    // ── Get own merchant ────────────────────────────────────────────────────────

    public async Task<(BusinessResponse? Response, MerchantFailure? Failure,
        IReadOnlyDictionary<string, string[]>? Errors)>
        GetMeAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return Fail(MerchantFailure.Unauthenticated);
        }

        var business = await db.Businesses.FirstOrDefaultAsync(b => b.UserId == userId, cancellationToken);
        if (business is null)
        {
            return Fail(MerchantFailure.BusinessNotFound);
        }

        return (ToResponse(business), null, null);
    }

    // ── Admin: list all merchants ───────────────────────────────────────────────

    public async Task<(IReadOnlyList<BusinessResponse>? Response, MerchantFailure? Failure,
        IReadOnlyDictionary<string, string[]>? Errors)>
        ListAllAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return (null, MerchantFailure.Unauthenticated, null);
        }

        if (!currentUser.IsAdmin)
        {
            return (null, MerchantFailure.Forbidden, null);
        }

        var businesses = await db.Businesses
            .OrderBy(b => b.RegisteredAt)
            .ToListAsync(cancellationToken);

        return (businesses.Select(ToResponse).ToList(), null, null);
    }

    // ── Admin: set status ───────────────────────────────────────────────────────

    public async Task<(BusinessResponse? Response, MerchantFailure? Failure,
        IReadOnlyDictionary<string, string[]>? Errors)>
        SetStatusAsync(Guid businessId, SetMerchantStatusInput? input, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } adminId || adminId == Guid.Empty)
        {
            return Fail(MerchantFailure.Unauthenticated);
        }

        if (!currentUser.IsAdmin)
        {
            return Fail(MerchantFailure.Forbidden);
        }

        var errors = ValidateStatusInput(input);
        if (errors is not null)
        {
            return (null, MerchantFailure.InvalidInput, errors);
        }

        var business = await db.Businesses.FindAsync([businessId], cancellationToken);
        if (business is null)
        {
            return Fail(MerchantFailure.BusinessNotFound);
        }

        var targetStatus = Enum.Parse<BusinessStatus>(input!.Status!, ignoreCase: true);

        try
        {
            switch (targetStatus)
            {
                case BusinessStatus.Approved:
                    business.Approve(adminId);
                    break;
                case BusinessStatus.Rejected:
                    business.Reject(adminId, input.Reason!);
                    break;
                case BusinessStatus.Suspended:
                    business.Suspend(adminId, input.Reason!);
                    break;
                default:
                    return (null, MerchantFailure.InvalidInput,
                        new Dictionary<string, string[]> { ["status"] = ["Status must be Approved, Rejected, or Suspended."] });
            }
        }
        catch (InvalidOperationException ex)
        {
            return (null, MerchantFailure.InvalidStatusTransition,
                new Dictionary<string, string[]> { ["status"] = [ex.Message] });
        }

        await db.SaveChangesAsync(cancellationToken);
        return (ToResponse(business), null, null);
    }

    // ── API Keys ────────────────────────────────────────────────────────────────

    public async Task<(OneTimeApiKeyResponse? Response, MerchantFailure? Failure,
        IReadOnlyDictionary<string, string[]>? Errors)>
        CreateApiKeyAsync(CreateApiKeyInput? input, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return (null, MerchantFailure.Unauthenticated, null);
        }

        // Approval and ownership are authoritative in the Merchant service.
        // A static Merchant role in the Identity JWT may be missing when the
        // token was issued before administrator approval.
        var business = await db.Businesses.FirstOrDefaultAsync(b => b.UserId == userId, cancellationToken);
        if (business is null)
        {
            return (null, MerchantFailure.BusinessNotFound, null);
        }

        if (business.Status != BusinessStatus.Approved)
        {
            return (null, MerchantFailure.BusinessNotApproved, null);
        }

        if (input?.Scopes is not { Count: > 0 })
        {
            return (null, MerchantFailure.InvalidInput,
                new Dictionary<string, string[]> { ["scopes"] = ["At least one scope is required."] });
        }

        IReadOnlyList<string> validatedScopes;
        try
        {
            validatedScopes = ApiKeyScopes.ParseAndValidate(input.Scopes);
        }
        catch (ArgumentException ex)
        {
            return (null, MerchantFailure.InvalidInput,
                new Dictionary<string, string[]> { ["scopes"] = [ex.Message] });
        }

        var (entity, plaintextKey) = ApiKey.Create(business.Id, validatedScopes, timeProvider.GetUtcNow());
        db.ApiKeys.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        return (new OneTimeApiKeyResponse(entity.Id, plaintextKey, entity.GetScopesList()), null, null);
    }

    public async Task<(IReadOnlyList<ApiKeyResponse>? Response, MerchantFailure? Failure,
        IReadOnlyDictionary<string, string[]>? Errors)>
        ListApiKeysAsync(CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return (null, MerchantFailure.Unauthenticated, null);
        }

        var business = await db.Businesses.FirstOrDefaultAsync(b => b.UserId == userId, cancellationToken);
        if (business is null)
        {
            return (null, MerchantFailure.BusinessNotFound, null);
        }

        if (business.Status != BusinessStatus.Approved)
        {
            return (null, MerchantFailure.BusinessNotApproved, null);
        }

        var keys = await db.ApiKeys
            .Where(k => k.BusinessId == business.Id)
            .OrderBy(k => k.CreatedAt)
            .ToListAsync(cancellationToken);

        return (keys.Select(ToApiKeyResponse).ToList(), null, null);
    }

    public async Task<(bool Success, MerchantFailure? Failure,
        IReadOnlyDictionary<string, string[]>? Errors)>
        RevokeApiKeyAsync(Guid keyId, CancellationToken cancellationToken = default)
    {
        if (!currentUser.IsAuthenticated || currentUser.UserId is not { } userId || userId == Guid.Empty)
        {
            return (false, MerchantFailure.Unauthenticated, null);
        }

        var business = await db.Businesses.FirstOrDefaultAsync(b => b.UserId == userId, cancellationToken);
        if (business is null)
        {
            return (false, MerchantFailure.BusinessNotFound, null);
        }

        if (business.Status != BusinessStatus.Approved)
        {
            return (false, MerchantFailure.BusinessNotApproved, null);
        }

        var key = await db.ApiKeys.FindAsync([keyId], cancellationToken);
        if (key is null || key.BusinessId != business.Id)
        {
            // Do not reveal whether a key exists for a different business
            return (false, MerchantFailure.ApiKeyNotFound, null);
        }

        key.Revoke();
        await db.SaveChangesAsync(cancellationToken);
        return (true, null, null);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────

    private static BusinessResponse ToResponse(Business b) =>
        new(b.Id, b.UserId, b.BusinessName, b.ContactEmail, b.ContactPhone,
            b.PickupAddress, b.Status.ToString(), b.RejectionReason,
            b.RegisteredAt, b.ApprovedAt, b.ApprovedBy);

    private static ApiKeyResponse ToApiKeyResponse(ApiKey k) =>
        new(k.Id, k.BusinessId, k.KeyPrefix, k.GetScopesList(),
            k.IsActive, k.CreatedAt, k.RevokedAt);

    private static (BusinessResponse?, MerchantFailure?, IReadOnlyDictionary<string, string[]>?)
        Fail(MerchantFailure failure) => (null, failure, null);

    private static IReadOnlyDictionary<string, string[]>? ValidateRegisterInput(RegisterMerchantInput? input)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(input?.BusinessName))
            errors["businessName"] = ["Business name is required."];
        else if (input.BusinessName.Trim().Length > 200)
            errors["businessName"] = ["Business name must not exceed 200 characters."];

        if (string.IsNullOrWhiteSpace(input?.ContactEmail))
            errors["contactEmail"] = ["Contact email is required."];

        if (string.IsNullOrWhiteSpace(input?.ContactPhone))
            errors["contactPhone"] = ["Contact phone is required."];
        else if (input.ContactPhone.Trim().Length > 32)
            errors["contactPhone"] = ["Contact phone must not exceed 32 characters."];

        if (string.IsNullOrWhiteSpace(input?.PickupAddress))
            errors["pickupAddress"] = ["Pickup address is required."];
        else if (input.PickupAddress.Trim().Length > 500)
            errors["pickupAddress"] = ["Pickup address must not exceed 500 characters."];

        return errors.Count > 0 ? errors : null;
    }

    private static IReadOnlyDictionary<string, string[]>? ValidateStatusInput(SetMerchantStatusInput? input)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(input?.Status))
        {
            errors["status"] = ["Status is required."];
            return errors;
        }

        if (!Enum.TryParse<BusinessStatus>(input.Status, ignoreCase: true, out var parsed)
            || parsed == BusinessStatus.Pending)
        {
            errors["status"] = ["Status must be Approved, Rejected, or Suspended."];
            return errors;
        }

        if (parsed is BusinessStatus.Rejected or BusinessStatus.Suspended
            && string.IsNullOrWhiteSpace(input.Reason))
        {
            errors["reason"] = ["Reason is required when rejecting or suspending."];
        }

        return errors.Count > 0 ? errors : null;
    }
}
