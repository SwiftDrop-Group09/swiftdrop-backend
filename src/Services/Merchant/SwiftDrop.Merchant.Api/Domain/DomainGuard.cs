using System.ComponentModel.DataAnnotations;

namespace SwiftDrop.Merchant.Api.Domain;

internal static class DomainGuard
{
    private static readonly EmailAddressAttribute EmailValidator = new();

    public static Guid Id(Guid value, string name) => value != Guid.Empty
        ? value : throw new ArgumentException("An identifier is required.", name);

    public static string Text(string value, string name, int maxLength = 500)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);
        if (value.Length > maxLength)
        {
            throw new ArgumentOutOfRangeException(name, $"'{name}' cannot exceed {maxLength} characters.");
        }
        return value.Trim();
    }

    public static string Email(string value, string name)
    {
        Text(value, name, 254);
        var trimmed = value.Trim();
        if (!EmailValidator.IsValid(trimmed) || !trimmed.Contains('.'))
        {
            throw new ArgumentException($"'{name}' must be a valid email address.", name);
        }
        return trimmed.ToLowerInvariant();
    }
}
