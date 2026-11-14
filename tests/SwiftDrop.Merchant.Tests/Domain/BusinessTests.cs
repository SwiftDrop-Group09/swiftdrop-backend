using SwiftDrop.Merchant.Api.Domain;

namespace SwiftDrop.Merchant.Tests.Domain;

public sealed class BusinessTests
{
    private readonly Guid _userId = Guid.NewGuid();
    private const string ValidName = "Acme Logistics Ltd";
    private const string ValidEmail = "ops@acme.com";
    private const string ValidPhone = "+94771234567";
    private const string ValidAddress = "123 Galle Road, Colombo 03";

    [Fact]
    public void Register_WithValidData_CreatesPendingBusiness()
    {
        var registeredAt = DateTimeOffset.UtcNow;
        var business = Business.Register(_userId, ValidName, ValidEmail, ValidPhone, ValidAddress, registeredAt);

        Assert.NotEqual(Guid.Empty, business.Id);
        Assert.Equal(_userId, business.UserId);
        Assert.Equal(ValidName, business.BusinessName);
        Assert.Equal(ValidEmail, business.ContactEmail);
        Assert.Equal(ValidPhone, business.ContactPhone);
        Assert.Equal(ValidAddress, business.PickupAddress);
        Assert.Equal(BusinessStatus.Pending, business.Status);
        Assert.Null(business.ApprovedAt);
        Assert.Null(business.ApprovedBy);
        Assert.Null(business.RejectionReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Register_WithBlankBusinessName_ThrowsArgumentException(string invalidName)
    {
        Assert.Throws<ArgumentException>(() =>
            Business.Register(_userId, invalidName, ValidEmail, ValidPhone, ValidAddress));
    }

    [Theory]
    [InlineData("invalid-email")]
    [InlineData("user@")]
    [InlineData("@domain.com")]
    public void Register_WithMalformedEmail_ThrowsArgumentException(string invalidEmail)
    {
        Assert.Throws<ArgumentException>(() =>
            Business.Register(_userId, ValidName, invalidEmail, ValidPhone, ValidAddress));
    }

    [Fact]
    public void Approve_WhenPending_SetsStatusToApproved()
    {
        var business = Business.Register(_userId, ValidName, ValidEmail, ValidPhone, ValidAddress);
        var adminId = Guid.NewGuid();

        business.Approve(adminId);

        Assert.Equal(BusinessStatus.Approved, business.Status);
        Assert.Equal(adminId, business.ApprovedBy);
        Assert.NotNull(business.ApprovedAt);
        Assert.Null(business.RejectionReason);
    }

    [Fact]
    public void Reject_WhenPending_SetsStatusToRejectedAndStoresReason()
    {
        var business = Business.Register(_userId, ValidName, ValidEmail, ValidPhone, ValidAddress);
        var adminId = Guid.NewGuid();
        const string reason = "Incomplete business tax verification documents.";

        business.Reject(adminId, reason);

        Assert.Equal(BusinessStatus.Rejected, business.Status);
        Assert.Equal(adminId, business.ApprovedBy);
        Assert.NotNull(business.ApprovedAt);
        Assert.Equal(reason, business.RejectionReason);
    }

    [Fact]
    public void Suspend_WhenApproved_SetsStatusToSuspendedAndStoresReason()
    {
        var business = Business.Register(_userId, ValidName, ValidEmail, ValidPhone, ValidAddress);
        var adminId = Guid.NewGuid();
        business.Approve(adminId);

        const string suspendReason = "Multiple fraudulent parcel claims.";
        business.Suspend(adminId, suspendReason);

        Assert.Equal(BusinessStatus.Suspended, business.Status);
        Assert.Equal(suspendReason, business.RejectionReason);
    }

    [Fact]
    public void Approve_WhenSuspended_ReActivatesToApproved()
    {
        var business = Business.Register(_userId, ValidName, ValidEmail, ValidPhone, ValidAddress);
        var adminId = Guid.NewGuid();
        business.Approve(adminId);
        business.Suspend(adminId, "Pending investigation.");

        business.Approve(adminId);

        Assert.Equal(BusinessStatus.Approved, business.Status);
        Assert.Null(business.RejectionReason);
    }

    [Fact]
    public void Approve_WhenAlreadyRejected_ThrowsInvalidOperationException()
    {
        var business = Business.Register(_userId, ValidName, ValidEmail, ValidPhone, ValidAddress);
        var adminId = Guid.NewGuid();
        business.Reject(adminId, "Invalid documents");

        Assert.Throws<InvalidOperationException>(() => business.Approve(adminId));
    }

    [Fact]
    public void Reject_WhenAlreadyApproved_ThrowsInvalidOperationException()
    {
        var business = Business.Register(_userId, ValidName, ValidEmail, ValidPhone, ValidAddress);
        var adminId = Guid.NewGuid();
        business.Approve(adminId);

        Assert.Throws<InvalidOperationException>(() => business.Reject(adminId, "Too late"));
    }
}
