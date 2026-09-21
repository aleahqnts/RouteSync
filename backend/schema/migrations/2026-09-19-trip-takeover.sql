-- Trip takeover: one active trip per bus, and a record of every handover.
--
-- Background
--
-- Nothing currently stops two trips being Active on the same bus at once. The driver app
-- patches trip_status to 'Active' when a driver presses Start, with no check that the bus
-- is free, and no constraint refuses the second one. A morning driver who finishes the
-- route and forgets to press End leaves their trip Active, and it stays Active until the
-- stale trip closer reaches it a full day past its scheduled end. The afternoon driver
-- starts theirs in the meantime, and the bus is on two trips.
--
-- The dashboard already defends itself against this. The fleet map keeps one marker per
-- vehicle and positions it from the newest reading, precisely because a vehicle can appear
-- on more than one active trip in inconsistent data. The counter phone does not. It polls
-- for the Active trip on the vehicle it is bound to, takes the first row the answer happens
-- to contain, and has no ordering to make that choice stable. So it can count the
-- afternoon's passengers into the morning's trip, and when the row it settled on is closed
-- underneath it, it counts them nowhere at all.
--
-- The rule belongs here rather than in the driver app for the reason the count guard gives:
-- a rule enforced in an app is only true once every phone in the fleet has been updated,
-- and stops being true the moment one of them falls behind.
--
-- Rule
--
-- A trip moving to trip_status = 'Active' on a bus that already holds another Active trip
-- is a handover, and the handover is settled here rather than left to whoever reads the
-- table next.
--
-- Past its scheduled end, the standing trip is closed: trip_status becomes 'Completed' and
-- actual_end_time is stamped. The new trip then starts as asked.
--
-- Still inside its scheduled end, the new trip is refused with SQLSTATE RS409, the code
-- this schema already uses for a conflict with something somebody else is doing. A bus
-- cannot be on two trips, and the driver who arrives early is not the one who gets to
-- decide that a colleague's shift is over while that colleague is still driving. The
-- message names the bus and when the standing shift is due to end, and reaches the driver
-- app unchanged, which reports what the server said rather than a status code.
--
-- Scheduled end is the trip's own date plus shift_end_time, and a day more when
-- shift_end_time is at or before shift_start_time, which is what puts an overnight shift on
-- the following morning. It is the same expression the stale trip closer uses, and the two
-- have to agree: a trip one of them treats as finished and the other treats as running
-- would be closed by one rule and protected by the other.
--
-- The stamp is the moment of the handover, not the scheduled end. The stale trip closer
-- writes the scheduled end because it runs up to a day late, and its own clock would claim
-- the trip ran for as long as nobody noticed. This runs at the handover itself, so its
-- clock is the best record there is of when the bus actually changed hands.
--
-- The service key is exempt from the refusal, as it is exempt from the count guard. An
-- operator settling the board can always force the handover, and a forced close is recorded
-- exactly like any other.
--
-- The bus itself is left alone. It is On Trip before the handover and On Trip after it, and
-- the driver app marks it On Trip again immediately after the start it just made. The stale
-- trip closer releases the bus because nothing is taking its place; here something is.
--
-- Wall clock
--
-- This system stores Philippine wall-clock time in its timestamptz columns. The phones send
-- the local reading with no offset and the database records it as +00, so the instant such
-- a column holds is the wall clock rather than the moment it names. date and shift_end_time
-- are plain local values on that same clock.
--
-- Two expressions follow from that, and both are used below:
--
--   now() at time zone 'Asia/Manila'                        the wall clock, for comparing
--   (now() at time zone 'Asia/Manila') at time zone 'UTC'   the same reading, stored the
--                                                           way a phone stores it
--
-- The comparison against the scheduled end is made entirely in the first form, where both
-- sides are plain local values and no offset arithmetic happens at all. actual_end_time is
-- written in the second, so the stamp this trigger leaves and the stamp the driver app
-- leaves when a driver ends their own trip are the same kind of value, and can be read,
-- sorted and subtracted together. Writing now() directly would store an instant eight hours
-- behind every other time in the table, and every duration computed from it would be wrong
-- by a shift.
--
-- Trail
--
-- A close performed here writes one audit_log row with the action 'trip_taken_over', never
-- the action a driver ending their own trip would produce. That distinction is the point of
-- the row. A trip closed here was closed by somebody else's start, and reading it as a
-- driver's own finish would credit that driver with an end time they never entered, and
-- would hide the fact that a shift ran long enough to need taking over. The summary names
-- the closed trip, the bus and both drivers. The changes object carries both trip ids, both
-- driver ids, and whether the standing trip was past its scheduled end, which is what
-- separates an ordinary handover from an operator forcing one.
--
-- The row is filed against the closed trip, since that is the row whose state changed
-- without its own driver doing anything.
--
-- Actor derivation follows the count guard, so the two read the same way in the log: the
-- role from the request claims, the actor from user_id or device_id, the source 'db'.
--
-- What else fires on this table
--
-- trg_trips_count_guard also watches trip_status, so it runs on the same write. It finds no
-- count to move on a status change that carries none, reprices revenue from the unchanged
-- total, and writes nothing to the log. Firing order between the two does not matter: a
-- refusal here aborts the statement, and whatever the other trigger did goes with it.
--
-- trg_trips_roster_edit watches the driver, bus, route, shift, date and break of a roster
-- trip. A close touches none of those, so a roster trip closed at handover is not marked
-- hand edited and the next publish still owns it.
--
-- The close reaches a row the writing driver's own policy forbids them, which is why the
-- function is security definer. A driver may write only their own trips, and the trip being
-- closed belongs to their colleague.
--
-- What this gives up
--
-- The refusal is a hard stop with nothing behind it. A driver whose colleague has genuinely
-- gone home, with the bus in front of them and the old trip still inside its hours, cannot
-- start. They have to call dispatch, which holds the service key and can force it. That is
-- the intended answer: the alternative is a button that ends a moving bus's trip.
--
-- Trips already Active on a bus before this is deployed are not reconciled. The first start
-- on that bus resolves them, one at a time, and until then that bus keeps whatever count
-- the counter phone happened to land on.
--
-- A single statement moving several trips on one bus to Active at once is not handled. The
-- trigger would close a row the same statement is about to write, which the database
-- refuses. No client does this: every start arrives as a patch filtered on one trip id.

begin;

create or replace function public.trips_takeover_guard() returns trigger
    language plpgsql
    security definer
    set search_path to 'public'
as $takeover$
declare
  v_claims     json;
  v_role       text;
  v_actor_type text;
  v_actor_id   text;
  v_device     boolean;
  v_now_ph     timestamp;    -- the Philippine wall clock, as a plain local value
  v_wall       timestamptz;  -- the same reading, stored the way a phone stores it
  v_end        timestamp;    -- the standing trip's scheduled end, on the same clock
  v_past       boolean;
  v_summary    text;
  v_open       record;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');
  v_device := v_role in ('app_camera', 'app_driver');

  v_now_ph := now() at time zone 'Asia/Manila';
  v_wall   := v_now_ph at time zone 'UTC';

  v_actor_type := case v_role
    when 'app_driver'   then 'user'
    when 'app_camera'   then 'device'
    when 'service_role' then 'admin'
    when 'anon'         then 'anon'
    else 'system'
  end;
  v_actor_id := coalesce(v_claims->>'user_id', v_claims->>'device_id');

  -- Every other trip the bus is already on, oldest first, locked for the length of this
  -- statement. Two drivers pressing Start at the same moment queue here: the second one
  -- re-reads the row the first just closed, finds it no longer Active, and starts cleanly
  -- instead of closing it a second time.
  --
  -- A loop rather than a single lookup, because data written before this rule existed can
  -- already have a bus on more than one active trip, and settling only one of them would
  -- leave the bus in the state this exists to prevent.
  for v_open in
    select trip_id, driver_id, date, shift_start_time, shift_end_time
      from public.trips
     where vehicle_id = NEW.vehicle_id
       and trip_id <> NEW.trip_id
       and trip_status = 'Active'
     order by date, shift_start_time, trip_id
       for update
  loop
    -- The same rule as ScheduledEnd in the stale trip closer. An end at or before the
    -- start means the shift runs overnight, so it lands on the following day.
    v_end := v_open.date + v_open.shift_end_time
             + case when v_open.shift_end_time <= v_open.shift_start_time
                      then interval '1 day'
                      else interval '0 days'
               end;
    v_past := v_now_ph > v_end;

    -- Still driving. Only a privileged key may end somebody else's shift for them.
    if not v_past and v_device then
      raise exception
        'Bus % is still on trip %, which is due to end at %. Start again once that shift is over, or ask dispatch to close it.',
        NEW.vehicle_id, v_open.trip_id, to_char(v_end, 'DD Mon HH24:MI')
        using errcode = 'RS409';
    end if;

    update public.trips
       set trip_status     = 'Completed',
           actual_end_time = v_wall
     where trip_id = v_open.trip_id;

    if v_past then
      v_summary := format(
        'Trip %s on bus %s was closed at handover: driver %s left it active past its scheduled end of %s, and driver %s started trip %s on the same bus.',
        v_open.trip_id, NEW.vehicle_id, v_open.driver_id,
        to_char(v_end, 'DD Mon HH24:MI'), NEW.driver_id, NEW.trip_id);
    else
      v_summary := format(
        'Trip %s on bus %s was closed at handover before its scheduled end of %s: driver %s was still on it, and driver %s started trip %s on the same bus.',
        v_open.trip_id, NEW.vehicle_id, to_char(v_end, 'DD Mon HH24:MI'),
        v_open.driver_id, NEW.driver_id, NEW.trip_id);
    end if;

    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      (v_actor_type, v_actor_id, v_role, 'trip_taken_over', 'trips', v_open.trip_id,
       'db', 'ok', v_summary,
       jsonb_build_object(
         'closed_trip_id',     v_open.trip_id,
         'new_trip_id',        NEW.trip_id,
         'closed_driver_id',   v_open.driver_id,
         'new_driver_id',      NEW.driver_id,
         'vehicle_id',         NEW.vehicle_id,
         'scheduled_end',      to_char(v_end, 'YYYY-MM-DD HH24:MI'),
         'past_scheduled_end', v_past));
  end loop;

  return NEW;
end
$takeover$;

alter function public.trips_takeover_guard() owner to postgres;

revoke all on function public.trips_takeover_guard() from public, anon, authenticated;

comment on function public.trips_takeover_guard() is 'Holds a bus to one active trip. Closes a standing trip that is past its scheduled end when the next one starts, refuses the start while the standing trip is still inside its hours, and files the close in audit_log as trip_taken_over.';

-- Only a trip actually moving into Active enters the function. A write that leaves the
-- status where it is never reaches it, which covers the count the counter phone patches
-- every five seconds, its heartbeat, and the driver app resuming a trip already running.
create or replace trigger trg_trips_takeover_guard
  before update on public.trips
  for each row
  when (new.trip_status = 'Active' and old.trip_status is distinct from new.trip_status)
  execute function public.trips_takeover_guard();

comment on trigger trg_trips_takeover_guard on public.trips is 'Settles the handover when a trip starts on a bus that is already on another active trip.';

commit;
