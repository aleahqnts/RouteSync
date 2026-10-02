-- Checks 2026-10-02-fleetmap-live-grants.sql. Run it after the migration, in the Supabase
-- SQL editor. A failure raises; a pass prints one notice.

do $test$
declare
  f constant text := 'public.fleetmap_live(date, integer, timestamp with time zone, jsonb)';
begin
  if has_function_privilege('anon', f, 'execute') then
    raise exception 'anon can still call fleetmap_live';
  end if;
  if has_function_privilege('authenticated', f, 'execute') then
    raise exception 'authenticated can still call fleetmap_live';
  end if;
  if not has_function_privilege('service_role', f, 'execute') then
    raise exception 'service_role can no longer call fleetmap_live';
  end if;
  raise notice 'pass: only service_role can call fleetmap_live';
end
$test$;
