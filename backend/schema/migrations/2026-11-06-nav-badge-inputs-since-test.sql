-- Checks 2026-11-06-nav-badge-inputs-since.sql. Run it after the migration, in the
-- Supabase SQL editor. A failure raises; a pass prints one notice. It only reads.

do $test$
declare
  f constant text := 'public.nav_badge_inputs_since(date, date, date, text[], text, text)';
  today date := ((now() at time zone 'Asia/Manila') - interval '6 hours')::date;
  this_month date := date_trunc('month', today)::date;
  next_month date := (date_trunc('month', today) + interval '1 month')::date;
  statuses text[] := array['Pending', 'AwaitingChange'];
  first json;
  again json;
  stale json;
begin
  if has_function_privilege('anon', f, 'execute') then
    raise exception 'anon can call nav_badge_inputs_since';
  end if;
  if has_function_privilege('authenticated', f, 'execute') then
    raise exception 'authenticated can call nav_badge_inputs_since';
  end if;
  if not has_function_privilege('service_role', f, 'execute') then
    raise exception 'service_role cannot call nav_badge_inputs_since';
  end if;

  first := public.nav_badge_inputs_since(today, this_month, next_month, statuses, 'fast', null);
  if first->'inputs' is null or json_typeof(first->'inputs') <> 'object' then
    raise exception 'a first asking did not return the rows';
  end if;
  if (first->'inputs')::text <> public.nav_badge_inputs(today, this_month, next_month, statuses, 'fast')::text then
    raise exception 'the rows differ from nav_badge_inputs';
  end if;

  again := public.nav_badge_inputs_since(today, this_month, next_month, statuses, 'fast', first->>'hash');
  if json_typeof(again->'inputs') <> 'null' then
    raise exception 'an asking with the current fingerprint was sent the rows again';
  end if;
  if again->>'hash' <> first->>'hash' then
    raise exception 'the fingerprint changed with nothing written';
  end if;

  stale := public.nav_badge_inputs_since(today, this_month, next_month, statuses, 'fast', 'not-a-fingerprint');
  if json_typeof(stale->'inputs') <> 'object' then
    raise exception 'an asking with an old fingerprint was not sent the rows';
  end if;

  raise notice 'nav_badge_inputs_since: all checks passed';
end
$test$;
