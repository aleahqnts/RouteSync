-- Checks 2026-11-07-nav-badge-alert-keys.sql. Run it after the migration, in the
-- Supabase SQL editor. A failure raises; a pass prints one notice. It only reads.

do $test$
declare
  f constant text := 'public.nav_badge_inputs(date, date, date, text[], text)';
  today date := ((now() at time zone 'Asia/Manila') - interval '6 hours')::date;
  this_month date := date_trunc('month', today)::date;
  next_month date := (date_trunc('month', today) + interval '1 month')::date;
  statuses text[] := array['Pending', 'AwaitingChange'];
  fast json;
  slow json;
  expected int;
begin
  if has_function_privilege('anon', f, 'execute') then
    raise exception 'anon can call nav_badge_inputs';
  end if;
  if has_function_privilege('authenticated', f, 'execute') then
    raise exception 'authenticated can call nav_badge_inputs';
  end if;
  if not has_function_privilege('service_role', f, 'execute') then
    raise exception 'service_role cannot call nav_badge_inputs';
  end if;

  fast := public.nav_badge_inputs(today, this_month, next_month, statuses, 'fast');
  if json_typeof(fast->'defects') <> 'array' then
    raise exception 'the fast section has no defects list';
  end if;

  select count(*) into expected
  from bus_checklist c join trips t on t.trip_id = c.trip_id
  where t.date = today and c.checklist_status = 'Passed with Defects';
  if json_array_length(fast->'defects') <> expected then
    raise exception 'defects lists % rows, expected %', json_array_length(fast->'defects'), expected;
  end if;

  if json_typeof(fast->'incidents') = 'array' and json_array_length(fast->'incidents') > 0
     and (fast->'incidents'->0->>'incident_id') is null then
    raise exception 'incidents are listed without incident_id';
  end if;

  slow := public.nav_badge_inputs(today, this_month, next_month, statuses, 'slow');
  if json_typeof(slow->'defects') <> 'null' then
    raise exception 'the slow section carries defects';
  end if;

  raise notice 'nav_badge_inputs alert keys: all checks passed';
end
$test$;
