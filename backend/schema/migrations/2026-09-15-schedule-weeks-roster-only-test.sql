-- Exercises the roster_only week marker, alone and through publish_roster_month.
--
-- Everything happens inside one transaction that ends in a rollback, so no trip, roster or
-- week marker survives the run. Run it after 2026-09-15-roster-tables.sql,
-- 2026-09-15-roster-publish.sql and this migration, in the Supabase SQL editor. It needs a
-- user, a route and a vehicle to exist and uses the lowest ids it finds, only ever inside
-- the rollback. Weeks and trips are dated in 2099, clear of any real schedule.
--
-- A failure raises and aborts; a pass prints one notice per case and a final line.

begin;

do $test$
declare
  v_driver integer;
  v_route  integer;
  v_bus    text;
  v_case   text;
  v_month  date := date '2099-02-01';
  v_trip   text;
  v_plan   jsonb;
begin
  select user_id into v_driver from public.users order by user_id limit 1;
  select route_id into v_route from public.routes order by route_id limit 1;
  select vehicle_id into v_bus from public.vehicles order by vehicle_id limit 1;

  if v_driver is null or v_route is null or v_bus is null then
    raise exception 'needs a user, a route and a vehicle to run';
  end if;

  v_case := 'a week the planner saves is not roster_only';
  insert into public.schedule_weeks (week_start, saved_at, saved_by) values (date '2099-01-05', now(), null);
  if (select roster_only from public.schedule_weeks where week_start = date '2099-01-05') then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'a client cannot mark a week roster_only by sending the column';
  insert into public.schedule_weeks (week_start, saved_at, saved_by, roster_only)
  values (date '2099-01-12', now(), null, true);
  if (select roster_only from public.schedule_weeks where week_start = date '2099-01-12') then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  -- February 2099 begins on a Sunday, so its publish reaches back into the week of
  -- 26 January, which nobody planned, and into the week of 2 February and later ones.
  -- The week of 2 February is saved by the planner first.
  insert into public.schedule_weeks (week_start, saved_at, saved_by) values (date '2099-02-02', now(), null);

  perform public.save_roster_month(v_month, 0, '[]'::jsonb, null);
  perform public.publish_roster_month(v_month, 1, jsonb_build_object(
    'inserts', jsonb_build_array(
      jsonb_build_object('date', date '2099-02-01', 'shift', 'Morning', 'route_id', v_route, 'vehicle_id', v_bus,
                         'driver_id', v_driver, 'shift_start_time', '06:00', 'shift_end_time', '14:00', 'break_start', '09:00'),
      jsonb_build_object('date', date '2099-02-03', 'shift', 'Morning', 'route_id', v_route, 'vehicle_id', v_bus,
                         'driver_id', v_driver, 'shift_start_time', '06:00', 'shift_end_time', '14:00', 'break_start', '09:00'))),
    null);

  v_case := 'a week only a publish has written is roster_only';
  if not coalesce((select roster_only from public.schedule_weeks where week_start = date '2099-01-26'), false) then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'a publish leaves a week the planner saved as not roster_only, and still moves its version';
  if (select roster_only from public.schedule_weeks where week_start = date '2099-02-02')
     or (select saved_at from public.schedule_weeks where week_start = date '2099-02-02')
        = (select saved_at from public.schedule_weeks where week_start = date '2099-01-05') then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  -- A re-publish that changes the trip on 1 February writes the week of 26 January again.
  select trip_id into v_trip from public.trips where roster_month = v_month and date = date '2099-02-01';
  v_plan := jsonb_build_object('updates', jsonb_build_array(
    jsonb_build_object('trip_id', v_trip, 'driver_id', v_driver, 'break_start', '10:00')));

  v_case := 'a re-publish keeps a roster_only week roster_only';
  perform public.publish_roster_month(v_month, 2, v_plan, null);
  if (select break_start from public.trips where trip_id = v_trip) <> time '10:00' then
    raise exception 'fail: % (the re-publish did not change the trip)', v_case;
  end if;
  if not coalesce((select roster_only from public.schedule_weeks where week_start = date '2099-01-26'), false) then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'the planner saving a roster_only week clears it';
  update public.schedule_weeks set saved_at = now() + interval '1 minute', saved_by = v_driver
   where week_start = date '2099-01-26';
  if (select roster_only from public.schedule_weeks where week_start = date '2099-01-26') then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'a publish after the planner saved does not mark the week roster_only again';
  v_plan := jsonb_build_object('updates', jsonb_build_array(
    jsonb_build_object('trip_id', v_trip, 'driver_id', v_driver, 'break_start', '11:00')));
  perform public.publish_roster_month(v_month, 3, v_plan, null);
  if (select roster_only from public.schedule_weeks where week_start = date '2099-01-26') then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  raise notice 'all week marker cases passed';
end
$test$;

rollback;
