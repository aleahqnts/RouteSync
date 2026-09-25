-- Closed trip guard: once a trip is completed, the driver app can no longer change it.
--
-- Background
--
-- A trip can be closed without its driver doing anything. The takeover guard closes it when
-- the next shift starts on the same bus, and the stale trip closer closes it a day past its
-- scheduled end. The driver app that was counting it was never told. It went on showing the
-- trip as running, with the manual counter out because the counter phone had moved to the
-- new trip, and every write it made still reached the closed row: the running count every
-- fifteen seconds, and in the end the driver's own End, which replaced the handover stamp
-- with a later end time and a count that included taps made after the bus changed hands.
-- The End's last step then set the bus to Ready to Deploy with the next driver out on the
-- road in it.
--
-- The driver app now notices a closed trip and stops counting. This is the same rule in the
-- database, where it also holds for every build already installed on a phone.
--
-- Rule
--
-- A write from the driver app to a trip that is already Completed is dropped. The trigger
-- returns null, which leaves the row exactly as it was and stops the later triggers from
-- running for it. The request still succeeds, having changed nothing. A refusal would be
-- worse: a phone holding a finalization queued offline sends it until it is accepted, so a
-- refused one would be sent forever and hold up every finalization queued behind it.
--
-- Every trip column the driver key may write is covered by that: trip_status,
-- actual_start_time, actual_end_time, total_boarded and estimated_revenue. So is a start, which would otherwise reopen the trip and close the next
-- driver's in turn through the takeover guard.
--
-- The counter phone is not covered. A count it made in a dead zone and delivers after the
-- trip closed is still a real count of that trip, which the count guard accepts and files as
-- count_late_raise. The service key is not covered either, since an admin correcting a closed
-- trip is the one write that should reach it.
--
-- Trail
--
-- A driver's End that arrives after the trip closed is filed in audit_log as
-- trip_end_after_close, against the trip, with the end time and count the phone sent beside
-- the ones the trip kept. That is the row that explains a driver saying they ended at one
-- time while the board shows another. The running count the phone sends every fifteen seconds
-- is dropped without a row, since a phone out of date for a few minutes would otherwise write
-- a dozen rows that say nothing new.
--
-- Releasing the bus
--
-- A driver may not change the status of a bus that another driver has an active trip on.
-- The update is dropped the same way, so the late End's release, which the phone sends
-- without reading the answer, succeeds and leaves the bus On Trip. The driver on the active
-- trip is unaffected, so the start that marks the bus On Trip still lands. The function is
-- security definer because a driver key can read only its own trips, and the trip that
-- matters here belongs to a colleague.
--
-- Trigger order
--
-- Before-update triggers on one table fire in name order. trg_trips_closed_guard sorts ahead
-- of trg_trips_count_guard, trg_trips_roster_edit and trg_trips_takeover_guard, so a dropped
-- write never reaches them. That matters most for the count guard, which would reprice the
-- revenue on a write it saw and could file it as a correction.
--
-- What this gives up
--
-- A driver who ends a trip while offline, and whose phone stays offline until the stale trip
-- closer has closed that trip a day later, loses their End. The trip keeps the scheduled end
-- the closer stamped and the count it held at the last sync.

begin;

-- ---------------------------------------------------------------------------
-- 1. Trips
-- ---------------------------------------------------------------------------

create or replace function public.trips_closed_guard() returns trigger
    language plpgsql
    security definer
    set search_path to 'public'
as $closed$
declare
  v_claims json;
  v_role   text;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');

  if v_role <> 'app_driver' then
    return NEW;
  end if;

  -- Only the driver's End is worth a row. It is the write that carries a completed status
  -- and an end time of its own.
  if NEW.trip_status = 'Completed'
     and NEW.actual_end_time is distinct from OLD.actual_end_time then
    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      ('user', v_claims->>'user_id', v_role, 'trip_end_after_close', 'trips', OLD.trip_id,
       'db', 'ok',
       format('Trip %s: driver %s ended it on their phone after it had already closed. The trip keeps its end time and a count of %s; the phone sent a count of %s.',
              OLD.trip_id, OLD.driver_id, OLD.total_boarded, NEW.total_boarded),
       jsonb_build_object(
         'kept_end_time',   OLD.actual_end_time,
         'sent_end_time',   NEW.actual_end_time,
         'kept_total',      OLD.total_boarded,
         'sent_total',      NEW.total_boarded,
         'vehicle_id',      OLD.vehicle_id));
  end if;

  return null;
end
$closed$;

alter function public.trips_closed_guard() owner to postgres;

revoke all on function public.trips_closed_guard() from public, anon, authenticated;

comment on function public.trips_closed_guard() is 'Drops any write from the driver app to a trip that is already Completed, and files a late End as trip_end_after_close.';

create or replace trigger trg_trips_closed_guard
  before update on public.trips
  for each row
  when (old.trip_status = 'Completed')
  execute function public.trips_closed_guard();

comment on trigger trg_trips_closed_guard on public.trips is 'Keeps a completed trip as it was closed, whatever a driver phone still holding it sends.';

-- ---------------------------------------------------------------------------
-- 2. Vehicles
-- ---------------------------------------------------------------------------

create or replace function public.vehicles_release_guard() returns trigger
    language plpgsql
    security definer
    set search_path to 'public'
as $release$
declare
  v_claims json;
  v_uid    integer;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;

  if coalesce(v_claims->>'role', 'db') <> 'app_driver' then
    return NEW;
  end if;

  v_uid := nullif(v_claims->>'user_id', '')::integer;

  if exists (select 1
               from public.trips t
              where t.vehicle_id = NEW.vehicle_id
                and t.trip_status = 'Active'
                and t.driver_id is distinct from v_uid) then
    return null;
  end if;

  return NEW;
end
$release$;

alter function public.vehicles_release_guard() owner to postgres;

revoke all on function public.vehicles_release_guard() from public, anon, authenticated;

comment on function public.vehicles_release_guard() is 'Drops a driver app change to the status of a bus that another driver has an active trip on.';

create or replace trigger trg_vehicles_release_guard
  before update on public.vehicles
  for each row
  when (old.vehicle_status is distinct from new.vehicle_status)
  execute function public.vehicles_release_guard();

comment on trigger trg_vehicles_release_guard on public.vehicles is 'Stops a driver phone from releasing a bus somebody else is driving.';

commit;
