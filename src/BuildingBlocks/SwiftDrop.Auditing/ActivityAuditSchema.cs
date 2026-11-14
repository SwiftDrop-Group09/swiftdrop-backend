namespace SwiftDrop.Auditing;

public static class ActivityAuditSchema
{
    public const string DownSql = """
        DO $$ BEGIN IF EXISTS (SELECT 1 FROM actor_activity_audit) THEN
            RAISE EXCEPTION 'Audit records cannot be discarded by rollback' USING ERRCODE='23514';
        END IF; END $$;
        DROP TABLE actor_activity_audit;
        DROP FUNCTION retain_actor_activity_audit();
        """;
    public const string Sql = """
        CREATE TABLE actor_activity_audit (
            event_id uuid PRIMARY KEY, operation_id uuid NOT NULL, occurred_at timestamptz NOT NULL,
            phase text NOT NULL CHECK (phase IN ('Started','Completed')), actor_id uuid,
            actor_roles text[] NOT NULL, actor_type text NOT NULL,
            action varchar(160) NOT NULL, method varchar(10) NOT NULL, route varchar(256) NOT NULL,
            resource_id uuid, ip_address inet, ip_source varchar(24) NOT NULL,
            status_code integer CHECK (status_code BETWEEN 100 AND 599), correlation_id uuid NOT NULL,
            duration_ms bigint, UNIQUE(operation_id, phase)
        );
        CREATE INDEX actor_activity_time_idx ON actor_activity_audit (occurred_at DESC, operation_id DESC);
        CREATE INDEX actor_activity_actor_idx ON actor_activity_audit (actor_id, occurred_at DESC);
        CREATE INDEX actor_activity_ip_idx ON actor_activity_audit (ip_address, occurred_at DESC);
        CREATE FUNCTION retain_actor_activity_audit() RETURNS trigger LANGUAGE plpgsql AS $$
        BEGIN RAISE EXCEPTION 'Activity audits are append-only' USING ERRCODE='23514'; END; $$;
        CREATE TRIGGER retain_actor_activity_audit BEFORE UPDATE OR DELETE ON actor_activity_audit
            FOR EACH ROW EXECUTE FUNCTION retain_actor_activity_audit();
        """;

    public static string GrantFor(string role)
    {
        if (role is not ("identity_app" or "delivery_app" or "merchant_app")) throw new ArgumentException(nameof(role));
        return $"DO $$ BEGIN IF EXISTS (SELECT 1 FROM pg_roles WHERE rolname='{role}') THEN GRANT SELECT, INSERT ON actor_activity_audit TO {role}; END IF; END $$;";
    }
}
