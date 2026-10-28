using System.Net;
using System.Security.Claims;
using System.Text;
using SwiftDrop.Delivery.Tests.Authentication;
using SwiftDrop.Delivery.Tests.Quotes;

namespace SwiftDrop.Delivery.Tests.Courier;

public sealed class CourierProofSecurityTests
{
    [Theory]
    [InlineData("Development")]
    [InlineData("Production")]
    public async Task ForgedHeadersAndUnsignedBearerCannotUploadInDeployedHosts(string environment)
    {
        using var factory = new QuoteApiFactory(null, replaceAuthentication: false, environmentName: environment);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("Authorization", "Bearer unverified-proof");
        client.DefaultRequestHeaders.Add("X-User-Id", Guid.NewGuid().ToString());
        client.DefaultRequestHeaders.Add("X-User-Role", "Courier");
        using var response = await ProofTestSupport.Send(client, Guid.NewGuid(), new StringContent("malformed"));
        await DeliveryReadAuthenticationTests.AssertProblemAsync(response, HttpStatusCode.Unauthorized, "unauthorized");
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.True(response.Headers.CacheControl?.Private);
    }

    [Theory]
    [InlineData("Customer")]
    [InlineData("Merchant")]
    [InlineData("Dispatcher")]
    [InlineData("Admin")]
    public async Task WrongRoleIsRejectedBeforeMultipartOrDependencyResolution(string role)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), role));
        using var client = factory.CreateClient();
        using var response = await ProofTestSupport.Send(client, Guid.NewGuid(), new StringContent("not multipart"));
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("invalid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public async Task InvalidTrustedSubjectDoesNotBecomeACourier(string? subject)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(subject, "Courier"));
        using var client = factory.CreateClient();
        using var response = await ProofTestSupport.Send(client, Guid.NewGuid(), new StringContent("not multipart"));
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("missing-proof")]
    [InlineData("missing-name")]
    [InlineData("long-name")]
    [InlineData("bad-version")]
    [InlineData("zero-version")]
    [InlineData("duplicate")]
    [InlineData("courierId")]
    [InlineData("storageRef")]
    [InlineData("proofId")]
    [InlineData("unsupported-mime")]
    [InlineData("oversized")]
    [InlineData("empty")]
    [InlineData("missing-key")]
    public async Task InvalidMultipartIsRejectedWithoutDatabaseOrStorage(string invalid)
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), "Courier"));
        using var client = factory.CreateClient();
        var form = invalid switch
        {
            "missing-proof" => ProofTestSupport.Form(photo: false),
            "missing-name" => ProofTestSupport.Form(name: " "),
            "long-name" => ProofTestSupport.Form(name: new string('n', 121)),
            "zero-version" => ProofTestSupport.Form(version: 0),
            "oversized" => ProofTestSupport.Form(photoBytes: new byte[5 * 1024 * 1024 + 1]),
            "empty" => ProofTestSupport.Form(photoBytes: []),
            "bad-version" => new MultipartFormDataContent(),
            _ => ProofTestSupport.Form()
        };
        if (invalid == "bad-version") form.Add(new StringContent("5.5"), "expectedVersion");
        if (invalid == "duplicate") form.Add(new StringContent("Other"), "recipientName");
        if (invalid is "courierId" or "storageRef" or "proofId") form.Add(new StringContent("forged"), invalid);
        if (invalid == "unsupported-mime") ProofTestSupport.AddFile(form, "signature", Encoding.UTF8.GetBytes("not image"), "application/octet-stream");
        using var response = await ProofTestSupport.Send(client, Guid.NewGuid(), form, invalid == "missing-key" ? null : "proof-key");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore);
    }

    [Fact]
    public async Task TruncatedMultipartIsAValidationErrorBeforeAnyDependencyAccess()
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), "Courier"));
        using var client = factory.CreateClient();
        var body = new ByteArrayContent(Encoding.UTF8.GetBytes(
            "--boundary\r\nContent-Disposition: form-data; name=\"recipientName\"\r\n\r\nRecipient without closing boundary"));
        body.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("multipart/form-data; boundary=boundary");
        using var response = await ProofTestSupport.Send(client, Guid.NewGuid(), body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.DoesNotContain("Stream", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ValidMultipartStillFailsClosedWhenTheDatabaseIsUnconfigured()
    {
        using var factory = new QuoteApiFactory(null, DeliveryTestAuthentication.Principal(Guid.NewGuid().ToString(), "Courier"));
        using var client = factory.CreateClient();
        using var response = await ProofTestSupport.Send(client, Guid.NewGuid(), ProofTestSupport.Form());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.DoesNotContain("StorageRef", await response.Content.ReadAsStringAsync());
    }
}
