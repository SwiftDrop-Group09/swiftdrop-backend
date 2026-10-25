ALTER TABLE public.users
    ADD COLUMN updated_at TIMESTAMPTZ,
    ADD COLUMN failed_login_count INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN lockout_count INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN locked_until TIMESTAMPTZ,
    ADD CONSTRAINT users_failed_login_count_check CHECK (failed_login_count >= 0 AND failed_login_count < 5),
    ADD CONSTRAINT users_lockout_count_check CHECK (lockout_count >= 0);

UPDATE public.users SET updated_at = created_at WHERE updated_at IS NULL;
ALTER TABLE public.users
    ALTER COLUMN updated_at SET NOT NULL,
    ALTER COLUMN updated_at SET DEFAULT NOW();

CREATE TABLE public.refresh_families (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES public.users (id) ON DELETE CASCADE,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ,
    CONSTRAINT refresh_families_owner_key UNIQUE (id, user_id),
    CONSTRAINT refresh_families_expiry_check CHECK (expires_at > created_at)
);

-- Preserve existing sessions. Inconsistent legacy family ownership fails the
-- migration transaction rather than assigning a family to another account.
INSERT INTO public.refresh_families (id, user_id, created_at, expires_at, revoked_at)
SELECT family_id, user_id, MIN(created_at), MAX(expires_at),
       CASE WHEN BOOL_AND(revoked_at IS NOT NULL) THEN MAX(revoked_at) END
FROM public.refresh_sessions
GROUP BY family_id, user_id;

ALTER TABLE public.refresh_sessions
    ADD COLUMN consumed_at TIMESTAMPTZ,
    ADD COLUMN replaced_by_session_id UUID,
    ADD CONSTRAINT refresh_sessions_family_owner_fk
        FOREIGN KEY (family_id, user_id) REFERENCES public.refresh_families (id, user_id),
    ADD CONSTRAINT refresh_sessions_id_family_owner_key UNIQUE (id, family_id, user_id),
    ADD CONSTRAINT refresh_sessions_replacement_key UNIQUE (replaced_by_session_id),
    ADD CONSTRAINT refresh_sessions_expiry_check CHECK (expires_at > created_at),
    ADD CONSTRAINT refresh_sessions_consumption_check
        CHECK ((consumed_at IS NULL) = (replaced_by_session_id IS NULL)),
    ADD CONSTRAINT refresh_sessions_replacement_not_self_check
        CHECK (replaced_by_session_id IS NULL OR replaced_by_session_id <> id),
    ADD CONSTRAINT refresh_sessions_replacement_owner_fk
        FOREIGN KEY (replaced_by_session_id, family_id, user_id)
        REFERENCES public.refresh_sessions (id, family_id, user_id);

CREATE INDEX refresh_families_user_id_idx ON public.refresh_families (user_id);
CREATE INDEX refresh_sessions_family_owner_idx ON public.refresh_sessions (family_id, user_id);
CREATE INDEX external_identities_user_id_idx ON public.external_identities (user_id);

GRANT SELECT, INSERT, UPDATE ON TABLE public.refresh_families TO identity_app;
