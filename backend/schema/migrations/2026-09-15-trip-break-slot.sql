-- Trip break slot: when inside its eight hours a trip's driver takes the one hour break.
--
-- Background
--
-- A shift is eight hours: seven driving and one parked at the terminal. The hour is not
-- chosen by the driver. It is one of three fixed slots, starting three, four or five
-- hours after the shift does, so that a route's buses do not all park at once:
--
--   Morning    06:00  slots 09:00, 10:00, 11:00
--   Afternoon  14:00  slots 17:00, 18:00, 19:00
--   Evening    22:00  slots 01:00, 02:00, 03:00, on the calendar day after the trip's date
--
-- The slot is stored rather than derived, because who gets which depends on the other
-- trips that day and a dispatcher can move it. The dashboard picks it when a trip is
-- written; nothing here chooses it for a new row.
--
-- Nobody presses anything at the start or end of a break. The fleet map, the dispatch
-- board and the driver app read the slot and label the hour, and GPS reporting, the
-- location guard and the camera carry on as they do on the road.
--
-- Access
--
-- app_driver and app_camera already select every column of their own trips, so both see
-- the slot with no new grant. Neither may write it: only the service key does, which is
-- how the dashboard reaches the database.

begin;

-- ---------------------------------------------------------------------------
-- 1. Column
-- ---------------------------------------------------------------------------

alter table public.trips
  add column if not exists break_start time without time zone;

comment on column public.trips.break_start is
  'Philippine wall-clock start of the one hour break, 3, 4 or 5 hours after '
  'shift_start_time. Null on trips written before breaks existed.';

-- ---------------------------------------------------------------------------
-- 2. Only one of the three slots
--
-- Tied to the trip's own start time rather than to shift names, so a trip booked with
-- the standard windows and one booked by hand are held to the same rule. Adding an
-- interval to a time wraps past midnight, which is what puts an Evening slot at 01:00.
-- ---------------------------------------------------------------------------

alter table public.trips
  drop constraint if exists trips_break_start_in_shift;

alter table public.trips
  add constraint trips_break_start_in_shift check (
    break_start is null
    or break_start in (
      shift_start_time + interval '3 hours',
      shift_start_time + interval '4 hours',
      shift_start_time + interval '5 hours'));

-- ---------------------------------------------------------------------------
-- 3. Existing trips
--
-- Every trip from the current operational day on that has not finished gets a slot:
-- the route's buses on each shift and day take the three in turn, in bus order. That is
-- the spread the dashboard's least-used rule arrives at when it books the same trips one
-- at a time.
--
-- The operational day runs 06:00 to 05:59, so it is the Philippine date six hours ago.
-- A trip already running today is included, so the map can label its break this
-- afternoon. Trips before today keep a null slot and show nothing.
--
-- A slot already set is never overwritten, so running this a second time changes
-- nothing.
-- ---------------------------------------------------------------------------

with ranked as (
  select trip_id,
         shift_start_time,
         (row_number() over (
            partition by date, route_id, shift_type
            order by vehicle_id, trip_id) - 1) % 3 as slot
    from public.trips
   where date >= ((now() at time zone 'Asia/Manila') - interval '6 hours')::date
     and trip_status <> 'Completed'
)
update public.trips t
   set break_start = r.shift_start_time + make_interval(hours => 3 + r.slot::int)
  from ranked r
 where t.trip_id = r.trip_id
   and t.break_start is null;

commit;
