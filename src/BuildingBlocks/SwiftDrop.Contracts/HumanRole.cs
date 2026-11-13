namespace SwiftDrop.Contracts;

public enum HumanRole { Customer, Courier, Merchant, Dispatcher, Admin }

public static class HumanRoles
{
    public static bool TryParse(string? value, out HumanRole role) =>
        Enum.TryParse(value, false, out role) && Enum.IsDefined(role) && role.ToString() == value;

    public static HumanRole Single(IReadOnlyList<string> roles) => roles is { Count: 1 } && TryParse(roles[0], out var role)
        ? role : throw new ArgumentException("Exactly one defined human role is required.");
}
