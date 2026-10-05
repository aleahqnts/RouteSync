-- Checks 2026-10-05-storage-schema-usage.sql. Run it after the migration, in the Supabase
-- SQL editor. A failure raises; a pass prints one notice.

do $test$
declare
  r text;
begin
  foreach r in array array['app_driver', 'app_camera'] loop
    if not has_schema_privilege(r, 'storage', 'usage') then
      raise exception '% cannot use the storage schema', r;
    end if;
    if not has_function_privilege(r, 'storage.foldername(text)', 'execute') then
      raise exception '% cannot call storage.foldername', r;
    end if;
  end loop;
  raise notice 'pass: app_driver and app_camera can use the storage schema';
end
$test$;
