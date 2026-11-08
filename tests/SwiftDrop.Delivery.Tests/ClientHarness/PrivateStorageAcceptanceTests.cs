namespace SwiftDrop.Delivery.Tests.ClientHarness;

public sealed class PrivateStorageAcceptanceTests
{
    [Fact]
    public async Task MissingConfigurationIsBlockedNotPassedAndDoesNotReadAmbientCredentials()
    {
        using var output = new StringWriter();
        var keys = new List<string>();
        var result = await PrivateStorageAcceptance.RunAsync(output, name => { keys.Add(name); return null; });
        Assert.Equal(3, result);
        Assert.Contains("\"status\":\"blocked\"", output.ToString());
        Assert.DoesNotContain("passed", output.ToString());
        Assert.All(keys, x => Assert.True(x.StartsWith("ProofStorage__", StringComparison.Ordinal)
            || x == "SWIFTDROP_COURIER_ACCEPTANCE_BUCKET"));
    }

    [Fact]
    public async Task AConfiguredBucketCannotBeWrittenWithoutMatchingDedicatedTestConfirmation()
    {
        using var output = new StringWriter();
        var result = await PrivateStorageAcceptance.RunAsync(output, name => name switch
        {
            "ProofStorage__Bucket" => "private-value-not-logged",
            "SWIFTDROP_COURIER_ACCEPTANCE_BUCKET" => "different-test-bucket",
            _ => "secret-value-not-logged"
        });
        Assert.Equal(3, result);
        Assert.Contains("blocked", output.ToString());
        Assert.DoesNotContain("private-value", output.ToString());
        Assert.DoesNotContain("secret-value", output.ToString());
    }

    [Fact]
    public async Task UnsafeEndpointFailsWithoutNetworkFallbackOrLeakingItsSecret()
    {
        using var output = new StringWriter();
        var result = await PrivateStorageAcceptance.RunAsync(output, name => name switch
        {
            "ProofStorage__Endpoint" => "https://user:do-not-log@127.0.0.1:1/private-path",
            "ProofStorage__Bucket" or "SWIFTDROP_COURIER_ACCEPTANCE_BUCKET" => "dedicated-test",
            "ProofStorage__Region" => "us-east-1",
            _ => "do-not-log"
        });
        Assert.Equal(1, result);
        Assert.Contains("\"status\":\"failed\"", output.ToString());
        Assert.DoesNotContain("do-not-log", output.ToString());
        Assert.DoesNotContain("127.0.0.1", output.ToString());
    }
}
