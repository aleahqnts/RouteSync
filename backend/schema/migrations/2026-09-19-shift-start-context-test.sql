-- Exercises shift_start_context.
--
-- Everything happens inside one transaction that ends in a rollback, so no trip and no
-- inspection survives the run. It needs a driver, a route and two buses with no Active
-- trip on them, and uses the lowest ids it finds, only ever inside the rollback.
--
-- Run it after the migration, in the Supabase SQL editor. A failure raises and aborts;
-- a pass prints one notice per case and a final line.
--
-- The caller check is not exercised here. It reads a user id from a driver JWT, which the
-- SQL editor does not carry, and under the service key the function answers for any trip
-- by design. Check that one from the driver app.

begin;

do $test$
declare
  v_driver  integer;
  v_route   integer;
  v_bus     character varying(20);
  v_now_ph  timestamp := now() at time zone 'Asia/Manila';
  v_today   date;
  v_ctx     record;
  v_case    text;
begin
  select user_id into v_driver from public.users order by user_id limit 1;
  select route_id into v_route from public.routes order by route_id limit 1;

  -- A bus with live work on it would decide these answers instead of the fixtures.
  select v.vehicle_id into v_bus
    from public.vehicles v
   where not exists (
     select 1 from public.trips t
      where t.vehicle_id = v.vehicle_id and t.trip_status = 'Active')
   order by v.vehicle_id
   limit 1;

  if v_driver is null or v_route is null or v_bus is null then
    raise exception 'needs a user, a route and a bus with no active trip to run';
  end if;

  v_today := v_now_ph::date;

  -- The shift about to start, and an earlier one on the same bus that is still open and
  -- was due to end an hour ago.
  insert into public.trips
    (trip_id, date, shift_type, shift_start_time, shift_end_time, route_id, vehicle_id,
     driver_id, trip_status)
  values
    ('TEST-CTX-NEXT', v_today, 'Afternoon',
     (v_now_ph + interval '15 minutes')::time, (v_now_ph + interval '8 hours')::time,
     v_route, v_bus, v_driver, 'Not Yet Started');

  -- No open trip on a free bus, and no inspection yet today.
  v_case := 'a free bus reports no open trip';
  select * into v_ctx from public.shift_start_context('TEST-CTX-NEXT');
  if v_ctx.open_trip_id is not null then
    raise exception 'fail: % (found %)', v_case, v_ctx.open_trip_id;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'a bus with no inspection today reports none';
  if v_ctx.bus_inspected_today then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  -- The earlier shift, left open past the time it was due to end.
  insert into public.trips
    (trip_id, date, shift_type, shift_start_time, shift_end_time, route_id, vehicle_id,
     driver_id, trip_status, actual_start_time)
  values
    ('TEST-CTX-OPEN', v_today, 'Morning',
     (v_now_ph - interval '9 hours')::time, (v_now_ph - interval '1 hour')::time,
     v_route, v_bus, v_driver, 'Active',
     (v_now_ph - interval '9 hours') at time zone 'UTC');

  v_case := 'the open trip on the same bus is reported with when it was due to end';
  select * into v_ctx from public.shift_start_context('TEST-CTX-NEXT');
  if v_ctx.open_trip_id is distinct from 'TEST-CTX-OPEN' then
    raise exception 'fail: % (found %)', v_case, coalesce(v_ctx.open_trip_id, 'nothing');
  end if;
  if v_ctx.open_trip_ends_at is null
     or abs(extract(epoch from (v_ctx.open_trip_ends_at - (v_now_ph - interval '1 hour')))) > 120 then
    raise exception 'fail: % (ends_at % against a wall clock of %)',
      v_case, v_ctx.open_trip_ends_at, v_now_ph;
  end if;
  raise notice 'pass: %', v_case;

  -- A cleared inspection on that bus today, filed against the open trip, is a fact about
  -- the bus and counts for the driver who did not do it.
  v_case := 'an inspection on this bus today is reported, whoever ran the trip';
  insert into public.bus_checklist
    (trip_id, vehicle_id, driver_id, submitted_at, exterior_inspection, engine_compartment,
     interior_inspection, brake_safety, passenger_systems, checklist_status)
  values
    ('TEST-CTX-OPEN', v_bus, v_driver, (v_now_ph - interval '8 hours') at time zone 'UTC',
     '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, '{}'::jsonb, 'Passed');
  select * into v_ctx from public.shift_start_context('TEST-CTX-NEXT');
  if not v_ctx.bus_inspected_today then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  -- An inspection from before six this morning belongs to yesterday.
  v_case := 'an inspection from before the operational day does not count';
  update public.bus_checklist
     set submitted_at = ((v_now_ph::date - 1) + time '20:00') at time zone 'UTC'
   where trip_id = 'TEST-CTX-OPEN';
  select * into v_ctx from public.shift_start_context('TEST-CTX-NEXT');
  if v_ctx.bus_inspected_today then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  -- A failed inspection grounds the bus and is never a reason to offer a skip.
  v_case := 'a failed inspection does not count as inspected';
  update public.bus_checklist
     set submitted_at   = (v_now_ph - interval '8 hours') at time zone 'UTC',
         checklist_status = 'Failed'
   where trip_id = 'TEST-CTX-OPEN';
  select * into v_ctx from public.shift_start_context('TEST-CTX-NEXT');
  if v_ctx.bus_inspected_today then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  -- A trip nobody is driving answers nothing rather than raising, so the shape of the
  -- refusal cannot be used to find out whether a trip exists.
  v_case := 'a trip that does not exist returns no row';
  perform * from public.shift_start_context('TRIP-NO-SUCH-ID');
  if found then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  raise notice 'all shift_start_context cases passed';
end
$test$;

rollback;
