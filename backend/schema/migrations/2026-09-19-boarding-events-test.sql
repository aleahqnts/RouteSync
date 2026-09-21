-- Exercises boarding_events and boarding_events_ph.
--
-- Everything happens inside one transaction that ends in a rollback, so no event
-- survives the run. It needs one trip to exist and uses the first it finds, only ever
-- inside the rollback.
--
-- Run it after the migration, in the Supabase SQL editor. A failure raises and aborts;
-- a pass prints one notice per case and a final line.
--
-- The row level security policy is not exercised here. It reads a claim from a device
-- JWT, which the SQL editor does not carry, and the service key this runs under bypasses
-- row level security by design. Check that one from the counter phone.

begin;

do $test$
declare
  v_trip    text;
  v_dev     text := 'test-device-0000';
  v_now     timestamptz := now();
  v_n       integer;
  v_lat     double precision;
  v_case    text;
begin
  select trip_id into v_trip from public.trips order by trip_id limit 1;
  if v_trip is null then
    raise exception 'needs at least one trip to run';
  end if;

  -- A crossing is stored as given.
  v_case := 'an inward crossing is accepted';
  insert into public.boarding_events
    (event_id, trip_id, counter_device_id, direction, device_timestamp)
  values
    ('test-evt-1', v_trip, v_dev, 'in', v_now - interval '4 seconds');
  select count(*) into v_n from public.boarding_events where event_id = 'test-evt-1';
  if v_n <> 1 then
    raise exception 'fail: % (% rows)', v_case, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- Delivering the same event twice stores it once, which is what makes a resend after a
  -- dead zone safe.
  v_case := 'the same event delivered twice stores once';
  insert into public.boarding_events
    (event_id, trip_id, counter_device_id, direction, device_timestamp)
  values
    ('test-evt-1', v_trip, v_dev, 'in', v_now - interval '4 seconds')
  on conflict (event_id) do nothing;
  select count(*) into v_n from public.boarding_events where event_id = 'test-evt-1';
  if v_n <> 1 then
    raise exception 'fail: % (% rows)', v_case, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- An outward crossing is recorded, so a camera that detected and excluded an exit can
  -- be told apart from one that saw nothing.
  v_case := 'an outward crossing is accepted and kept separate';
  insert into public.boarding_events
    (event_id, trip_id, counter_device_id, direction, device_timestamp)
  values
    ('test-evt-2', v_trip, v_dev, 'out', v_now - interval '3 seconds');
  select count(*) into v_n from public.boarding_events
   where trip_id = v_trip and direction = 'in' and event_id like 'test-evt-%';
  if v_n <> 1 then
    raise exception 'fail: % (% inward rows)', v_case, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- Any direction other than the two is refused, so a typo cannot become a third kind.
  v_case := 'a direction outside in and out is refused';
  begin
    insert into public.boarding_events
      (event_id, trip_id, counter_device_id, direction, device_timestamp)
    values
      ('test-evt-3', v_trip, v_dev, 'sideways', v_now);
    raise exception 'fail: % (it was accepted)', v_case;
  exception when check_violation then
    raise notice 'pass: %', v_case;
  end;

  -- An event naming a trip that does not exist is refused, leaving no orphan.
  v_case := 'an event for a trip that does not exist is refused';
  begin
    insert into public.boarding_events
      (event_id, trip_id, counter_device_id, direction, device_timestamp)
    values
      ('test-evt-4', 'TRIP-NO-SUCH-ID', v_dev, 'in', v_now);
    raise exception 'fail: % (it was accepted)', v_case;
  exception when foreign_key_violation then
    raise notice 'pass: %', v_case;
  end;

  -- Latency is worked out on the stored instants, so it is seconds rather than hours
  -- even though the view reads the times in Philippine wall-clock.
  v_case := 'the reading view gives latency in seconds';
  select latency_seconds into v_lat from public.boarding_events_ph
   where event_id = 'test-evt-1';
  if v_lat is null or v_lat < 0 or v_lat > 600 then
    raise exception 'fail: % (latency %)', v_case, v_lat;
  end if;
  raise notice 'pass: %', v_case;

  -- The gap between the reported figure and the events behind it is what marks a trip as
  -- counted by hand. It is only meaningful once both are present, so the arithmetic is
  -- checked rather than any particular trip's value.
  v_case := 'the reconciliation gap is computable';
  select t.total_boarded - count(e.event_id) filter (where e.direction = 'in')
    into v_n
    from public.trips t
    left join public.boarding_events e on e.trip_id = t.trip_id
   where t.trip_id = v_trip
   group by t.total_boarded;
  if v_n is null then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: % (gap % for trip %)', v_case, v_n, v_trip;

  raise notice 'all boarding_events cases passed';
end
$test$;

rollback;
