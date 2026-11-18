-- Forward-only consolidation. Original assignments remain in an owner-only archive.
LOCK TABLE public.users, public.user_roles, public.roles IN ACCESS EXCLUSIVE MODE;
CREATE TYPE public.user_role AS ENUM ('Customer','Courier','Merchant','Dispatcher','Admin');
CREATE TABLE public.user_role_migration_archive AS
SELECT u.id AS user_id, ur.role_name::text AS previous_role, ur.assigned_at,
       CURRENT_TIMESTAMP AS archived_at
FROM public.users u LEFT JOIN public.user_roles ur ON ur.user_id=u.id;
REVOKE ALL ON public.user_role_migration_archive FROM PUBLIC, identity_app;

-- Stable precedence selected for this migration: Admin, Dispatcher, Merchant,
-- Courier, Customer. Keep original timestamps for the retained assignment.
DELETE FROM public.user_roles ur USING public.user_roles preferred
WHERE ur.user_id=preferred.user_id
  AND array_position(ARRAY['Admin','Dispatcher','Merchant','Courier','Customer'],ur.role_name::text)
    > array_position(ARRAY['Admin','Dispatcher','Merchant','Courier','Customer'],preferred.role_name::text);
INSERT INTO public.user_roles (user_id,role_name)
SELECT u.id,'Customer' FROM public.users u
WHERE NOT EXISTS (SELECT 1 FROM public.user_roles r WHERE r.user_id=u.id);

ALTER TABLE public.user_roles DROP CONSTRAINT user_roles_role_name_fkey;
ALTER TABLE public.roles ALTER COLUMN name TYPE public.user_role USING name::text::public.user_role;
ALTER TABLE public.user_roles ALTER COLUMN role_name TYPE public.user_role USING role_name::text::public.user_role;
ALTER TABLE public.user_roles ADD CONSTRAINT user_roles_role_name_fkey FOREIGN KEY (role_name) REFERENCES public.roles(name);
ALTER TABLE public.user_roles ADD CONSTRAINT user_roles_one_role_per_user UNIQUE (user_id);
GRANT USAGE ON TYPE public.user_role TO identity_app;

CREATE FUNCTION public.require_single_user_role() RETURNS trigger
LANGUAGE plpgsql SET search_path=pg_catalog,public AS $$
DECLARE affected uuid[]; candidate uuid;
BEGIN
    IF TG_TABLE_NAME='users' THEN affected:=ARRAY[NEW.id];
    ELSIF TG_OP='UPDATE' THEN affected:=ARRAY[OLD.user_id,NEW.user_id];
    ELSIF TG_OP='DELETE' THEN affected:=ARRAY[OLD.user_id];
    ELSE affected:=ARRAY[NEW.user_id]; END IF;
    FOREACH candidate IN ARRAY affected LOOP
        IF EXISTS (SELECT 1 FROM public.users WHERE id=candidate)
           AND (SELECT count(*) FROM public.user_roles WHERE user_id=candidate) <> 1 THEN
            RAISE EXCEPTION 'A user must have exactly one role' USING ERRCODE='23514';
        END IF;
    END LOOP;
    RETURN NULL;
END $$;
CREATE CONSTRAINT TRIGGER users_require_role AFTER INSERT ON public.users
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.require_single_user_role();
CREATE CONSTRAINT TRIGGER user_roles_require_one AFTER INSERT OR UPDATE OR DELETE ON public.user_roles
    DEFERRABLE INITIALLY DEFERRED FOR EACH ROW EXECUTE FUNCTION public.require_single_user_role();
