namespace SwiftDrop.Notification.Worker.Routing;

public sealed class ServiceJwtFileReader(IConfiguration configuration)
{
    public async Task<string?> ReadAsync(string audience, CancellationToken cancellationToken)
    {
        var path = configuration[$"Notification:ServiceTokenFiles:{audience}"];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            return null;
        }

        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length is < 1 or > 8192)
            {
                return null;
            }

            var token = (await File.ReadAllTextAsync(path, cancellationToken)).Trim();
            return token.Length is > 0 and <= 8192 && !token.Any(char.IsWhiteSpace) ? token : null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
