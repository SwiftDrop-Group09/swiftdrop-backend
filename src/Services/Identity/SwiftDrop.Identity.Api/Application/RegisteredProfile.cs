using System.Text.Json.Serialization;
using SwiftDrop.Contracts;

namespace SwiftDrop.Identity.Api.Application;

public sealed record RegisteredProfile
{
    [JsonConstructor]
    public RegisteredProfile(Guid id, string email, string fullName, string? phoneNumber,
        string? address, IReadOnlyList<string> roles, bool isActive)
    {
        Id = id; Email = email; FullName = fullName; PhoneNumber = phoneNumber;
        Address = address; Role = HumanRoles.Single(roles); IsActive = isActive;
    }

    public Guid Id { get; init; }
    public string Email { get; init; }
    public string FullName { get; init; }
    public string? PhoneNumber { get; init; }
    public string? Address { get; init; }
    private HumanRole role;
    [JsonIgnore]
    public HumanRole Role
    {
        get => role;
        init => role = Enum.IsDefined(value) ? value : throw new ArgumentException("Undefined human role.");
    }
    // Singleton wire adapter; the actual domain value is the enum above.
    public IReadOnlyList<string> Roles
    {
        get => new[] { Role.ToString() };
        init => Role = HumanRoles.Single(value);
    }
    public bool IsActive { get; init; }
}
