-- Prints storage.sql: the project's storage buckets, the grants on storage to the app roles,
-- and the policies on storage, as they stand in the live database.
--
-- The schema dump leaves the storage schema out, so this is how those are kept. Run it
-- with the same login the dump uses (see backend/README.md):
--
--   psql -X -At -f backend/schema/storage-snapshot.sql > backend/schema/storage.sql

\set QUIET on
set role postgres;

select '-- Storage: the buckets the apps upload to, and who may read and write them.
--
-- The schema dump leaves Supabase''s storage schema out, since the platform owns its
-- tables. The buckets and the policies on them are this project''s own, though, and a
-- database rebuilt from schema.sql alone would have neither. This is them, read from the
-- live database by storage-snapshot.sql when schema.sql was last refreshed.
--
--   camera-snapshots   calibration frames from the counter phone
--   inspection-photos  photos a driver attaches to a failed checklist item
--
-- Run after roles.sql and schema.sql. Each statement can be run again safely.

begin;
';

select coalesce(string_agg(format(
  E'insert into storage.buckets (id, name, public, file_size_limit, allowed_mime_types)\nvalues (%L, %L, %s, %s, %s)\non conflict (id) do nothing;',
  id, name, public::text, coalesce(file_size_limit::text, 'null'),
  coalesce(quote_literal(allowed_mime_types::text) || '::text[]', 'null')), E'\n\n' order by id), '') || E'\n'
from storage.buckets;

select coalesce(string_agg(format('grant %s on %I.%I to %I;', privs, table_schema, table_name, grantee),
                           E'\n' order by grantee, table_name), '') || E'\n'
from (select grantee, table_schema, table_name,
             string_agg(lower(privilege_type), ', ' order by privilege_type) as privs
      from information_schema.role_table_grants
      where table_schema = 'storage' and grantee in ('app_driver', 'app_camera')
      group by grantee, table_schema, table_name) g;

select coalesce(string_agg(format(
  E'drop policy if exists %I on %I.%I;\ncreate policy %I on %I.%I as %s for %s to %s%s%s;',
  policyname, schemaname, tablename, policyname, schemaname, tablename, lower(permissive), lower(cmd),
  (select string_agg(quote_ident(r), ', ') from unnest(roles) as r),
  case when qual is not null then E'\n  using (' || qual || ')' else '' end,
  case when with_check is not null then E'\n  with check (' || with_check || ')' else '' end),
  E'\n\n' order by tablename, policyname), '') || E'\n'
from pg_policies
where schemaname = 'storage';

select 'commit;';
