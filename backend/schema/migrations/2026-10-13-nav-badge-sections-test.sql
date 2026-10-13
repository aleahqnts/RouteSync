-- Checks 2026-10-13-nav-badge-sections.sql. Run it after the migration, in the Supabase SQL
-- editor. A failure raises; a pass prints one notice. It reads only.

do $test$
declare
  f constant text := 'public.nav_badge_inputs(date, date, date, text[], text)';
  d date := current_date;
  m date := date_trunc('month', current_date)::date;
  st text[] := array['Pending'];
  a json;
  fast json;
  slow json;
  k text;
begin
  if to_regprocedure('public.nav_badge_inputs(date, date, date, text[])') is not null then
    raise exception 'the four-argument nav_badge_inputs is still there';
  end if;
  if has_function_privilege('anon', f, 'execute') then
    raise exception 'anon can call nav_badge_inputs';
  end if;
  if has_function_privilege('authenticated', f, 'execute') then
    raise exception 'authenticated can call nav_badge_inputs';
  end if;
  if not has_function_privilege('service_role', f, 'execute') then
    raise exception 'service_role cannot call nav_badge_inputs';
  end if;

  a    := nav_badge_inputs(d, m, (m + interval '1 month')::date, st);
  fast := nav_badge_inputs(d, m, (m + interval '1 month')::date, st, 'fast');
  slow := nav_badge_inputs(d, m, (m + interval '1 month')::date, st, 'slow');

  foreach k in array array['trips', 'vehicles', 'availability', 'open_logs', 'incidents'] loop
    if (fast -> k)::text is distinct from (a -> k)::text then raise exception 'fast % differs from all', k; end if;
    if json_typeof(slow -> k) <> 'null' then raise exception 'slow carries %', k; end if;
  end loop;
  foreach k in array array['leave_open', 'leave_asked', 'leave_today', 'roster'] loop
    if (slow -> k)::text is distinct from (a -> k)::text then raise exception 'slow % differs from all', k; end if;
    if json_typeof(fast -> k) <> 'null' then raise exception 'fast carries %', k; end if;
  end loop;

  raise notice 'pass: all % bytes, fast % bytes, slow % bytes',
    length(a::text), length(fast::text), length(slow::text);
end
$test$;
