using SwiftDrop.Merchant.Api.Domain.Orders;

namespace SwiftDrop.Merchant.Api.Application.Orders;

public sealed record ValidationResult<T>(T? Value, IReadOnlyDictionary<string, string[]> Errors)
    where T : class
{
    public bool IsValid => Value is not null && Errors.Count == 0;
}

/// <summary>
/// Field validation for merchant orders. Field names in errors use the JSON
/// paths a merchant sent (e.g. <c>pickupAddress.zoneId</c>). Rules mirror
/// Delivery's QuoteInput so an order valid here is not rejected for shape later.
/// </summary>
public static class OrderInputValidator
{
    public const int MaxWeightKg = 30;
    public const int DefaultPageSize = 20;
    public const int MaxPageSize = 100;

    private const int StreetMaxLength = 200;
    private const int CityMaxLength = 100;
    private const int PostalCodeMaxLength = 20;
    private const int RecipientNameMaxLength = 120;
    private const int PhoneNumberMaxLength = 32;

    private static readonly string[] Sizes = ["Small", "Medium", "Large"];

    public static ValidationResult<MerchantOrderPayload> ValidateOrder(MerchantOrderInput? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null)
        {
            errors["body"] = ["An order is required."];
            return new(null, errors);
        }

        var externalOrderId = Identifier(input.ExternalOrderId, "externalOrderId", InboxJobLimits.ExternalOrderIdMaxLength, errors);
        var pickup = Address(input.PickupAddress, "pickupAddress", errors);
        var delivery = Address(input.DeliveryAddress, "deliveryAddress", errors);
        var package = Package(input.PackageDetails, errors);
        var recipient = Recipient(input.Recipient, errors);
        var priceVersion = Text(input.AcceptedPriceVersion, "acceptedPriceVersion", InboxJobLimits.PricingVersionMaxLength, errors);

        if (errors.Count > 0)
        {
            return new(null, errors);
        }

        return new(new MerchantOrderPayload(externalOrderId!, pickup!, delivery!, package!, recipient!, priceVersion!), errors);
    }

    /// <summary>Idempotency-Key: required, opaque, at most 128 visible ASCII characters.</summary>
    public static IReadOnlyDictionary<string, string[]> ValidateIdempotencyKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return Error("Idempotency-Key", "The Idempotency-Key header is required.");
        }

        return key.Length <= InboxJobLimits.IdempotencyKeyMaxLength && key.All(character => character is > ' ' and <= '~')
            ? new Dictionary<string, string[]>()
            : Error("Idempotency-Key", $"Use at most {InboxJobLimits.IdempotencyKeyMaxLength} visible ASCII characters.");
    }

    public static ValidationResult<OrderPage> ValidatePage(OrderListQuery query)
    {
        var errors = new Dictionary<string, string[]>();

        var page = 1;
        if (query.Page is not null && (!int.TryParse(query.Page, out page) || page < 1))
        {
            errors["page"] = ["Page must be a whole number of at least 1."];
        }

        var pageSize = DefaultPageSize;
        if (query.PageSize is not null && (!int.TryParse(query.PageSize, out pageSize) || pageSize is < 1 or > MaxPageSize))
        {
            errors["pageSize"] = [$"Page size must be between 1 and {MaxPageSize}."];
        }

        InboxJobState? state = null;
        if (query.State is not null)
        {
            if (Enum.TryParse<InboxJobState>(query.State, ignoreCase: false, out var parsed) && Enum.IsDefined(parsed)
                && !int.TryParse(query.State, out _))
            {
                state = parsed;
            }
            else
            {
                errors["state"] = [$"State must be one of: {string.Join(", ", Enum.GetNames<InboxJobState>())}."];
            }
        }

        return errors.Count > 0 ? new(null, errors) : new(new OrderPage(page, pageSize, state), errors);
    }

    private static OrderAddress? Address(OrderAddressInput? address, string field, Dictionary<string, string[]> errors)
    {
        if (address is null)
        {
            errors[field] = ["An address is required."];
            return null;
        }

        var street = Text(address.Street, $"{field}.street", StreetMaxLength, errors);
        var city = Text(address.City, $"{field}.city", CityMaxLength, errors);
        var postalCode = Text(address.PostalCode, $"{field}.postalCode", PostalCodeMaxLength, errors);
        if (!Guid.TryParse(address.ZoneId?.Trim(), out var zoneId) || zoneId == Guid.Empty)
        {
            errors[$"{field}.zoneId"] = ["A valid nonempty zone identifier is required."];
        }

        return street is null || city is null || postalCode is null || zoneId == Guid.Empty
            ? null
            : new OrderAddress(street, city, postalCode, zoneId);
    }

    private static OrderPackage? Package(OrderPackageInput? package, Dictionary<string, string[]> errors)
    {
        if (package is null)
        {
            errors["packageDetails"] = ["Parcel details are required."];
            return null;
        }

        var weightValid = package.WeightKg is > 0 and <= MaxWeightKg;
        if (!weightValid)
        {
            errors["packageDetails.weightKg"] = [$"Weight must be greater than zero and at most {MaxWeightKg} kg."];
        }

        var sizeValid = package.Size is not null && Sizes.Contains(package.Size, StringComparer.Ordinal);
        if (!sizeValid)
        {
            errors["packageDetails.size"] = ["Size must be Small, Medium or Large."];
        }

        return weightValid && sizeValid ? new OrderPackage(package.WeightKg!.Value, package.Size!) : null;
    }

    private static OrderRecipient? Recipient(OrderRecipientInput? recipient, Dictionary<string, string[]> errors)
    {
        if (recipient is null)
        {
            errors["recipient"] = ["Recipient details are required."];
            return null;
        }

        var name = Text(recipient.Name, "recipient.name", RecipientNameMaxLength, errors);
        var phone = Text(recipient.PhoneNumber, "recipient.phoneNumber", PhoneNumberMaxLength, errors);
        return name is null || phone is null ? null : new OrderRecipient(name, phone);
    }

    /// <summary>Free text: required, trimmed, bounded, no control characters.</summary>
    private static string? Text(string? value, string field, int maxLength, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = ["This field is required."];
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > maxLength || trimmed.Any(char.IsControl))
        {
            errors[field] = [$"Use at most {maxLength} characters without control characters."];
            return null;
        }

        return trimmed;
    }

    /// <summary>Identifiers are compared exactly, so surrounding whitespace is an error, not trimmed.</summary>
    private static string? Identifier(string? value, string field, int maxLength, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = ["This field is required."];
            return null;
        }

        if (value.Length > maxLength || value.Trim().Length != value.Length || value.Any(char.IsControl))
        {
            errors[field] = [$"Use at most {maxLength} characters, without surrounding whitespace or control characters."];
            return null;
        }

        return value;
    }

    private static Dictionary<string, string[]> Error(string field, string message) => new() { [field] = [message] };
}
