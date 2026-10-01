-- Checks 2026-10-01-drop-unused.sql. Run it after the migration, in the Supabase SQL
-- editor. A failure raises; a pass prints one notice.

do $test$
begin
  if to_regclass('public.routes_before_osm') is not null then
    raise exception 'routes_before_osm is still there';
  end if;
  if to_regclass('public.security_detector_state') is not null then
    raise exception 'security_detector_state is still there';
  end if;
  if to_regprocedure('public.whoami()') is not null then
    raise exception 'whoami() is still there';
  end if;
  raise notice 'pass: all three are gone';
end
$test$;
