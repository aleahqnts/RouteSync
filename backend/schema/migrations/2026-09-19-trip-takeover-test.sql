-- Exercises public.trips_takeover_guard.
--
-- Everything happens inside one transaction that ends in a rollback, so no trip and no
-- audit row survives the run. Sequence values are consumed either way.
--
-- The trips have to be real rows in public.trips rather than a temporary copy, because the
-- trigger looks the bus's other active trips up in public.trips by name, and a temporary
-- table of the same shape would be invisible to it.
--
-- Fixtures
--
-- It needs two vehicles, two users and a route, and takes the lowest ids it finds. The
-- vehicles must have no Active trip of their own: the rule keys on the bus alone, so a bus
-- already carrying live work would decide the outcome of half the cases here. That is what
-- the not exists clause in the lookup is for, and the run stops if two such buses cannot be
-- found.
--
-- Dates are worked out from the Philippine wall clock rather than current_date, since the
-- session runs in UTC and the two disagree for eight hours of every day. The trips that
-- have to read as still running are dated tomorrow, so the case is true whenever this runs.
--
-- Other triggers the fixtures meet
--
--   trg_trips_count_guard   fires on every status change here, because it watches
--                           trip_status too. It finds no count to move, reprices revenue
--                           from the unchanged total of zero, and writes nothing to the
--                           log, so nothing was needed to work around it.
--   trg_trips_roster_edit   never fires. The fixtures leave roster_month null, and a status
--                           change touches none of the columns it watches.
--
-- Run it after the migration, in the Supabase SQL editor. A failure raises and aborts; a
-- pass prints one notice per case and a final line.

begin;

do $test$
declare
  v_today   date;
  v_bus1    text;
  v_bus2    text;
  v_d1      integer;
  v_d2      integer;
  v_route   integer;
  v_status  public.trip_status_enum;
  v_status2 public.trip_status_enum;
  v_end     timestamptz;
  v_drift   double precision;
  v_changes jsonb;
  v_summary text;
  v_n       integer;
  v_case    text;
begin
  v_today := (now() at time zone 'Asia/Manila')::date;

  select vehicle_id into v_bus1
    from public.vehicles v
   where not exists (select 1 from public.trips t
                      where t.vehicle_id = v.vehicle_id
                        and t.trip_status = 'Active')
   order by vehicle_id limit 1;

  select vehicle_id into v_bus2
    from public.vehicles v
   where v.vehicle_id > v_bus1
     and not exists (select 1 from public.trips t
                      where t.vehicle_id = v.vehicle_id
                        and t.trip_status = 'Active')
   order by vehicle_id limit 1;

  select user_id into v_d1 from public.users order by user_id limit 1;
  select user_id into v_d2 from public.users where user_id > v_d1 order by user_id limit 1;
  select route_id into v_route from public.routes order by route_id limit 1;

  if v_bus2 is null or v_d2 is null or v_route is null then
    raise exception 'needs two buses with no active trip, two users and a route to run';
  end if;
  raise notice 'fixtures: buses % and %, drivers % and %, route %',
    v_bus1, v_bus2, v_d1, v_d2, v_route;

  -- A morning trip left running yesterday, well past its scheduled end, and the afternoon
  -- trip that follows it on the same bus. The start time is written the way the driver app
  -- writes it: Philippine wall clock stored as UTC.
  insert into public.trips
    (trip_id, "date", shift_type, shift_start_time, shift_end_time,
     route_id, vehicle_id, driver_id, trip_status, actual_start_time)
  values
    ('TEST-TKO-STALE', v_today - 1, 'Morning', '06:00', '14:00',
     v_route, v_bus1, v_d1, 'Active',
     (v_today - 1 + time '06:05') at time zone 'UTC'),
    ('TEST-TKO-NEXT', v_today, 'Afternoon', '14:00', '22:00',
     v_route, v_bus1, v_d2, 'Not Yet Started', null);

  -- A pair on the second bus, dated tomorrow, so the first of them is inside its hours no
  -- matter what time of day this runs.
  insert into public.trips
    (trip_id, "date", shift_type, shift_start_time, shift_end_time,
     route_id, vehicle_id, driver_id, trip_status)
  values
    ('TEST-TKO-RUNNING', v_today + 1, 'Morning', '06:00', '14:00',
     v_route, v_bus2, v_d1, 'Not Yet Started'),
    ('TEST-TKO-EARLY', v_today + 1, 'Afternoon', '14:00', '22:00',
     v_route, v_bus2, v_d2, 'Not Yet Started');

  -- 1. The ordinary start, on a bus with nothing else running.
  v_case := 'a start on a bus with no other active trip is left alone';
  perform set_config('request.jwt.claims',
    format('{"role":"app_driver","user_id":"%s"}', v_d1), true);
  update public.trips
     set trip_status       = 'Active',
         actual_start_time = (now() at time zone 'Asia/Manila') at time zone 'UTC'
   where trip_id = 'TEST-TKO-RUNNING';
  select trip_status into v_status from public.trips where trip_id = 'TEST-TKO-RUNNING';
  select count(*) into v_n from public.audit_log
   where action = 'trip_taken_over' and target_id like 'TEST-TKO-%';
  if v_status <> 'Active' or v_n <> 0 then
    raise exception 'fail: % (status %, % takeover rows)', v_case, v_status, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- 2. The forgotten morning trip, closed by the afternoon driver starting theirs.
  v_case := 'a start on a bus whose trip is past its scheduled end closes that trip';
  perform set_config('request.jwt.claims',
    format('{"role":"app_driver","user_id":"%s"}', v_d2), true);
  update public.trips
     set trip_status       = 'Active',
         actual_start_time = (now() at time zone 'Asia/Manila') at time zone 'UTC'
   where trip_id = 'TEST-TKO-NEXT';
  select trip_status, actual_end_time into v_status, v_end
    from public.trips where trip_id = 'TEST-TKO-STALE';
  select trip_status into v_status2 from public.trips where trip_id = 'TEST-TKO-NEXT';
  if v_status <> 'Completed' or v_end is null or v_status2 <> 'Active' then
    raise exception 'fail: % (closed trip %, end time %, new trip %)',
      v_case, v_status, v_end, v_status2;
  end if;
  raise notice 'pass: %', v_case;

  -- 3. The stamp has to be on the clock the driver app writes. A trigger using now()
  --    directly would land eight hours out, which is 28800 seconds.
  v_case := 'the stamped end is Philippine wall clock, as a phone would write it';
  v_drift := extract(epoch from
    (v_end - ((now() at time zone 'Asia/Manila') at time zone 'UTC')));
  if abs(v_drift) > 120 then
    raise exception 'fail: % (off by % seconds; near 28800 means the zone was dropped)',
      v_case, v_drift;
  end if;
  raise notice 'pass: % (% seconds from the wall clock)', v_case, round(v_drift::numeric, 1);

  -- 4. The close is filed as its own action, against the trip that was closed.
  v_case := 'the close is filed as trip_taken_over against the closed trip';
  select count(*) into v_n from public.audit_log
   where action = 'trip_taken_over' and target_id = 'TEST-TKO-STALE';
  select changes, summary into v_changes, v_summary
    from public.audit_log
   where action = 'trip_taken_over' and target_id = 'TEST-TKO-STALE'
   order by id desc limit 1;
  if v_n <> 1
     or (v_changes ->> 'closed_trip_id') is distinct from 'TEST-TKO-STALE'
     or (v_changes ->> 'new_trip_id')    is distinct from 'TEST-TKO-NEXT'
     or (v_changes ->> 'closed_driver_id')::integer   is distinct from v_d1
     or (v_changes ->> 'new_driver_id')::integer      is distinct from v_d2
     or (v_changes ->> 'past_scheduled_end')::boolean is distinct from true then
    raise exception 'fail: % (% rows, changes %)', v_case, v_n, v_changes;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'the summary names the closed trip, the bus and both drivers';
  if v_summary not like '%TEST-TKO-STALE%'
     or v_summary not like ('%' || v_bus1 || '%')
     or v_summary not like ('%driver ' || v_d1::text || ' %')
     or v_summary not like ('%driver ' || v_d2::text || ' %') then
    raise exception 'fail: % (%)', v_case, v_summary;
  end if;
  raise notice 'pass: %', v_case;

  -- 5. A bus carrying two active trips, which is the state this rule exists to prevent and
  --    which older data can still be in. Inserted directly, since the trigger watches
  --    updates only. The counter phone's five second write has to pass through untouched,
  --    or counting stops the moment one bus is inconsistent.
  insert into public.trips
    (trip_id, "date", shift_type, shift_start_time, shift_end_time,
     route_id, vehicle_id, driver_id, trip_status)
  values
    ('TEST-TKO-GHOST', v_today + 1, 'Evening', '22:00', '06:00',
     v_route, v_bus1, v_d1, 'Active');

  v_case := 'an ordinary write such as the count heartbeat never enters the trigger';
  perform set_config('request.jwt.claims',
    '{"role":"app_camera","device_id":"cam-test"}', true);
  begin
    update public.trips set count_heartbeat = now() where trip_id = 'TEST-TKO-NEXT';
  exception when sqlstate 'RS409' then
    raise exception 'fail: % (a write that changed no status entered the trigger)', v_case;
  end;
  select trip_status into v_status from public.trips where trip_id = 'TEST-TKO-GHOST';
  select count(*) into v_n from public.audit_log
   where action = 'trip_taken_over' and target_id like 'TEST-TKO-%';
  if v_status <> 'Active' or v_n <> 1 then
    raise exception 'fail: % (other trip %, % takeover rows)', v_case, v_status, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- 6. A driver arriving early cannot end a colleague's shift while it is still running.
  v_case := 'a start on a bus whose trip is still inside its hours is refused';
  perform set_config('request.jwt.claims',
    format('{"role":"app_driver","user_id":"%s"}', v_d2), true);
  begin
    update public.trips
       set trip_status       = 'Active',
           actual_start_time = (now() at time zone 'Asia/Manila') at time zone 'UTC'
     where trip_id = 'TEST-TKO-EARLY';
    raise exception 'fail: % (it was accepted)', v_case;
  exception when sqlstate 'RS409' then
    null;
  end;
  select trip_status into v_status  from public.trips where trip_id = 'TEST-TKO-RUNNING';
  select trip_status into v_status2 from public.trips where trip_id = 'TEST-TKO-EARLY';
  select count(*) into v_n from public.audit_log
   where action = 'trip_taken_over' and target_id = 'TEST-TKO-RUNNING';
  if v_status <> 'Active' or v_status2 <> 'Not Yet Started' or v_n <> 0 then
    raise exception 'fail: % (standing trip %, refused trip %, % takeover rows)',
      v_case, v_status, v_status2, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- 7. Dispatch can always settle it, and the forced close says so in the log.
  v_case := 'the service key forces the handover while the standing trip is still running';
  perform set_config('request.jwt.claims', '{"role":"service_role"}', true);
  update public.trips
     set trip_status       = 'Active',
         actual_start_time = (now() at time zone 'Asia/Manila') at time zone 'UTC'
   where trip_id = 'TEST-TKO-EARLY';
  select trip_status into v_status  from public.trips where trip_id = 'TEST-TKO-RUNNING';
  select trip_status into v_status2 from public.trips where trip_id = 'TEST-TKO-EARLY';
  select changes into v_changes from public.audit_log
   where action = 'trip_taken_over' and target_id = 'TEST-TKO-RUNNING'
   order by id desc limit 1;
  if v_status <> 'Completed' or v_status2 <> 'Active'
     or (v_changes ->> 'past_scheduled_end')::boolean is distinct from false then
    raise exception 'fail: % (standing trip %, new trip %, changes %)',
      v_case, v_status, v_status2, v_changes;
  end if;
  raise notice 'pass: %', v_case;

  select count(*) into v_n from public.audit_log where target_id like 'TEST-TKO-%';
  raise notice 'all takeover cases passed, % audit rows written and about to be rolled back', v_n;
end
$test$;

rollback;
