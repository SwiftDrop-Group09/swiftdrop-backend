namespace SwiftDrop.Delivery.Api.Domain;

public static class ReassignmentReason
{
    public static string Normalize(string? reason)
    {
        var normalized = reason?.Trim();
        if (normalized is null || normalized.Length is < 1 or > 500)
            throw new ArgumentException("Supply a reassignment reason of 1 to 500 characters.", nameof(reason));
        return normalized;
    }
}
