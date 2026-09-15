-- Exercises the roster tables and save_roster_month.
--
-- Everything happens inside one transaction that ends in a rollback, so no roster, no
-- slot and no version survives the run. It needs two drivers, two buses and a route to
-- exist, and uses the lowest ids it finds, only ever inside the rollback.
--
-- Run it after the migration, in the Supabase SQL editor. A failure raises and aborts;
-- a pass prints one notice per case and a final line.

begin;

do $test$
declare
  v_month   date := date '2099-01-01';
  v_d1      integer;
  v_d2      integer;
  v_route   integer;
  v_bus1    text;
  v_bus2    text;
  v_version integer;
  v_n       integer;
  v_case    text;
begin
  select user_id into v_d1 from public.users order by user_id limit 1;
  select user_id into v_d2 from public.users where user_id > v_d1 order by user_id limit 1;
  select route_id into v_route from public.routes order by route_id limit 1;
  select vehicle_id into v_bus1 from public.vehicles order by vehicle_id limit 1;
  select vehicle_id into v_bus2 from public.vehicles where vehicle_id > v_bus1 order by vehicle_id limit 1;

  if v_d2 is null or v_route is null or v_bus2 is null then
    raise exception 'needs two users, a route and two vehicles to run';
  end if;

  -- A month saved for the first time starts from version 0 and leaves at 1.
  v_case := 'a first save from version 0 creates the month at version 1';
  v_version := public.save_roster_month(v_month, 0, jsonb_build_array(
    jsonb_build_object('driver_id', v_d1, 'kind', 'Crew', 'route_id', v_route,
                       'vehicle_id', v_bus1, 'shift', 'Morning', 'rest_weekday', 2),
    jsonb_build_object('driver_id', v_d2, 'kind', 'Floater', 'route_id', v_route,
                       'vehicle_id', null, 'shift', 'Afternoon', 'rest_weekday', 5)), null);
  select count(*) into v_n from public.roster_slots where month = v_month;
  if v_version <> 1 or v_n <> 2 then
    raise exception 'fail: % (version %, % slots)', v_case, v_version, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- A save replaces the month whole.
  v_case := 'a second save replaces every slot and bumps the version';
  v_version := public.save_roster_month(v_month, 1, jsonb_build_array(
    jsonb_build_object('driver_id', v_d2, 'kind', 'Crew', 'route_id', v_route,
                       'vehicle_id', v_bus2, 'shift', 'Evening', 'rest_weekday', 7)), null);
  select count(*) into v_n from public.roster_slots where month = v_month;
  if v_version <> 2 or v_n <> 1 then
    raise exception 'fail: % (version %, % slots)', v_case, v_version, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- A page built on an older copy is refused, and nothing it sent is written.
  v_case := 'a save from a stale version is refused with RS409 and changes nothing';
  begin
    perform public.save_roster_month(v_month, 1, '[]'::jsonb, null);
    raise exception 'fail: %', v_case;
  exception when sqlstate 'RS409' then
    select count(*) into v_n from public.roster_slots where month = v_month;
    if v_n <> 1 then raise exception 'fail: % (% slots left)', v_case, v_n; end if;
    raise notice 'pass: %', v_case;
  end;

  -- A month is its first day.
  v_case := 'a month that is not the first of the month is refused';
  begin
    perform public.save_roster_month(date '2099-01-15', 0, '[]'::jsonb, null);
    raise exception 'fail: %', v_case;
  exception when sqlstate '22023' then
    raise notice 'pass: %', v_case;
  end;

  -- One driver on one bus shift, and one place per driver.
  v_case := 'two crew drivers on one bus shift are refused';
  begin
    perform public.save_roster_month(v_month, 2, jsonb_build_array(
      jsonb_build_object('driver_id', v_d1, 'kind', 'Crew', 'route_id', v_route,
                         'vehicle_id', v_bus1, 'shift', 'Morning', 'rest_weekday', 1),
      jsonb_build_object('driver_id', v_d2, 'kind', 'Crew', 'route_id', v_route,
                         'vehicle_id', v_bus1, 'shift', 'Morning', 'rest_weekday', 3)), null);
    raise exception 'fail: %', v_case;
  exception when unique_violation then
    raise notice 'pass: %', v_case;
  end;

  v_case := 'a floater with a bus is refused';
  begin
    perform public.save_roster_month(v_month, 2, jsonb_build_array(
      jsonb_build_object('driver_id', v_d1, 'kind', 'Floater', 'route_id', v_route,
                         'vehicle_id', v_bus1, 'shift', 'Morning', 'rest_weekday', 1)), null);
    raise exception 'fail: %', v_case;
  exception when check_violation then
    raise notice 'pass: %', v_case;
  end;

  v_case := 'a rest day outside Monday to Sunday is refused';
  begin
    perform public.save_roster_month(v_month, 2, jsonb_build_array(
      jsonb_build_object('driver_id', v_d1, 'kind', 'Floater', 'route_id', v_route,
                         'vehicle_id', null, 'shift', 'Morning', 'rest_weekday', 8)), null);
    raise exception 'fail: %', v_case;
  exception when check_violation then
    raise notice 'pass: %', v_case;
  end;

  -- A refused save leaves the month as the last good save left it.
  select version into v_version from public.roster_months where month = v_month;
  select count(*) into v_n from public.roster_slots where month = v_month;
  if v_version <> 2 or v_n <> 1 then
    raise exception 'fail: a refused save changed the month (version %, % slots)', v_version, v_n;
  end if;
  raise notice 'pass: a refused save leaves the month as it was';

  -- Only the service key may save.
  if has_function_privilege('anon', 'public.save_roster_month(date, integer, jsonb, integer)', 'execute')
     or has_function_privilege('authenticated', 'public.save_roster_month(date, integer, jsonb, integer)', 'execute')
     or has_function_privilege('app_driver', 'public.save_roster_month(date, integer, jsonb, integer)', 'execute') then
    raise exception 'fail: save_roster_month is executable by a client role';
  end if;
  if has_table_privilege('anon', 'public.roster_slots', 'select')
     or has_table_privilege('authenticated', 'public.roster_slots', 'select')
     or has_table_privilege('app_driver', 'public.roster_slots', 'insert') then
    raise exception 'fail: roster_slots is open to a client role';
  end if;
  raise notice 'pass: only the service key saves, and drivers only read';

  raise notice 'all roster table cases passed';
end
$test$;

rollback;
