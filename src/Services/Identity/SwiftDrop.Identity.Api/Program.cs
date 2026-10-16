namespace SwiftDrop.Identity.Api;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHealthChecks();

        var app = builder.Build();

        // Process liveness only; dependency readiness is a later milestone.
        app.MapHealthChecks("/health");

        app.Run();
    }
}
