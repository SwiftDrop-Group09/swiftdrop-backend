ALTER TABLE public.audit_logs
    ADD COLUMN result_code VARCHAR(80) NOT NULL DEFAULT 'Success',
    ADD COLUMN correlation_id VARCHAR(128),
    ADD CONSTRAINT audit_logs_result_code_check CHECK (result_code ~ '^[A-Za-z][A-Za-z0-9_]{0,79}$'),
    ADD CONSTRAINT audit_logs_correlation_id_check
        CHECK (correlation_id IS NULL OR correlation_id ~ '^[A-Za-z0-9][A-Za-z0-9._:-]{0,127}$');

CREATE INDEX users_created_at_id_idx ON public.users (created_at, id);
CREATE INDEX audit_logs_created_at_id_idx ON public.audit_logs (created_at, id);

-- Role replacement is an Identity-owned, transactional administration operation.
-- The application still cannot alter schemas, seed arbitrary roles or change audits.
GRANT DELETE ON TABLE public.user_roles TO identity_app;
