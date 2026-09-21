-- Two facts the shift screen needs before a driver may start, which it is not allowed to read.
--
-- Background
--
-- A driver's key sees only their own work. p_trips_driver_select restricts trips to
-- driver_id = jwt_uid(), and p_checklists_driver_select restricts inspections to trips
-- that driver ran. That is the right default and it stays.
--
-- Two rules added to the shift screen need facts on the other side of it:
--
--   Handover. Starting a trip on a bus that still has an open one closes that trip. The
--   driver has to be told before it happens, and the start has to be held until the other
--   shift is due to end. Neither is possible if the open trip is invisible.
--
--   Inspection once a day. A bus is inspected by the first driver to run it in an
--   operational day; later drivers on the same bus that day may skip. Whether it has been
--   inspected is a fact about the bus, and the inspection belongs to somebody else's trip.
--
-- Model
--
-- A function rather than a wider policy. Widening p_trips_driver_select to cover a
-- driver's assigned vehicle would expose every trip row on that bus, its driver and its
-- times, to every driver, through a predicate that has to be exactly right on a table
-- three applications write to. This returns the two answers and nothing else, so nothing
-- new becomes readable and there is one place to check.
--
-- The caller is verified against the trip they name. A driver may ask about their own
-- shift and no other, so the function cannot be used to look around the fleet. A caller
-- who names a trip that is not theirs gets no row, not an error, since telling them the
-- trip exists would itself be an answer.
--
-- The other driver's name is deliberately absent. The screen does not need it, users_app
-- already refuses it, and returning it here would turn a shift lookup into a way to
-- enumerate staff.
--
-- Time
--
-- Philippine wall clock throughout, matching how every client writes and every other
-- date in this system reads. Shift ends are built from trips.date and shift_end_time,
-- which are already local values, so they are compared against a local now with no offset
-- arithmetic. submitted_at is a timestamptz holding a wall-clock reading, so the
-- operational day boundary is relabelled the same way before it is compared.

begin;

create or replace function public.shift_start_context(p_trip_id character varying)
returns table (
  open_trip_id        character varying(20),
  open_trip_ends_at   timestamp,
  bus_inspected_today boolean
)
    language plpgsql
    stable
    security definer
    set search_path to 'public'
as $context$
declare
  v_uid       integer := public.jwt_uid();
  v_role      text    := coalesce(nullif(current_setting('request.jwt.claims', true), '')::json ->> 'role', 'db');
  v_vehicle   character varying(20);
  v_now_ph    timestamp   := now() at time zone 'Asia/Manila';
  v_day_start timestamp;
  v_day_wall  timestamptz;
  v_open_id   character varying(20);
  v_open_ends timestamp;
  v_inspected boolean;
begin
  -- The caller's own shift, or nothing. A key with no user id behind it, and a trip
  -- belonging to somebody else, are the same answer on purpose.
  select t.vehicle_id into v_vehicle
    from public.trips t
   where t.trip_id = p_trip_id
     and (v_role not in ('app_driver', 'app_camera') or t.driver_id = v_uid);

  if v_vehicle is null then
    return;
  end if;

  -- The operational day runs from six in the morning, so a night shift belongs to the day
  -- it started and the next morning's driver inspects again. The same rule the apps apply.
  v_day_start := case
                   when v_now_ph::time < time '06:00'
                     then (v_now_ph::date - 1) + time '06:00'
                   else v_now_ph::date + time '06:00'
                 end;
  v_day_wall  := v_day_start at time zone 'UTC';

  -- The open trip that is due to end first. A bus carrying more than one is already
  -- inconsistent, and the earliest is the one a handover is answering. Both values stay
  -- null when the bus is free, which is the ordinary case.
  select o.trip_id, o.ends_at
    into v_open_id, v_open_ends
    from (
      select t.trip_id,
             t.date + t.shift_end_time
               + case when t.shift_end_time <= t.shift_start_time then interval '1 day'
                      else interval '0' end as ends_at
        from public.trips t
       where t.vehicle_id = v_vehicle
         and t.trip_id <> p_trip_id
         and t.trip_status = 'Active'
       order by ends_at
       limit 1
    ) o;

  -- Cleared means inspected and drivable. A failed inspection is not an inspection for
  -- this purpose: it grounds the bus, and a later driver must not be offered a skip on
  -- the strength of it.
  select exists (
    select 1
      from public.bus_checklist c
      join public.trips ct on ct.trip_id = c.trip_id
     where ct.vehicle_id = v_vehicle
       and c.submitted_at >= v_day_wall
       and c.checklist_status::text in ('Passed', 'Passed with Defects', 'Skipped')
  ) into v_inspected;

  open_trip_id        := v_open_id;
  open_trip_ends_at   := v_open_ends;
  bus_inspected_today := v_inspected;
  return next;
end
$context$;

alter function public.shift_start_context(character varying) owner to postgres;

comment on function public.shift_start_context(character varying) is 'The open trip on this shift bus, when it is due to end, and whether the bus has been inspected in this operational day. Answers only for a trip the caller is driving, and never names another driver.';

-- The driver app is the only caller. The counter phone has no use for it and is not
-- granted it.
grant execute on function public.shift_start_context(character varying) to app_driver;

commit;
