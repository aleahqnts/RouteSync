-- Checks 2026-10-30-device-app-version.sql. Run it after the migration, in the Supabase
-- SQL editor. A failure raises; a pass prints one notice.

do $test$
begin
  if not exists (
    select 1 from information_schema.columns
    where table_schema = 'public' and table_name = 'device_status'
      and column_name = 'app_version' and data_type = 'text'
  ) then
    raise exception 'device_status.app_version is missing or not text';
  end if;
  if not has_column_privilege('app_camera', 'public.device_status', 'app_version', 'UPDATE') then
    raise exception 'app_camera cannot update device_status.app_version';
  end if;
  raise notice 'device-app-version: pass';
end
$test$;
