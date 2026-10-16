namespace SwiftDrop.Identity.Api.Application;

public interface IUserRegistrationStore
{
    Task<bool> IsReadyAsync(CancellationToken cancellationToken);

    Task<RegisteredProfile?> RegisterAsync(
        string email,
        string normalizedEmail,
        string fullName,
        string passwordHash,
        CancellationToken cancellationToken);
}
