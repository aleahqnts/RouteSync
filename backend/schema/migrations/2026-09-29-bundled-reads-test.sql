-- Exercises nav_badge_inputs, dashboard_figures and fleetmap_live.
--
-- Reads only, inside a transaction that ends in a rollback. Each list is checked against a
-- count of the same rows read directly, so it passes on whatever data the project holds.
--
-- Run it after the migration, in the Supabase SQL editor. A failure raises and aborts; a pass
-- prints one notice per case and a final line.

begin;

do $test$
declare
  v_today   date := (now() at time zone 'Asia/Manila')::date;
  v_month   date := date_trunc('month', (now() at time zone 'Asia/Manila'))::date;
  v_nav     json;
  v_dash    json;
  v_map     json;
  v_case    text;
begin
  v_nav  := public.nav_badge_inputs(v_today, v_month, (v_month + interval '1 month')::date,
                                    array['Pending', 'AwaitingChange']);
  v_dash := public.dashboard_figures(v_today);
  v_map  := public.fleetmap_live(v_today, null, now() - interval '30 minutes');

  v_case := 'the badge lists hold the rows each separate read would';
  if json_array_length(v_nav->'trips') <> (select count(*) from public.trips where date between v_today and v_today + 1)
     or json_array_length(v_nav->'vehicles') <> (select count(*) from public.vehicles)
     or json_array_length(v_nav->'availability') <> (select count(*) from public.driver_availability)
     or json_array_length(v_nav->'open_logs') <> (select count(*) from public.maintenance_logs where resolved_at is null)
     or json_array_length(v_nav->'leave_open') <> (select count(*) from public.leave_requests where status in ('Pending', 'AwaitingChange'))
     or json_array_length(v_nav->'leave_today') <> (select count(*) from public.leave_requests
                                                    where status = 'Approved' and start_date <= v_today and end_date >= v_today) then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'the roster and incident sections are read';
  if v_nav->'roster' is null or json_typeof(v_nav->'roster') = 'null'
     or v_nav->'incidents' is null or json_typeof(v_nav->'incidents') = 'null' then
    raise exception 'fail: %', v_case;
  end if;
  if json_array_length(v_nav->'incidents') <> (select least(count(*), 1000) from public.security_incidents where needs_review is true) then
    raise exception 'fail: % (incident count)', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'the dashboard lists hold the rows each separate read would';
  if json_array_length(v_dash->'today_trips') <> (select count(*) from public.trips where date = v_today)
     or json_array_length(v_dash->'yesterday_trips') <> (select count(*) from public.trips where date = v_today - 1)
     or json_array_length(v_dash->'open_logs') <> (select count(*) from public.maintenance_logs where resolved_at is null)
     or json_array_length(v_dash->'routes') <> (select count(*) from public.routes) then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'the dashboard reads no more of a trip or a route than its cards use';
  if exists (select 1 from json_array_elements(v_dash->'routes') r where r::jsonb ? 'waypoints_json')
     or exists (select 1 from json_array_elements(v_dash->'today_trips') t where t::jsonb ? 'driver_id') then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'the map holds every active trip, the fare, and at most a thousand positions';
  if json_array_length(v_map->'trips') <> (select count(*) from public.trips where trip_status = 'Active')
     or json_array_length(v_map->'fare') <> (select count(*) from public.fare_config)
     or json_array_length(v_map->'telemetry') > 1000 then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'only the service key may call them';
  if has_function_privilege('anon', 'public.nav_badge_inputs(date, date, date, text[])', 'execute')
     or has_function_privilege('authenticated', 'public.dashboard_figures(date)', 'execute')
     or has_function_privilege('anon', 'public.fleetmap_live(date, integer, timestamptz)', 'execute')
     or not has_function_privilege('service_role', 'public.nav_badge_inputs(date, date, date, text[])', 'execute')
     or not has_function_privilege('service_role', 'public.dashboard_figures(date)', 'execute')
     or not has_function_privilege('service_role', 'public.fleetmap_live(date, integer, timestamptz)', 'execute') then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  raise notice 'all bundled read cases passed';
end
$test$;

rollback;
