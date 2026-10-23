using SwiftDrop.Delivery.Api.Domain;

namespace SwiftDrop.Delivery.Api.Application.Quotes;

public sealed record QuoteInputValidationResult(
    NormalizedQuoteInput? Input,
    IReadOnlyDictionary<string, string[]> Errors);

public static class QuoteInputValidator
{
    public static QuoteInputValidationResult Validate(QuoteInput? input)
    {
        var errors = new Dictionary<string, string[]>();
        if (input is null)
        {
            errors["body"] = ["A quote request is required."];
            return new(null, errors);
        }

        var pickup = ValidateAddress(input.PickupAddress, "pickupAddress", errors);
        var delivery = ValidateAddress(input.DeliveryAddress, "deliveryAddress", errors);
        if (input.PackageDetails is null)
        {
            errors["packageDetails"] = ["Parcel details are required."];
        }
        else
        {
            if (input.PackageDetails.WeightKg is null or <= 0 or > 30)
            {
                errors["packageDetails.weightKg"] = ["Weight must be greater than zero and at most 30 kg."];
            }

            if (input.PackageDetails.Size is not ("Small" or "Medium" or "Large"))
            {
                errors["packageDetails.size"] = ["Size must be Small, Medium or Large."];
            }
        }

        if (input.Recipient is null)
        {
            errors["recipient"] = ["Recipient details are required."];
        }
        else
        {
            RequiredText(input.Recipient.Name, "recipient.name", errors);
            RequiredText(input.Recipient.PhoneNumber, "recipient.phoneNumber", errors);
        }

        if (errors.Count > 0)
        {
            return new(null, errors);
        }

        var package = input.PackageDetails!;
        var recipient = input.Recipient!;
        return new(new NormalizedQuoteInput(
            pickup!, delivery!,
            new NormalizedPackageDetails(package.WeightKg!.Value, Enum.Parse<ParcelSize>(package.Size!)),
            new NormalizedRecipient(recipient.Name!.Trim(), recipient.PhoneNumber!.Trim())), errors);
    }

    private static NormalizedAddress? ValidateAddress(
        AddressInput? address, string field, Dictionary<string, string[]> errors)
    {
        if (address is null)
        {
            errors[field] = ["An address is required."];
            return null;
        }

        RequiredText(address.Street, field + ".street", errors);
        RequiredText(address.City, field + ".city", errors);
        RequiredText(address.PostalCode, field + ".postalCode", errors);
        if (!Guid.TryParse(address.ZoneId?.Trim(), out var zoneId) || zoneId == Guid.Empty)
        {
            errors[field + ".zoneId"] = ["A valid nonempty zone identifier is required."];
        }

        return new(address.Street?.Trim() ?? "", address.City?.Trim() ?? "",
            address.PostalCode?.Trim() ?? "", zoneId);
    }

    private static void RequiredText(string? value, string field, Dictionary<string, string[]> errors)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            errors[field] = ["This field is required."];
        }
        else if (value.Contains('\0'))
        {
            errors[field] = ["This field cannot contain a null character."];
        }
    }
}
