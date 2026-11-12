using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace SwiftDrop.Auditing;

public sealed record ActivityRecord(Guid Id, string Timestamp, Guid? ActorId, string[] Roles, string ActorType,
    string Service, string Action, string Method, string Route, Guid? ResourceId, string? IpAddress,
    string IpSource, int? StatusCode, string Outcome, Guid? CorrelationId, long? DurationMs, string Kind);
public sealed record ActivityPage(IReadOnlyList<ActivityRecord> Items, long TotalCount, bool HasMore, bool RecordingEnabled);

public static class ActivityAudit
{
    public const string ActorItem = "ActivityAudit.ActorId";
    public const string RolesItem = "ActivityAudit.Roles";
    public static WebApplication UseActivityAudit(this WebApplication app, string service, string path)
    {
        var configuration = app.Configuration;
        var enabled = configuration.GetValue("Audit:Enabled", true);
        app.Use(async (context, next) =>
        {
            if (!enabled || (!context.Request.Path.StartsWithSegments("/api") && !context.Request.Path.StartsWithSegments("/internal"))
                || context.GetEndpoint() is null || context.Request.Method == "OPTIONS") { await next(context); return; }
            var operation = Guid.NewGuid();
            var occurred = DateTimeOffset.UtcNow;
            var stopwatch = Stopwatch.StartNew();
            var correlation = Guid.TryParse(context.Response.Headers["X-Correlation-ID"], out var supplied) ? supplied : Guid.NewGuid();
            var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("SwiftDrop.ActivityAudit");
            var started = false;
            try { await Append(context, configuration, operation, occurred, correlation, "Started", null, null); started = true; }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ArgumentException or TimeoutException or OperationCanceledException)
            {
                logger.LogCritical("Activity audit unavailable for {Service}; no payload or credentials logged. Failure: {Failure}", service, ex.GetType().Name);
                if (context.User.Identity?.IsAuthenticated == true || context.Request.Path.StartsWithSegments("/api/v1/auth"))
                { context.Response.StatusCode = 503; await context.Response.WriteAsJsonAsync(new { code = "audit_unavailable", title = "Activity recording is unavailable. Try again later.", status = 503 }); return; }
            }
            var failed = false;
            try { await next(context); }
            catch { failed = true; throw; }
            finally
            {
                if (started)
                    try { await Append(context, configuration, operation, occurred, correlation, "Completed", failed ? 500 : context.Response.StatusCode, stopwatch.ElapsedMilliseconds); }
                    catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or ArgumentException or TimeoutException or OperationCanceledException)
                    { logger.LogCritical("Activity completion could not be recorded for operation {Operation}; its Started record remains. Failure: {Failure}", operation, ex.GetType().Name); }
            }
        });
        app.MapGet(path, async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "private, no-store";
            try { return Results.Ok(await Read(configuration, service, context.Request.Query, context.RequestAborted)); }
            catch (ArgumentException) { return Results.BadRequest(new { code = "validation", title = "Invalid audit filters." }); }
            catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException or TimeoutException)
            { return Results.Problem(statusCode: 503, title: "Audit records are unavailable."); }
        }).RequireAuthorization(policy => policy.RequireRole("Admin")).WithName($"List{service}ActivityAudit");
        return app;
    }

    private static NpgsqlConnection Connection(IConfiguration configuration) => new(configuration.GetConnectionString("Database")
        ?? throw new InvalidOperationException("Database access is not configured."));

    private static async Task Append(HttpContext context, IConfiguration configuration, Guid operation, DateTimeOffset occurred,
        Guid correlation, string phase, int? status, long? duration)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await using var connection = Connection(configuration);
        await connection.OpenAsync(deadline.Token);
        var endpoint = context.GetEndpoint();
        var route = (endpoint as RouteEndpoint)?.RoutePattern.RawText ?? "/api";
        var action = endpoint?.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName ?? $"{context.Request.Method} {route}";
        var actor = context.Items.TryGetValue(ActorItem, out var item) && item is Guid attributed ? attributed
            : Guid.TryParse(context.User.FindFirst("sub")?.Value, out var subject) ? subject : (Guid?)null;
        var roles = (context.Items.TryGetValue(RolesItem, out var recordedRoles) && recordedRoles is IEnumerable<string> suppliedRoles
            ? suppliedRoles : context.User.FindAll("roles").Select(claim => claim.Value)).Where(value => value.Length <= 40).Distinct().Take(10).ToArray();
        var address = context.Connection.RemoteIpAddress;
        var ipSource = "Peer";
        var trusted = configuration.GetSection("Audit:TrustedProxies").Get<string[]>() ?? [];
        if (address is not null && trusted.Any(value => IPAddress.TryParse(value, out var proxy) && proxy.MapToIPv6().Equals(address.MapToIPv6())))
        {
            var forwarded = context.Request.Headers["X-Forwarded-For"].ToString().Split(',').LastOrDefault()?.Trim();
            if (IPAddress.TryParse(forwarded, out var client)) { address = client; ipSource = "TrustedProxy"; }
        }
        var resource = context.Request.RouteValues.Values.Select(value => Guid.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out var id) ? id : (Guid?)null).FirstOrDefault(id => id is not null);
        await using var command = new NpgsqlCommand("""
            INSERT INTO actor_activity_audit
            (event_id,operation_id,occurred_at,phase,actor_id,actor_roles,actor_type,action,method,route,resource_id,ip_address,ip_source,status_code,correlation_id,duration_ms)
            VALUES (@event,@operation,@time,@phase,@actor,@roles,@actorType,@action,@method,@route,@resource,@ip,@ipSource,@status,@correlation,@duration)
            """, connection);
        void Add(string name, NpgsqlDbType type, object? value) => command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);
        Add("event", NpgsqlDbType.Uuid, Guid.NewGuid()); Add("operation", NpgsqlDbType.Uuid, operation); Add("time", NpgsqlDbType.TimestampTz, occurred);
        Add("phase", NpgsqlDbType.Text, phase); Add("actor", NpgsqlDbType.Uuid, actor); Add("roles", NpgsqlDbType.Array | NpgsqlDbType.Text, roles);
        Add("actorType", NpgsqlDbType.Text, actor is null ? "Anonymous" : context.User.HasClaim(claim => claim.Type is "scope" or "scopes" or "client_id") ? "Service" : "Human");
        Add("action", NpgsqlDbType.Text, action[..Math.Min(action.Length, 160)]); Add("method", NpgsqlDbType.Text, context.Request.Method);
        Add("route", NpgsqlDbType.Text, route[..Math.Min(route.Length, 256)]); Add("resource", NpgsqlDbType.Uuid, resource);
        Add("ip", NpgsqlDbType.Inet, address?.IsIPv4MappedToIPv6 == true ? address.MapToIPv4() : address);
        Add("ipSource", NpgsqlDbType.Text, ipSource); Add("status", NpgsqlDbType.Integer, status); Add("correlation", NpgsqlDbType.Uuid, correlation); Add("duration", NpgsqlDbType.Bigint, duration);
        await command.ExecuteNonQueryAsync(deadline.Token);
    }

    public static async Task<ActivityPage> Read(IConfiguration configuration, string service, IQueryCollection query, CancellationToken ct)
    {
        string? Value(string name, int maximum = 160) { var value = query[name]; if (value.Count > 1 || value.ToString().Length > maximum) throw new ArgumentException(); return value.Count == 0 || string.IsNullOrWhiteSpace(value[0]) ? null : value[0]; }
        var allowed = new[] { "limit", "before", "beforeId", "from", "to", "actorId", "role", "ip", "action", "method", "outcome", "resourceId", "correlationId", "kind", "actorType" };
        if (query.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal))) throw new ArgumentException();
        var limit = Value("limit") is { } rawLimit && int.TryParse(rawLimit, out var parsedLimit) ? parsedLimit : 20;
        if (limit is < 1 or > 100 || (Value("limit") is not null && !int.TryParse(Value("limit"), out _))) throw new ArgumentException();
        var conditions = new List<string>(); var parameters = new List<NpgsqlParameter>();
        void Filter(string name, NpgsqlDbType type, object? value, string sql) { if (value is null) return; parameters.Add(new(name, type) { Value = value }); conditions.Add(sql); }
        DateTimeOffset? Date(string name) => Value(name) is { } raw ? DateTimeOffset.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date) ? date.ToUniversalTime() : throw new ArgumentException() : null;
        Guid? Id(string name) => Value(name) is { } raw ? Guid.TryParse(raw, out var id) ? id : throw new ArgumentException() : null;
        var from = Date("from"); var to = Date("to"); if (from > to) throw new ArgumentException();
        Filter("from", NpgsqlDbType.TimestampTz, from, "occurred_at >= @from"); Filter("to", NpgsqlDbType.TimestampTz, to, "occurred_at < @to");
        Filter("actor", NpgsqlDbType.Uuid, Id("actorId"), "actor_id = @actor"); Filter("resource", NpgsqlDbType.Uuid, Id("resourceId"), "resource_id = @resource");
        Filter("correlation", NpgsqlDbType.Uuid, Id("correlationId"), "correlation_id = @correlation");
        Filter("role", NpgsqlDbType.Text, Value("role", 40), "@role = ANY(actor_roles)");
        if (Value("ip", 64) is { } ip) { if (!IPAddress.TryParse(ip, out var address)) throw new ArgumentException(); Filter("ip", NpgsqlDbType.Inet, address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address, "ip_address = @ip"); }
        Filter("action", NpgsqlDbType.Text, Value("action"), "strpos(lower(action), lower(@action)) > 0");
        var method = Value("method", 10); if (method is not null && method is not ("GET" or "POST" or "PUT" or "PATCH" or "DELETE" or "HEAD" or "SYSTEM")) throw new ArgumentException();
        Filter("method", NpgsqlDbType.Text, method, "method = @method");
        var outcome = Value("outcome", 20); if (outcome is not null && outcome is not ("Success" or "Denied" or "Failed" or "InProgress")) throw new ArgumentException();
        Filter("outcome", NpgsqlDbType.Text, outcome, "outcome = @outcome");
        var kind = Value("kind", 20); if (kind is not null && kind is not ("Request" or "Security" or "Domain" or "Configuration" or "Background")) throw new ArgumentException(); Filter("kind", NpgsqlDbType.Text, kind, "kind = @kind");
        var actorType = Value("actorType", 20); if (actorType is not null && actorType is not ("Human" or "Service" or "Anonymous" or "System")) throw new ArgumentException(); Filter("actorType", NpgsqlDbType.Text, actorType, "actor_type = @actorType");
        var before = Date("before"); var beforeId = Id("beforeId"); if ((before is null) != (beforeId is null)) throw new ArgumentException();
        var legacy = service == "Identity" ? """
            UNION ALL SELECT md5('identity-security-' || id::text)::uuid, created_at, actor_user_id, ARRAY[]::text[],
                CASE WHEN actor_user_id IS NULL THEN 'Anonymous' ELSE 'Human' END, action, '', '', subject_user_id,
                NULL::inet, 'Unavailable', NULL::integer, CASE WHEN lower(result_code)='success' THEN 'Success' ELSE 'Failed' END,
                CASE WHEN correlation_id ~* '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$' THEN correlation_id::uuid ELSE NULL END,
                NULL::bigint, 'Security' FROM audit_logs
            """ : service == "Delivery" ? """
            UNION ALL SELECT md5('delivery-history-' || id::text)::uuid, timestamp, changed_by, ARRAY[]::text[], 'Human',
                'ParcelStatus:' || to_status, '', '', parcel_id, NULL::inet, 'Unavailable', NULL::integer, 'Success', NULL::uuid, NULL::bigint, 'Domain' FROM booking_history
            UNION ALL SELECT md5('delivery-config-' || id::text)::uuid, timestamp, actor_id, ARRAY[]::text[], 'Human',
                action, '', '', resource_id, NULL::inet, 'Unavailable', NULL::integer, 'Success', NULL::uuid, NULL::bigint, 'Configuration' FROM delivery_config_audits
            """ : service == "Merchant" ? """
            UNION ALL SELECT md5('merchant-attempt-' || id::text)::uuid, requested_at, NULL::uuid, ARRAY['System']::text[], 'System',
                CASE WHEN response_status_code BETWEEN 200 AND 299 THEN 'WebhookDelivered' ELSE 'WebhookAttemptFailed' END,
                'SYSTEM', '/background/webhooks/{jobId}', job_id, NULL::inet, 'NotApplicable', response_status_code,
                CASE WHEN response_status_code BETWEEN 200 AND 299 THEN 'Success' ELSE 'Failed' END,
                NULL::uuid, duration_ms::bigint, 'Background' FROM webhook_attempts
            UNION ALL SELECT md5('merchant-replay-' || id::text)::uuid, created_at, actor_id, ARRAY[]::text[], 'Human',
                'WebhookReplay', '', '', original_job_id, NULL::inet, 'Unavailable', NULL::integer, 'Success', NULL::uuid, NULL::bigint, 'Domain' FROM webhook_replay_audits
            """ : "";
        var prefix = """
            WITH latest AS (SELECT DISTINCT ON (operation_id) * FROM actor_activity_audit
                ORDER BY operation_id, CASE WHEN phase='Completed' THEN 1 ELSE 0 END DESC), all_records AS (
                SELECT operation_id AS id,occurred_at,actor_id,actor_roles,actor_type,action,method,route,resource_id,ip_address,ip_source,status_code,
                    CASE WHEN phase='Started' THEN 'InProgress' WHEN status_code < 400 THEN 'Success' WHEN status_code IN (401,403) THEN 'Denied' ELSE 'Failed' END AS outcome,
                    correlation_id,duration_ms,'Request' AS kind FROM latest
            """ + "\n" + legacy + "\n) SELECT * FROM all_records";
        var filters = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
        await using var connection = Connection(configuration); await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using var count = new NpgsqlCommand("SELECT count(*) FROM (" + prefix + filters + ") counted", connection, transaction);
        foreach (var parameter in parameters) count.Parameters.Add(new(parameter.ParameterName, parameter.NpgsqlDbType) { Value = parameter.Value });
        var total = (long)(await count.ExecuteScalarAsync(ct))!;
        if (before is not null) { conditions.Add("(occurred_at,id) < (@before,@beforeId)"); parameters.Add(new("before", NpgsqlDbType.TimestampTz) { Value = before }); parameters.Add(new("beforeId", NpgsqlDbType.Uuid) { Value = beforeId! }); }
        filters = conditions.Count == 0 ? "" : " WHERE " + string.Join(" AND ", conditions);
        await using var command = new NpgsqlCommand(prefix + filters + " ORDER BY occurred_at DESC,id DESC LIMIT @limit", connection, transaction);
        command.Parameters.AddRange(parameters.ToArray()); command.Parameters.AddWithValue("limit", limit + 1);
        var items = new List<ActivityRecord>();
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct)) items.Add(new(reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'", CultureInfo.InvariantCulture), reader.IsDBNull(2) ? null : reader.GetGuid(2), reader.GetFieldValue<string[]>(3), reader.GetString(4), service,
                reader.GetString(5), reader.GetString(6), reader.GetString(7), reader.IsDBNull(8) ? null : reader.GetGuid(8), reader.IsDBNull(9) ? null : reader.GetFieldValue<IPAddress>(9).ToString(), reader.GetString(10), reader.IsDBNull(11) ? null : reader.GetInt32(11), reader.GetString(12), reader.IsDBNull(13) ? null : reader.GetGuid(13), reader.IsDBNull(14) ? null : reader.GetInt64(14), reader.GetString(15)));
        await transaction.CommitAsync(ct); return new(items.Take(limit).ToArray(), total, items.Count > limit, configuration.GetValue("Audit:Enabled", true));
    }
}
