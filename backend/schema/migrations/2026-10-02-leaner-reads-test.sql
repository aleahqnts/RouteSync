-- Checks 2026-10-02-leaner-reads.sql. Run it after the migration, in the Supabase SQL
-- editor. A failure raises; a pass prints one notice. Nothing is written.

do $test$
declare
  map  constant text := 'public.fleetmap_live(date, integer, timestamp with time zone, jsonb)';
  figs constant text := 'public.dashboard_figures(date, boolean)';
  v_map  json;
  v_all  json;
  v_live json;
  v_row  json;
begin
  if to_regprocedure('public.dashboard_figures(date)') is not null then
    raise exception 'the old one-argument dashboard_figures is still there';
  end if;

  if has_function_privilege('anon', map, 'execute') or has_function_privilege('authenticated', map, 'execute')
     or has_function_privilege('anon', figs, 'execute') or has_function_privilege('authenticated', figs, 'execute') then
    raise exception 'anon or authenticated can call one of the two';
  end if;
  if not has_function_privilege('service_role', map, 'execute')
     or not has_function_privilege('service_role', figs, 'execute') then
    raise exception 'service_role can no longer call one of the two';
  end if;

  -- The map's trips carry only the columns it draws from.
  v_map := public.fleetmap_live(current_date, null, now() - interval '1 day');
  v_row := v_map -> 'trips' -> 0;
  if v_row is not null and (v_row -> 'hand_edited' is not null or v_row -> 'trip_id' is null) then
    raise exception 'the map''s trip rows have the wrong columns: %', v_row;
  end if;
  v_row := v_map -> 'telemetry' -> 0;
  if v_row is not null and (v_row -> 'received_at' is not null or v_row -> 'latitude' is null) then
    raise exception 'the map''s readings have the wrong columns: %', v_row;
  end if;

  -- Called without the flag, as an older dashboard does: all of yesterday. With it, only
  -- the trips still running, which are a part of all of them.
  v_all := public.dashboard_figures(current_date);
  v_live := public.dashboard_figures(current_date, true);
  if json_array_length(v_live -> 'yesterday_trips') > json_array_length(v_all -> 'yesterday_trips') then
    raise exception 'the running trips outnumber all of yesterday';
  end if;
  if exists (select 1 from json_array_elements(v_live -> 'yesterday_trips') e
             where e ->> 'trip_status' <> 'Active') then
    raise exception 'a finished trip came back with p_yesterday_active_only';
  end if;

  raise notice 'pass: % of yesterday''s % trips still running; grants to service_role only',
    json_array_length(v_live -> 'yesterday_trips'), json_array_length(v_all -> 'yesterday_trips');
end
$test$;
