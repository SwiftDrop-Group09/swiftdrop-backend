using Yarp.ReverseProxy.Transforms;

namespace SwiftDrop.Gateway;

public partial class Program
{
    public static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = GatewayControls.MaxRequestBodyBytes);
        builder.Services.AddHealthChecks();
        builder.Services.AddGatewayRateLimiting(builder.Configuration);
        builder.Services.AddReverseProxy()
            .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"))
            .AddTransforms(context =>
            {
                context.AddRequestHeaderRemove("Forwarded");
                context.AddRequestHeaderRemove("X-Original-For");
                context.AddRequestHeaderRemove("X-Original-Host");
                context.AddRequestHeaderRemove("X-Original-Proto");
                context.AddRequestHeaderRemove("X-Original-Prefix");
            });

        var app = builder.Build();
        app.UseGatewayForwarding();
        app.Use(GatewayControls.CorrelateAndBoundRequest);
        app.UseRouting();
        app.UseRateLimiter();

        // Process liveness only; dependency readiness is a later milestone.
        app.MapHealthChecks("/health");
        app.MapReverseProxy();

        app.Run();
    }
}
