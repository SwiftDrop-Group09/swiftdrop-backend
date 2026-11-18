using Microsoft.EntityFrameworkCore;
using SwiftDrop.Delivery.Api.Application.CourierProof;
using SwiftDrop.Delivery.Api.Domain;
using SwiftDrop.Delivery.Api.Infrastructure.CourierProof;
using SwiftDrop.Delivery.Api.Infrastructure.Persistence;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Courier;

[Collection("Postgres")]
public sealed class PostgresProofReadTests(PostgresFixture fixture)
{
    [Fact]
    public async Task StagedProofCanBeVerifiedReadAndListedWithoutDisposingTheRequestContext()
    {
        await using var db = new DeliveryDbContext(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        await db.Database.MigrateAsync();
        var storage = new PostgresProofStorage(() => db);
        var key = $"proofs/{Guid.NewGuid():N}/{Guid.NewGuid():N}/photo.png";
        var bytes = ProofTestSupport.Image();
        var staged = await storage.StageAsync(key, new(ProofType.Photo, "image/png", bytes), default);
        await storage.VerifyAsync(staged, default);
        var content = await storage.ReadObjectAsync(key, default);
        Assert.NotNull(content);
        Assert.Equal(bytes, content.Value.Content);
        Assert.Equal("image/png", content.Value.ContentType);
        var found = false;
        await foreach (var item in storage.ListAsync(default)) if (item.StorageRef == key) found = true;
        Assert.True(found);
        await storage.DeleteAsync(key, default);
        Assert.Null(await storage.ReadObjectAsync(key, default));
        Assert.True(await db.Database.CanConnectAsync());
    }

    [Theory]
    [InlineData("https://public.invalid/image.png")]
    [InlineData("../another-object")]
    public async Task PrivateReaderRejectsNonServerKeysBeforeAccessingDatabase(string key)
    {
        var storage = new PostgresProofStorage(() => throw new InvalidOperationException("Must not access database"));
        await Assert.ThrowsAsync<ProofStorageException>(() => storage.ReadObjectAsync(key, default));
    }

    [Fact]
    public async Task PrivateReaderFailsClosedForCorruptContent()
    {
        await using var db = new DeliveryDbContext(new DbContextOptionsBuilder<DeliveryDbContext>().UseNpgsql(fixture.ConnectionString).Options);
        await db.Database.MigrateAsync();
        var storage = new PostgresProofStorage(() => db);
        var key = $"proofs/{Guid.NewGuid():N}/{Guid.NewGuid():N}/photo.png";
        await storage.StageAsync(key, new(ProofType.Photo, "image/png", ProofTestSupport.Image()), default);
        await db.Database.ExecuteSqlRawAsync("UPDATE proof_objects SET sha256 = {0} WHERE storage_ref = {1}", new string('0', 64), key);
        await Assert.ThrowsAsync<ProofStorageException>(() => storage.ReadObjectAsync(key, default));
    }
}
