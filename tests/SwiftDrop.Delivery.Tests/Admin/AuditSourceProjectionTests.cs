using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Npgsql;
using SwiftDrop.Auditing;
using SwiftDrop.Delivery.Tests.Persistence;

namespace SwiftDrop.Delivery.Tests.Admin;

[Collection("Postgres")]
public sealed class AuditSourceProjectionTests(PostgresFixture fixture)
{
    [Theory]
    [InlineData("Identity", 1)]
    [InlineData("Delivery", 2)]
    [InlineData("Merchant", 2)]
    public async Task ProjectsRetainedServiceRecordsWithoutInventingIpOrCurrentRoles(string service, int expected)
    {
        var schema = "audit_projection_" + Guid.NewGuid().ToString("N");
        await using var owner = new NpgsqlConnection(fixture.ConnectionString); await owner.OpenAsync();
        await using var create = new NpgsqlCommand($"CREATE SCHEMA {schema}", owner); await create.ExecuteNonQueryAsync();
        try
        {
            var settings = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { SearchPath = schema };
            await using var connection = new NpgsqlConnection(settings.ConnectionString); await connection.OpenAsync();
            var actor = Guid.NewGuid(); var resource = Guid.NewGuid();
            var legacy = service switch
            {
                "Identity" => """
                    CREATE TABLE audit_logs(id bigint,created_at timestamptz,actor_user_id uuid,action text,subject_user_id uuid,result_code text,correlation_id text);
                    INSERT INTO audit_logs VALUES(1,now(),@actor,'RolesChanged',@resource,'Success',NULL);
                    """,
                "Delivery" => """
                    CREATE TABLE booking_history(id uuid,timestamp timestamptz,changed_by uuid,to_status text,parcel_id uuid);
                    CREATE TABLE delivery_config_audits(id uuid,timestamp timestamptz,actor_id uuid,action text,resource_id uuid);
                    INSERT INTO booking_history VALUES(gen_random_uuid(),now(),@actor,'Created',@resource);
                    INSERT INTO delivery_config_audits VALUES(gen_random_uuid(),now(),@actor,'ZoneUpdated',@resource);
                    """,
                _ => """
                    CREATE TABLE webhook_attempts(id uuid,requested_at timestamptz,response_status_code integer,job_id uuid,duration_ms integer);
                    CREATE TABLE webhook_replay_audits(id uuid,created_at timestamptz,actor_id uuid,original_job_id uuid);
                    INSERT INTO webhook_attempts VALUES(gen_random_uuid(),now(),204,@resource,20);
                    INSERT INTO webhook_replay_audits VALUES(gen_random_uuid(),now(),@actor,@resource);
                    """
            };
            await using var seed = new NpgsqlCommand(ActivityAuditSchema.Sql + legacy, connection);
            seed.Parameters.AddWithValue("actor", actor); seed.Parameters.AddWithValue("resource", resource);
            await seed.ExecuteNonQueryAsync();
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Database"] = settings.ConnectionString }).Build();
            var page = await ActivityAudit.Read(configuration, service, new QueryCollection(), default);
            Assert.Equal(expected, page.Items.Count); Assert.Equal(expected, page.TotalCount);
            Assert.All(page.Items, item => { Assert.Null(item.IpAddress); Assert.Equal(service, item.Service); Assert.Equal(resource, item.ResourceId); });
            if (service == "Merchant")
            {
                var system = Assert.Single(page.Items, item => item.ActorType == "System");
                Assert.Equal("Background", system.Kind); Assert.Equal("WebhookDelivered", system.Action); Assert.Equal("NotApplicable", system.IpSource);
            }
            else Assert.All(page.Items, item => Assert.Empty(item.Roles));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP SCHEMA {schema} CASCADE", owner); await drop.ExecuteNonQueryAsync();
        }
    }
}
