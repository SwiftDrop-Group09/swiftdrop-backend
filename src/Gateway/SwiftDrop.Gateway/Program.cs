namespace SwiftDrop.Gateway;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddHealthChecks();
        builder.Services.AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

        var app = builder.Build();

        // Process liveness only; dependency readiness is a later milestone.
        app.MapHealthChecks("/health");
        app.MapReverseProxy();

        app.Run();
    }
}
