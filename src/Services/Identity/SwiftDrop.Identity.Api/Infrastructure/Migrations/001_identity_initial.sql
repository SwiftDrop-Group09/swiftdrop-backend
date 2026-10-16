CREATE TABLE public.roles (
    name VARCHAR(32) PRIMARY KEY
);

INSERT INTO public.roles (name)
VALUES ('Customer'), ('Courier'), ('Dispatcher'), ('Merchant'), ('Admin')
ON CONFLICT (name) DO NOTHING;

CREATE TABLE public.users (
    id UUID PRIMARY KEY,
    email VARCHAR(254) NOT NULL,
    normalized_email VARCHAR(254) NOT NULL,
    full_name VARCHAR(120) NOT NULL,
    phone_number VARCHAR(32),
    address TEXT,
    password_hash TEXT,
    is_active BOOLEAN NOT NULL DEFAULT TRUE,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE UNIQUE INDEX users_normalized_email_key ON public.users (normalized_email);

CREATE TABLE public.user_roles (
    user_id UUID NOT NULL REFERENCES public.users (id) ON DELETE CASCADE,
    role_name VARCHAR(32) NOT NULL REFERENCES public.roles (name),
    assigned_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    PRIMARY KEY (user_id, role_name)
);

CREATE TABLE public.external_identities (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES public.users (id) ON DELETE CASCADE,
    provider VARCHAR(32) NOT NULL,
    subject VARCHAR(255) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    UNIQUE (provider, subject)
);

CREATE TABLE public.refresh_sessions (
    id UUID PRIMARY KEY,
    user_id UUID NOT NULL REFERENCES public.users (id) ON DELETE CASCADE,
    family_id UUID NOT NULL,
    token_hash TEXT NOT NULL UNIQUE,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE public.audit_logs (
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    actor_user_id UUID REFERENCES public.users (id),
    action VARCHAR(80) NOT NULL,
    subject_user_id UUID REFERENCES public.users (id),
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

GRANT SELECT ON TABLE public.roles, public.schema_migrations TO identity_app;
GRANT SELECT, INSERT, UPDATE ON TABLE public.users TO identity_app;
GRANT SELECT, INSERT ON TABLE public.user_roles, public.audit_logs TO identity_app;
GRANT SELECT, INSERT, UPDATE, DELETE
    ON TABLE public.external_identities, public.refresh_sessions TO identity_app;
GRANT USAGE, SELECT ON SEQUENCE public.audit_logs_id_seq TO identity_app;
