using System.Security.Claims;

using Microsoft.AspNetCore.Http;

using SwiftDrop.Delivery.Api.Application;
using SwiftDrop.Delivery.Api.Infrastructure.Authentication;

namespace SwiftDrop.Delivery.Tests.Authentication;

public sealed class ClaimsCurrentCustomerTests
{
    private const string VerifiedScheme = "VerifiedDeliveryTestIdentity";

    [Fact]
    public void MissingHttpContextHasNoAuthenticatedCustomer()
    {
        var customer = new ClaimsCurrentCustomer(new HttpContextAccessor());

        AssertIdentity(customer, false, false, null);
    }

    [Fact]
    public void EmptyPrincipalHasNoAuthenticatedCustomer()
    {
        var customer = Current(new ClaimsPrincipal());

        AssertIdentity(customer, false, false, null);
    }

    [Fact]
    public void UnauthenticatedIdentityCannotUseItsSubjectOrCustomerRole()
    {
        var identity = new ClaimsIdentity([
            new("sub", DeliveryTestAuthentication.CustomerId.ToString("D")),
            new(ClaimTypes.Role, "Customer"), new("roles", "Customer")]);

        AssertIdentity(Current(new ClaimsPrincipal(identity)), false, false, null);
    }

    [Theory]
    [InlineData(ClaimTypes.Role, ClaimTypes.Role)]
    [InlineData("roles", ClaimTypes.Role)]
    [InlineData("delivery-role", "delivery-role")]
    public void CustomerRoleComesFromTheSelectedIdentity(string claimType, string roleClaimType)
    {
        var identity = Identity(DeliveryTestAuthentication.CustomerId.ToString("D"),
            [new(claimType, "Customer")], roleClaimType);

        AssertIdentity(Current(new ClaimsPrincipal(identity)), true, true, DeliveryTestAuthentication.CustomerId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Admin")]
    [InlineData("Courier")]
    [InlineData("Merchant")]
    [InlineData("customer")]
    [InlineData("CUSTOMER")]
    [InlineData(" Customer ")]
    [InlineData("Customer,Courier")]
    [InlineData("[\"Customer\"]")]
    public void OtherRoleValuesCannotAcquireCustomerPrivileges(string? role)
    {
        var roles = role is null ? Array.Empty<Claim>() : [new Claim("roles", role)];
        var identity = Identity(DeliveryTestAuthentication.NonCustomerId.ToString("D"), roles);

        AssertIdentity(Current(new ClaimsPrincipal(identity)), true, false, DeliveryTestAuthentication.NonCustomerId);
    }

    [Fact]
    public void MultipleRoleClaimsCanIncludeTheExactCustomerRole()
    {
        var principal = DeliveryTestAuthentication.Principal(
            DeliveryTestAuthentication.CustomerId.ToString("D"), "Courier", "Customer");

        AssertIdentity(Current(principal), true, true, DeliveryTestAuthentication.CustomerId);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-user-identifier")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void MissingInvalidOrEmptySubjectDoesNotProduceAnOwner(string? subject)
    {
        var identity = Identity(subject, [new(ClaimTypes.Role, "Customer")]);

        AssertIdentity(Current(new ClaimsPrincipal(identity)), true, true, null);
    }

    [Theory]
    [InlineData("abcdefab-1234-4234-8234-abcdefabcdef")]
    [InlineData("ABCDEFAB-1234-4234-8234-ABCDEFABCDEF")]
    [InlineData("{abcdefab-1234-4234-8234-abcdefabcdef}")]
    [InlineData(" abcdefab-1234-4234-8234-abcdefabcdef ")]
    public void ExistingGuidSubjectFormsRemainAccepted(string subject)
    {
        var identity = Identity(subject, [new(ClaimTypes.Role, "Customer")]);

        AssertIdentity(Current(new ClaimsPrincipal(identity)), true, true,
            Guid.Parse("abcdefab-1234-4234-8234-abcdefabcdef"));
    }

    [Theory]
    [InlineData("11111111-1111-4111-8111-111111111111")]
    [InlineData("22222222-2222-4222-8222-222222222222")]
    [InlineData("")]
    public void DuplicateSubjectClaimsNeverChooseAnOwner(string secondSubject)
    {
        var identity = Identity(DeliveryTestAuthentication.CustomerId.ToString("D"),
            [new(ClaimTypes.Role, "Customer"), new("sub", secondSubject)]);

        AssertIdentity(Current(new ClaimsPrincipal(identity)), true, true, null);
    }

    [Fact]
    public void NameIdentifierDoesNotSubstituteForTheRequiredSubject()
    {
        var identity = Identity(null, [new(ClaimTypes.Role, "Customer"),
            new(ClaimTypes.NameIdentifier, DeliveryTestAuthentication.CustomerId.ToString("D"))]);

        AssertIdentity(Current(new ClaimsPrincipal(identity)), true, true, null);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnauthenticatedSecondaryCannotReplaceOwnerOrElevateNonCustomer(bool secondaryFirst)
    {
        var verified = Identity(DeliveryTestAuthentication.NonCustomerId.ToString("D"),
            [new(ClaimTypes.Role, "Courier")]);
        var unverified = new ClaimsIdentity([
            new("sub", DeliveryTestAuthentication.CustomerId.ToString("D")),
            new(ClaimTypes.Role, "Customer"), new("roles", "Customer")]);
        var principal = new ClaimsPrincipal(secondaryFirst ? [unverified, verified] : [verified, unverified]);

        AssertIdentity(Current(principal), true, false, DeliveryTestAuthentication.NonCustomerId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnauthenticatedSecondaryDoesNotChangeAValidCustomer(bool secondaryFirst)
    {
        var verified = Identity(DeliveryTestAuthentication.CustomerId.ToString("D"),
            [new(ClaimTypes.Role, "Customer")]);
        var unverified = new ClaimsIdentity([
            new("sub", DeliveryTestAuthentication.OtherCustomerId.ToString("D")),
            new(ClaimTypes.Role, "Admin")]);
        var principal = new ClaimsPrincipal(secondaryFirst ? [unverified, verified] : [verified, unverified]);

        AssertIdentity(Current(principal), true, true, DeliveryTestAuthentication.CustomerId);
    }

    [Fact]
    public void UnauthenticatedSecondaryCannotFillAMissingVerifiedSubject()
    {
        var verified = Identity(null, [new(ClaimTypes.Role, "Customer")]);
        var unverified = new ClaimsIdentity([new("sub", DeliveryTestAuthentication.CustomerId.ToString("D"))]);

        AssertIdentity(Current(new ClaimsPrincipal([verified, unverified])), true, true, null);
    }

    [Theory]
    [InlineData("11111111-1111-4111-8111-111111111111")]
    [InlineData("22222222-2222-4222-8222-222222222222")]
    public void MultipleAuthenticatedIdentitiesFailClosedEvenWhenSubjectsMatch(string secondSubject)
    {
        var first = Identity(DeliveryTestAuthentication.CustomerId.ToString("D"),
            [new(ClaimTypes.Role, "Customer")]);
        var second = Identity(secondSubject, [new(ClaimTypes.Role, "Customer")]);

        AssertIdentity(Current(new ClaimsPrincipal([first, second])), false, false, null);
    }

    [Fact]
    public void RequestHeadersCannotEstablishAnAuthenticatedIdentity()
    {
        var context = new DefaultHttpContext();
        AddForgedHeaders(context);
        var customer = new ClaimsCurrentCustomer(new HttpContextAccessor { HttpContext = context });

        AssertIdentity(customer, false, false, null);
    }

    [Fact]
    public void RequestHeadersCannotElevateOrReplaceATrustedNonCustomer()
    {
        var context = new DefaultHttpContext
        {
            User = new ClaimsPrincipal(Identity(DeliveryTestAuthentication.NonCustomerId.ToString("D"),
                [new(ClaimTypes.Role, "Courier")]))
        };
        AddForgedHeaders(context);
        var customer = new ClaimsCurrentCustomer(new HttpContextAccessor { HttpContext = context });

        AssertIdentity(customer, true, false, DeliveryTestAuthentication.NonCustomerId);
    }

    private static ClaimsCurrentCustomer Current(ClaimsPrincipal principal) =>
        new(new HttpContextAccessor { HttpContext = new DefaultHttpContext { User = principal } });

    private static ClaimsIdentity Identity(string? subject, IEnumerable<Claim> additionalClaims,
        string roleClaimType = ClaimTypes.Role)
    {
        var claims = additionalClaims.ToList();
        if (subject is not null) claims.Add(new("sub", subject));
        return new(claims, VerifiedScheme, ClaimTypes.Name, roleClaimType);
    }

    private static void AddForgedHeaders(HttpContext context)
    {
        context.Request.Headers["X-User-Id"] = DeliveryTestAuthentication.CustomerId.ToString("D");
        context.Request.Headers["X-User-Role"] = "Customer";
        context.Request.Headers.Authorization = "Bearer fictional-unverified-token";
    }

    private static void AssertIdentity(ICurrentCustomer customer, bool authenticated, bool isCustomer, Guid? owner)
    {
        Assert.Equal(authenticated, customer.IsAuthenticated);
        Assert.Equal(isCustomer, customer.IsCustomer);
        Assert.Equal(owner, customer.CustomerId);
    }
}
