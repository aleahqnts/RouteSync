-- Exercises public.trips_count_guard against a temporary copy of the trips table.
--
-- Everything happens inside one transaction that ends in a rollback, and the table it
-- writes to is a temporary one, so no trip, no vehicle and no audit row survives the
-- run. The only real tables it touches are fare_config, which it reads, and audit_log,
-- which it writes to and then rolls back. Sequence values are consumed either way.
--
-- Run it after the migration, in the Supabase SQL editor. A failure raises and aborts;
-- a pass prints one notice per case and a final line.

begin;

create temp table trips_test (like public.trips including defaults) on commit drop;

create trigger trg_test
  before update on trips_test
  for each row
  when (
       old.total_boarded      is distinct from new.total_boarded
    or old.boarded_counted    is distinct from new.boarded_counted
    or old.boarded_adjustment is distinct from new.boarded_adjustment
    or old.estimated_revenue  is distinct from new.estimated_revenue
    or old.trip_status        is distinct from new.trip_status
  )
  execute function public.trips_count_guard();

do $test$
declare
  v_fare    numeric(10,2);
  v_total   integer;
  v_counted integer;
  v_adjust  integer;
  v_rev     numeric(10,2);
  v_status  public.trip_status_enum;
  v_n       integer;
  v_case    text;
begin
  select standard_fare into v_fare from public.fare_config where id = 1;
  if v_fare is null then
    raise exception 'fare_config row 1 is missing, the guard cannot price anything';
  end if;
  raise notice 'standard fare: %', v_fare;

  insert into trips_test
    (trip_id, "date", shift_type, shift_start_time, shift_end_time,
     route_id, vehicle_id, driver_id, trip_status)
  values
    ('TEST-COUNT-GUARD', current_date, 'Morning', '06:00', '14:00',
     1, 'V000', 0, 'Active');

  -- 1. A counter phone claiming a count it has just made.
  v_case := '1 camera raises to 10';
  perform set_config('request.jwt.claims',
    '{"role":"app_camera","device_id":"cam-test"}', true);
  update trips_test set total_boarded = 10 where trip_id = 'TEST-COUNT-GUARD';
  select total_boarded, boarded_counted, boarded_adjustment, estimated_revenue
    into v_total, v_counted, v_adjust, v_rev
    from trips_test where trip_id = 'TEST-COUNT-GUARD';
  if (v_total, v_counted, v_adjust, v_rev) is distinct from (10, 10, 0, round(10 * v_fare, 2)) then
    raise exception 'FAIL %: got total=% counted=% adjust=% revenue=%',
      v_case, v_total, v_counted, v_adjust, v_rev;
  end if;
  raise notice 'pass: %', v_case;

  -- 2. A stale claim from below the high-water mark is ignored and recorded.
  v_case := '2 camera claims 8, below the stored 10';
  update trips_test set total_boarded = 8 where trip_id = 'TEST-COUNT-GUARD';
  select total_boarded, boarded_counted into v_total, v_counted
    from trips_test where trip_id = 'TEST-COUNT-GUARD';
  select count(*) into v_n from public.audit_log
    where target_id = 'TEST-COUNT-GUARD' and action = 'count_clamped';
  if (v_total, v_counted, v_n) is distinct from (10, 10, 1) then
    raise exception 'FAIL %: got total=% counted=% clamped_rows=%',
      v_case, v_total, v_counted, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- 3. The count climbs to 35 before the bus enters a dead zone.
  v_case := '3 camera raises to 35';
  update trips_test set total_boarded = 35 where trip_id = 'TEST-COUNT-GUARD';
  select total_boarded, boarded_counted into v_total, v_counted
    from trips_test where trip_id = 'TEST-COUNT-GUARD';
  if (v_total, v_counted) is distinct from (35, 35) then
    raise exception 'FAIL %: got total=% counted=%', v_case, v_total, v_counted;
  end if;
  raise notice 'pass: %', v_case;

  -- 4. The reported incident. The driver app finalizes offline holding 25, the figure
  --    it last saw before losing signal, and prices revenue from it.
  v_case := '4 driver finalizes at 25 while 35 is stored';
  perform set_config('request.jwt.claims',
    '{"role":"app_driver","user_id":"7"}', true);
  update trips_test
     set trip_status = 'Completed',
         total_boarded = 25,
         estimated_revenue = round(25 * v_fare, 2),
         actual_end_time = now()
   where trip_id = 'TEST-COUNT-GUARD';
  select total_boarded, boarded_counted, estimated_revenue, trip_status
    into v_total, v_counted, v_rev, v_status
    from trips_test where trip_id = 'TEST-COUNT-GUARD';
  select count(*) into v_n from public.audit_log
    where target_id = 'TEST-COUNT-GUARD' and action = 'count_clamped';
  if (v_total, v_counted, v_rev, v_status::text, v_n)
     is distinct from (35, 35, round(35 * v_fare, 2), 'Completed', 2) then
    raise exception 'FAIL %: got total=% counted=% revenue=% status=% clamped_rows=%',
      v_case, v_total, v_counted, v_rev, v_status, v_n;
  end if;
  raise notice 'pass: % (trip completed, count and revenue held at 35)', v_case;

  -- 5. The counter phone reconnects after the trip closed and delivers its dead zone
  --    count. This is the write that used to be lost.
  v_case := '5 camera reconciles 42 after completion';
  perform set_config('request.jwt.claims',
    '{"role":"app_camera","device_id":"cam-test"}', true);
  update trips_test set total_boarded = 42 where trip_id = 'TEST-COUNT-GUARD';
  select total_boarded, boarded_counted, estimated_revenue
    into v_total, v_counted, v_rev
    from trips_test where trip_id = 'TEST-COUNT-GUARD';
  select count(*) into v_n from public.audit_log
    where target_id = 'TEST-COUNT-GUARD' and action = 'count_late_raise';
  if (v_total, v_counted, v_rev, v_n)
     is distinct from (42, 42, round(42 * v_fare, 2), 1) then
    raise exception 'FAIL %: got total=% counted=% revenue=% late_rows=%',
      v_case, v_total, v_counted, v_rev, v_n;
  end if;
  raise notice 'pass: % (revenue followed the count)', v_case;

  -- 6. A driver correcting an over-count by hand.
  v_case := '6 driver adjusts by -3';
  perform set_config('request.jwt.claims',
    '{"role":"app_driver","user_id":"7"}', true);
  update trips_test set boarded_adjustment = -3 where trip_id = 'TEST-COUNT-GUARD';
  select total_boarded, boarded_counted, boarded_adjustment, estimated_revenue
    into v_total, v_counted, v_adjust, v_rev
    from trips_test where trip_id = 'TEST-COUNT-GUARD';
  select count(*) into v_n from public.audit_log
    where target_id = 'TEST-COUNT-GUARD' and action = 'count_adjusted';
  if (v_total, v_counted, v_adjust, v_rev, v_n)
     is distinct from (39, 42, -3, round(39 * v_fare, 2), 1) then
    raise exception 'FAIL %: got total=% counted=% adjust=% revenue=% adjusted_rows=%',
      v_case, v_total, v_counted, v_adjust, v_rev, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- 7. A later machine claim does not erase the correction.
  v_case := '7 camera reclaims 42, correction survives';
  perform set_config('request.jwt.claims',
    '{"role":"app_camera","device_id":"cam-test"}', true);
  update trips_test set total_boarded = 42 where trip_id = 'TEST-COUNT-GUARD';
  select total_boarded, boarded_counted, boarded_adjustment
    into v_total, v_counted, v_adjust
    from trips_test where trip_id = 'TEST-COUNT-GUARD';
  if (v_total, v_counted, v_adjust) is distinct from (39, 42, -3) then
    raise exception 'FAIL %: got total=% counted=% adjust=%',
      v_case, v_total, v_counted, v_adjust;
  end if;
  raise notice 'pass: %', v_case;

  -- 8. The counter phone cannot move the correction.
  v_case := '8 camera cannot change the adjustment';
  update trips_test set boarded_adjustment = 0 where trip_id = 'TEST-COUNT-GUARD';
  select boarded_adjustment, total_boarded into v_adjust, v_total
    from trips_test where trip_id = 'TEST-COUNT-GUARD';
  if (v_adjust, v_total) is distinct from (-3, 39) then
    raise exception 'FAIL %: got adjust=% total=%', v_case, v_adjust, v_total;
  end if;
  raise notice 'pass: %', v_case;

  -- 9. The service key settles a disputed count, including downwards.
  v_case := '9 service key sets the total to 50';
  perform set_config('request.jwt.claims', '{"role":"service_role"}', true);
  update trips_test set total_boarded = 50 where trip_id = 'TEST-COUNT-GUARD';
  select total_boarded, boarded_counted, boarded_adjustment, estimated_revenue
    into v_total, v_counted, v_adjust, v_rev
    from trips_test where trip_id = 'TEST-COUNT-GUARD';
  if (v_total, v_counted, v_adjust, v_rev)
     is distinct from (50, 53, -3, round(50 * v_fare, 2)) then
    raise exception 'FAIL %: got total=% counted=% adjust=% revenue=%',
      v_case, v_total, v_counted, v_adjust, v_rev;
  end if;
  raise notice 'pass: %', v_case;

  v_case := '10 service key lowers the total to 20';
  update trips_test set total_boarded = 20 where trip_id = 'TEST-COUNT-GUARD';
  select total_boarded, boarded_counted into v_total, v_counted
    from trips_test where trip_id = 'TEST-COUNT-GUARD';
  if (v_total, v_counted) is distinct from (20, 23) then
    raise exception 'FAIL %: got total=% counted=%', v_case, v_total, v_counted;
  end if;
  raise notice 'pass: %', v_case;

  -- 11. A correction larger than the count floors at zero rather than going negative.
  v_case := '11 an oversized correction floors at zero';
  perform set_config('request.jwt.claims',
    '{"role":"app_driver","user_id":"7"}', true);
  update trips_test set boarded_adjustment = -100 where trip_id = 'TEST-COUNT-GUARD';
  select total_boarded, boarded_counted, estimated_revenue
    into v_total, v_counted, v_rev
    from trips_test where trip_id = 'TEST-COUNT-GUARD';
  if (v_total, v_counted, v_rev) is distinct from (0, 23, 0.00) then
    raise exception 'FAIL %: got total=% counted=% revenue=%',
      v_case, v_total, v_counted, v_rev;
  end if;
  raise notice 'pass: %', v_case;

  -- 12. The heartbeat the counter phone sends every five seconds skips the guard.
  v_case := '12 a heartbeat only write changes nothing';
  perform set_config('request.jwt.claims',
    '{"role":"app_camera","device_id":"cam-test"}', true);
  select count(*) into v_n from public.audit_log where target_id = 'TEST-COUNT-GUARD';
  update trips_test set count_heartbeat = now() where trip_id = 'TEST-COUNT-GUARD';
  select total_boarded, boarded_counted, boarded_adjustment
    into v_total, v_counted, v_adjust
    from trips_test where trip_id = 'TEST-COUNT-GUARD';
  if (v_total, v_counted, v_adjust) is distinct from (0, 23, -100) then
    raise exception 'FAIL %: got total=% counted=% adjust=%',
      v_case, v_total, v_counted, v_adjust;
  end if;
  select count(*) - v_n into v_n from public.audit_log where target_id = 'TEST-COUNT-GUARD';
  if v_n <> 0 then
    raise exception 'FAIL %: the heartbeat wrote % audit rows', v_case, v_n;
  end if;
  raise notice 'pass: %', v_case;

  select count(*) into v_n from public.audit_log where target_id = 'TEST-COUNT-GUARD';
  raise notice 'all cases passed, % audit rows written and about to be rolled back', v_n;
end
$test$;

rollback;
