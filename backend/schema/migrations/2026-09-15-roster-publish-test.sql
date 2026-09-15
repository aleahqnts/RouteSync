-- Exercises the hand-edit and skip triggers and publish_roster_month.
--
-- Everything happens inside one transaction that ends in a rollback, so no trip, skip,
-- gap, roster or week marker survives the run. It needs two drivers, two buses and a
-- route to exist and uses the lowest ids it finds, only ever inside the rollback. The
-- trips it writes are dated in 2099, clear of any real schedule.
--
-- Run it after the migration, in the Supabase SQL editor. A failure raises and aborts;
-- a pass prints one notice per case and a final line.

begin;

do $test$
declare
  v_month  date := date '2099-01-01';
  v_day    date := date '2099-01-06';   -- a Tuesday
  v_d1     integer;
  v_d2     integer;
  v_route  integer;
  v_bus1   text;
  v_bus2   text;
  v_result jsonb;
  v_trip1  text;
  v_trip2  text;
  v_n      integer;
  v_case   text;
  v_plan   jsonb;
begin
  select user_id into v_d1 from public.users order by user_id limit 1;
  select user_id into v_d2 from public.users where user_id > v_d1 order by user_id limit 1;
  select route_id into v_route from public.routes order by route_id limit 1;
  select vehicle_id into v_bus1 from public.vehicles order by vehicle_id limit 1;
  select vehicle_id into v_bus2 from public.vehicles where vehicle_id > v_bus1 order by vehicle_id limit 1;

  if v_d2 is null or v_route is null or v_bus2 is null then
    raise exception 'needs two users, a route and two vehicles to run';
  end if;

  perform public.save_roster_month(v_month, 0, '[]'::jsonb, null);

  -- A first publish writes the trips and marks the month published.
  v_case := 'a publish writes its trips as roster trips and publishes the month';
  v_plan := jsonb_build_object(
    'inserts', jsonb_build_array(
      jsonb_build_object('date', v_day, 'shift', 'Morning', 'route_id', v_route, 'vehicle_id', v_bus1,
                         'driver_id', v_d1, 'shift_start_time', '06:00', 'shift_end_time', '14:00', 'break_start', '09:00'),
      jsonb_build_object('date', v_day, 'shift', 'Morning', 'route_id', v_route, 'vehicle_id', v_bus2,
                         'driver_id', v_d2, 'shift_start_time', '06:00', 'shift_end_time', '14:00', 'break_start', '10:00')),
    'gaps', jsonb_build_array(
      jsonb_build_object('date', v_day, 'vehicle_id', v_bus1, 'shift', 'Evening', 'route_id', v_route,
                         'reason', 'No floater available')));
  v_result := public.publish_roster_month(v_month, 1, v_plan, null);

  select count(*) into v_n from public.trips where roster_month = v_month and not hand_edited;
  if (v_result ->> 'inserted')::int <> 2 or v_n <> 2
     or (select status from public.roster_months where month = v_month) <> 'Published'
     or (v_result ->> 'version')::int <> 2 then
    raise exception 'fail: % (%)', v_case, v_result;
  end if;
  if not exists (select 1 from public.schedule_weeks where week_start = date '2099-01-05') then
    raise exception 'fail: % (the week was not marked as saved)', v_case;
  end if;
  if (select count(*) from public.roster_gaps where month = v_month) <> 1 then
    raise exception 'fail: % (the gap was not stored)', v_case;
  end if;
  raise notice 'pass: %', v_case;

  select trip_id into v_trip1 from public.trips where roster_month = v_month and vehicle_id = v_bus1;
  select trip_id into v_trip2 from public.trips where roster_month = v_month and vehicle_id = v_bus2;

  -- A stale page cannot publish.
  v_case := 'a publish from a stale version is refused with RS409';
  begin
    perform public.publish_roster_month(v_month, 1, '{}'::jsonb, null);
    raise exception 'fail: %', v_case;
  exception when sqlstate 'RS409' then
    raise notice 'pass: %', v_case;
  end;

  -- Status, counts and times are not hand edits; the driver is.
  v_case := 'a status change is not a hand edit';
  update public.trips set trip_status = 'Pending' where trip_id = v_trip2;
  if (select hand_edited from public.trips where trip_id = v_trip2) then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'changing the break on a roster trip marks it hand edited';
  update public.trips set break_start = '11:00' where trip_id = v_trip1;
  if not (select hand_edited from public.trips where trip_id = v_trip1) then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  -- Deleting a roster trip remembers the slot.
  v_case := 'deleting a roster trip writes a skip for its slot';
  delete from public.trips where trip_id = v_trip2;
  if not exists (select 1 from public.roster_skips
                  where date = v_day and vehicle_id = v_bus2 and shift = 'Morning' and reason = 'Deleted') then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  -- A re-publish keeps the hand edit and does not refill the skip.
  v_case := 'a re-publish keeps a hand-edited trip and drops an insert into a skipped slot';
  v_plan := jsonb_build_object(
    'updates', jsonb_build_array(jsonb_build_object('trip_id', v_trip1, 'driver_id', v_d2, 'break_start', '09:00')),
    'inserts', jsonb_build_array(
      jsonb_build_object('date', v_day, 'shift', 'Morning', 'route_id', v_route, 'vehicle_id', v_bus2,
                         'driver_id', v_d1, 'shift_start_time', '06:00', 'shift_end_time', '14:00', 'break_start', '10:00')),
    'gaps', '[]'::jsonb);
  v_result := public.publish_roster_month(v_month, 2, v_plan, null);

  if (v_result ->> 'updated')::int <> 0 or (v_result ->> 'dropped')::int <> 1
     or jsonb_array_length(v_result -> 'kept') <> 1
     or (select driver_id from public.trips where trip_id = v_trip1) <> v_d1
     or exists (select 1 from public.trips where date = v_day and vehicle_id = v_bus2) then
    raise exception 'fail: % (%)', v_case, v_result;
  end if;
  if exists (select 1 from public.roster_gaps where month = v_month) then
    raise exception 'fail: % (old gaps were not replaced)', v_case;
  end if;
  raise notice 'pass: %', v_case;

  -- Nobody in two places.
  v_case := 'a publish that would book a driver twice on one shift is refused whole';
  delete from public.roster_skips where date = v_day;
  select count(*) into v_n from public.trips where roster_month = v_month;
  begin
    perform public.publish_roster_month(v_month, 3, jsonb_build_object(
      'inserts', jsonb_build_array(
        jsonb_build_object('date', v_day, 'shift', 'Morning', 'route_id', v_route, 'vehicle_id', v_bus2,
                           'driver_id', v_d1, 'shift_start_time', '06:00', 'shift_end_time', '14:00', 'break_start', '10:00'))),
      null);
    raise exception 'fail: %', v_case;
  exception when sqlstate 'RS422' then
    if (select count(*) from public.trips where roster_month = v_month) <> v_n then
      raise exception 'fail: % (a trip was written anyway)', v_case;
    end if;
    raise notice 'pass: %', v_case;
  end;

  -- Only the service key may publish.
  if has_function_privilege('anon', 'public.publish_roster_month(date, integer, jsonb, integer)', 'execute')
     or has_function_privilege('authenticated', 'public.publish_roster_month(date, integer, jsonb, integer)', 'execute')
     or has_function_privilege('app_driver', 'public.publish_roster_month(date, integer, jsonb, integer)', 'execute') then
    raise exception 'fail: publish_roster_month is executable by a client role';
  end if;
  raise notice 'pass: only the service key publishes';

  raise notice 'all roster publish cases passed';
end
$test$;

rollback;
