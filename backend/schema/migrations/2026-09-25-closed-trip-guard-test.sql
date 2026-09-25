-- Exercises public.trips_closed_guard and public.vehicles_release_guard.
--
-- Everything happens inside one transaction that ends in a rollback, so no trip, no bus
-- status and no audit row survives the run.
--
-- Fixtures
--
-- It needs one bus with no active trip, two users and a route, and takes the lowest ids it
-- finds. The scene is the handover this guards: driver one's morning trip closed by driver
-- two starting the afternoon on the same bus, with driver one's phone still holding the
-- morning trip.
--
-- Run it after the migration, in the Supabase SQL editor. A failure raises and aborts; a
-- pass prints one notice per case and a final line.

begin;

do $test$
declare
  v_today  date;
  v_bus    text;
  v_d1     integer;
  v_d2     integer;
  v_route  integer;
  v_wall   timestamptz;
  v_status public.trip_status_enum;
  v_end    timestamptz;
  v_end2   timestamptz;
  v_total  integer;
  v_total2 integer;
  v_bstat  public.vehicle_status_enum;
  v_n      integer;
  v_case   text;
begin
  v_today := (now() at time zone 'Asia/Manila')::date;
  v_wall  := (now() at time zone 'Asia/Manila') at time zone 'UTC';

  select vehicle_id into v_bus
    from public.vehicles v
   where not exists (select 1 from public.trips t
                      where t.vehicle_id = v.vehicle_id
                        and t.trip_status = 'Active')
   order by vehicle_id limit 1;

  select user_id into v_d1 from public.users order by user_id limit 1;
  select user_id into v_d2 from public.users where user_id > v_d1 order by user_id limit 1;
  select route_id into v_route from public.routes order by route_id limit 1;

  if v_bus is null or v_d2 is null or v_route is null then
    raise exception 'needs a bus with no active trip, two users and a route to run';
  end if;
  raise notice 'fixtures: bus %, drivers % and %, route %', v_bus, v_d1, v_d2, v_route;

  -- Driver one's morning trip, closed at handover with 39 boarded and the bus On Trip under
  -- driver two's afternoon. Written without claims, as the database itself, so none of the
  -- guards being tested stand in the way of setting the scene.
  perform set_config('request.jwt.claims', '', true);

  insert into public.trips
    (trip_id, "date", shift_type, shift_start_time, shift_end_time,
     route_id, vehicle_id, driver_id, trip_status, actual_start_time, actual_end_time,
     total_boarded)
  values
    ('TEST-CTG-CLOSED', v_today, 'Morning', '06:00', '14:00',
     v_route, v_bus, v_d1, 'Completed',
     (v_today + time '06:05') at time zone 'UTC', v_wall - interval '30 minutes',
     39),
    ('TEST-CTG-NEXT', v_today, 'Afternoon', '14:00', '22:00',
     v_route, v_bus, v_d2, 'Active',
     v_wall - interval '30 minutes', null, 0);

  update public.vehicles set vehicle_status = 'On Trip' where vehicle_id = v_bus;

  select actual_end_time into v_end from public.trips where trip_id = 'TEST-CTG-CLOSED';

  -- 1. The running count, sent by driver one's phone every fifteen seconds.
  v_case := 'a running count sent to a closed trip changes nothing and files nothing';
  perform set_config('request.jwt.claims',
    format('{"role":"app_driver","user_id":"%s"}', v_d1), true);
  update public.trips
     set total_boarded = 40, estimated_revenue = 520
   where trip_id = 'TEST-CTG-CLOSED';
  get diagnostics v_n = row_count;
  select total_boarded into v_total from public.trips where trip_id = 'TEST-CTG-CLOSED';
  if v_n <> 0 or v_total <> 39 then
    raise exception 'fail: % (% rows changed, total %)', v_case, v_n, v_total;
  end if;
  select count(*) into v_n from public.audit_log where target_id like 'TEST-CTG-%';
  if v_n <> 0 then
    raise exception 'fail: % (% audit rows)', v_case, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- 2. Driver one ending the trip on their phone, an hour after the handover.
  v_case := 'an End sent to a closed trip keeps the end time and count it closed with';
  update public.trips
     set trip_status       = 'Completed',
         total_boarded     = 40,
         estimated_revenue = 520,
         actual_end_time   = v_wall + interval '30 minutes'
   where trip_id = 'TEST-CTG-CLOSED';
  select trip_status, actual_end_time, total_boarded
    into v_status, v_end2, v_total
    from public.trips where trip_id = 'TEST-CTG-CLOSED';
  if v_status <> 'Completed' or v_end2 is distinct from v_end
     or v_total <> 39 then
    raise exception 'fail: % (status %, end % against %, total %)',
      v_case, v_status, v_end2, v_end, v_total;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'the late End is filed as trip_end_after_close with both counts';
  select count(*) into v_n from public.audit_log
   where action = 'trip_end_after_close' and target_id = 'TEST-CTG-CLOSED'
     and (changes ->> 'kept_total')::integer = 39
     and (changes ->> 'sent_total')::integer = 40;
  if v_n <> 1 then
    raise exception 'fail: % (% matching rows)', v_case, v_n;
  end if;
  raise notice 'pass: %', v_case;

  -- 3. The End's last step: releasing the bus driver two is out on.
  v_case := 'a driver cannot release a bus another driver has an active trip on';
  update public.vehicles
     set vehicle_status = 'Ready to Deploy'
   where vehicle_id = v_bus and vehicle_status = 'On Trip';
  select vehicle_status into v_bstat from public.vehicles where vehicle_id = v_bus;
  if v_bstat <> 'On Trip' then
    raise exception 'fail: % (bus is %)', v_case, v_bstat;
  end if;
  raise notice 'pass: %', v_case;

  -- 4. A start on the closed trip, which would reopen it and take the bus back.
  v_case := 'a start sent to a closed trip does not reopen it';
  update public.trips
     set trip_status = 'Active', actual_start_time = v_wall
   where trip_id = 'TEST-CTG-CLOSED';
  select trip_status into v_status from public.trips where trip_id = 'TEST-CTG-CLOSED';
  if v_status <> 'Completed' then
    raise exception 'fail: % (closed trip is %)', v_case, v_status;
  end if;
  select count(*) into v_n from public.audit_log
   where action = 'trip_taken_over' and target_id like 'TEST-CTG-%';
  if v_n <> 0 then
    raise exception 'fail: % (the next trip was taken over)', v_case;
  end if;
  raise notice 'pass: %', v_case;

  -- 5. The counter phone delivering a count made before the handover, from a dead zone.
  v_case := 'a late count from the counter phone still reaches the closed trip';
  perform set_config('request.jwt.claims',
    '{"role":"app_camera","device_id":"cam-test"}', true);
  update public.trips set total_boarded = 41 where trip_id = 'TEST-CTG-CLOSED';
  select total_boarded into v_total from public.trips where trip_id = 'TEST-CTG-CLOSED';
  if v_total <> 41 then
    raise exception 'fail: % (total %)', v_case, v_total;
  end if;
  raise notice 'pass: %', v_case;

  -- 6. Driver two, whose trip is the active one, is not held back by any of this.
  v_case := 'the driver on the active trip still counts, and still sets the bus';
  perform set_config('request.jwt.claims',
    format('{"role":"app_driver","user_id":"%s"}', v_d2), true);
  update public.trips set total_boarded = 5 where trip_id = 'TEST-CTG-NEXT';
  update public.vehicles set vehicle_status = 'Flagged' where vehicle_id = v_bus;
  select total_boarded into v_total2 from public.trips where trip_id = 'TEST-CTG-NEXT';
  select vehicle_status into v_bstat from public.vehicles where vehicle_id = v_bus;
  if v_total2 <> 5 or v_bstat <> 'Flagged' then
    raise exception 'fail: % (total %, bus %)', v_case, v_total2, v_bstat;
  end if;
  raise notice 'pass: %', v_case;

  -- 7. Driver two's own End, on a trip still active, goes through as ever, and with no
  --    active trip left on the bus driver one may set its status again.
  v_case := 'an End on an active trip still closes it';
  update public.trips
     set trip_status = 'Completed', actual_end_time = v_wall
   where trip_id = 'TEST-CTG-NEXT';
  select trip_status into v_status from public.trips where trip_id = 'TEST-CTG-NEXT';
  if v_status <> 'Completed' then
    raise exception 'fail: % (status %)', v_case, v_status;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'a bus with no active trip on it can be set by any of its drivers';
  perform set_config('request.jwt.claims',
    format('{"role":"app_driver","user_id":"%s"}', v_d1), true);
  update public.vehicles set vehicle_status = 'Ready to Deploy' where vehicle_id = v_bus;
  select vehicle_status into v_bstat from public.vehicles where vehicle_id = v_bus;
  if v_bstat <> 'Ready to Deploy' then
    raise exception 'fail: % (bus is %)', v_case, v_bstat;
  end if;
  raise notice 'pass: %', v_case;

  select count(*) into v_n from public.audit_log where target_id like 'TEST-CTG-%';
  raise notice 'all closed trip cases passed, % audit rows written and about to be rolled back', v_n;
end
$test$;

rollback;
