-- Exercises device_health_log and device_health_log_ph.
--
-- Everything happens inside one transaction that ends in a rollback, so no health event
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
  v_log     bigint;
  v_ph      timestamp;
  v_case    text;
begin
  select trip_id into v_trip from public.trips order by trip_id limit 1;
  if v_trip is null then
    raise exception 'needs at least one trip to run';
  end if;

  -- A restart is stored as given, with no battery level, since it reports an event
  -- rather than a measurement.
  v_case := 'a restart is accepted';
  insert into public.device_health_log
    (device_id, trip_id, event_type, occurred_at)
  values
    (v_dev, v_trip, 'restart', v_now)
  returning log_id into v_log;
  select count(*) into v_n from public.device_health_log where log_id = v_log;
  if v_n <> 1 then
    raise exception 'fail: % (% rows)', v_case, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- A charge reading is stored with its level, which is what the consumption slope is
  -- drawn through.
  v_case := 'a battery reading with a level is accepted';
  insert into public.device_health_log
    (device_id, trip_id, event_type, battery_level, occurred_at)
  values
    (v_dev, v_trip, 'battery_reading', 87, v_now - interval '1 hour');
  select count(*) into v_n from public.device_health_log
   where device_id = v_dev and event_type = 'battery_reading' and battery_level = 87;
  if v_n <> 1 then
    raise exception 'fail: % (% rows)', v_case, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- Any event type other than the two is refused, so a crash column cannot arrive later
  -- by the back door and stand empty.
  v_case := 'an event type outside restart and battery_reading is refused';
  begin
    insert into public.device_health_log
      (device_id, trip_id, event_type, occurred_at)
    values
      (v_dev, v_trip, 'crash', v_now);
    raise exception 'fail: % (it was accepted)', v_case;
  exception when check_violation then
    raise notice 'pass: %', v_case;
  end;

  -- A charge outside nought to a hundred is refused, since a slope drawn through an
  -- impossible reading gives a consumption figure that looks real.
  v_case := 'a battery level above one hundred is refused';
  begin
    insert into public.device_health_log
      (device_id, trip_id, event_type, battery_level, occurred_at)
    values
      (v_dev, v_trip, 'battery_reading', 150, v_now);
    raise exception 'fail: % (it was accepted)', v_case;
  exception when check_violation then
    raise notice 'pass: %', v_case;
  end;

  -- An event naming a trip that does not exist is refused, leaving no orphan.
  v_case := 'an event for a trip that does not exist is refused';
  begin
    insert into public.device_health_log
      (device_id, trip_id, event_type, occurred_at)
    values
      (v_dev, 'TRIP-NO-SUCH-ID', 'restart', v_now);
    raise exception 'fail: % (it was accepted)', v_case;
  exception when foreign_key_violation then
    raise notice 'pass: %', v_case;
  end;

  -- The reading view renders the stored instant in Philippine wall-clock, which is how a
  -- restart gets placed against the shift it interrupted.
  v_case := 'the reading view gives the event in Philippine time';
  select occurred_ph into v_ph from public.device_health_log_ph where log_id = v_log;
  if v_ph is null or v_ph is distinct from (v_now at time zone 'Asia/Manila') then
    raise exception 'fail: % (view gave %)', v_case, v_ph;
  end if;
  raise notice 'pass: % (%)', v_case, v_ph;

  raise notice 'all device_health_log cases passed';
end
$test$;

rollback;
