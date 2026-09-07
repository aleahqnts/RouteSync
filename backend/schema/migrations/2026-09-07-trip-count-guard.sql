-- Trip count guard: derive trips.total_boarded rather than letting two phones write it.
--
-- Background
--
-- trips.total_boarded has two writers that cannot coordinate. The counter phone
-- (app_camera) patches it every five seconds while it counts, and the driver app
-- (app_driver) writes it again when the trip is finalized. Either surface can be out of
-- contact for the length of a route, so both can hold a figure the other has never
-- seen. The write that reconnected last won outright, which meant a count made in a
-- dead zone could be replaced by a stale figure from the other surface.
--
-- Model
--
-- The reported figure is no longer written by anyone. A phone's total_boarded is read
-- as a claim about how many passengers the machine has counted. The high-water mark of
-- those claims is held in boarded_counted, a driver's manual correction is held in
-- boarded_adjustment, and total_boarded is the sum of the two. Every existing reader
-- continues to select total_boarded and needs no change.
--
-- The rule lives in the database rather than in the clients because the counter phone
-- and the driver app are separate Android packages on separate release cycles. A rule
-- enforced in an app is only true once every phone in the fleet has been updated, and
-- stops being true the moment one of them falls behind. Enforced here it is true for
-- every client at once, including builds already in the field.
--
-- The service key is exempt. An admin settling a disputed count has to be able to lower
-- it, and every other privileged correction in this system goes through that key.
--
-- Revenue is derived alongside the count. The counter phone has no grant on
-- estimated_revenue and never maintained it, so a count raised after a trip closed used
-- to leave revenue priced from the lower figure, which the reports then summed.

begin;

-- ---------------------------------------------------------------------------
-- 1. Columns
-- ---------------------------------------------------------------------------

alter table public.trips
  add column if not exists boarded_counted    integer not null default 0,
  add column if not exists boarded_adjustment integer not null default 0;

comment on column public.trips.boarded_counted is
  'High-water mark of the machine counts claimed by the counter phone or the driver '
  'app. Only an admin correction through the service key can lower it.';

comment on column public.trips.boarded_adjustment is
  'Net manual correction entered by the driver, negative where the camera over-counted. '
  'Added to boarded_counted to give total_boarded.';

-- Existing rows carry their stored total as the counted figure, so the identity
-- total_boarded = boarded_counted + boarded_adjustment holds from the first write.
update public.trips
   set boarded_counted = total_boarded
 where boarded_counted = 0
   and total_boarded <> 0;

-- The driver app owns the correction. The counter phone is refused it here and in the
-- guard below, because a machine has nothing to correct.
grant update ("boarded_adjustment") on public.trips to app_driver;

-- ---------------------------------------------------------------------------
-- 2. Guard
-- ---------------------------------------------------------------------------

create or replace function public.trips_count_guard() returns trigger
    language plpgsql
    security definer
    set search_path to 'public'
as $guard$
declare
  v_claims     json;
  v_role       text;
  v_actor_type text;
  v_actor_id   text;
  v_device     boolean;
  v_claim      integer;   -- the machine count asserted by this write
  v_counted    integer;   -- the high-water mark after it
  v_adjust     integer;
  v_final      integer;
  v_fare       numeric(10,2);
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');
  v_device := v_role in ('app_camera', 'app_driver');

  -- The incoming figure, read before anything below rewrites it.
  v_claim := coalesce(NEW.total_boarded, OLD.total_boarded);

  if v_device then
    v_counted := greatest(OLD.boarded_counted, v_claim);
    -- Only the driver app may move the correction. The counter phone holds no grant on
    -- the column, and is held to the same rule here so the guard states it outright.
    v_adjust  := case
                   when v_role = 'app_driver'
                     then coalesce(NEW.boarded_adjustment, OLD.boarded_adjustment)
                   else OLD.boarded_adjustment
                 end;
  else
    -- The service key sets the reported figure literally. The counted figure is rebased
    -- underneath it so that later writes from a phone compose with the correction
    -- instead of undoing it.
    v_adjust  := coalesce(NEW.boarded_adjustment, OLD.boarded_adjustment);
    v_counted := case
                   when v_claim is distinct from OLD.total_boarded
                     then v_claim - v_adjust
                   else greatest(OLD.boarded_counted,
                                 coalesce(NEW.boarded_counted, OLD.boarded_counted))
                 end;
  end if;

  v_final := greatest(0, v_counted + v_adjust);

  NEW.boarded_counted    := v_counted;
  NEW.boarded_adjustment := v_adjust;
  NEW.total_boarded      := v_final;

  -- Revenue follows the count it is priced from. A phone never authors this figure: the
  -- driver app sends one computed from whatever total it was holding, which is the
  -- value being corrected here, and the counter phone cannot write the column at all.
  -- A write through the service key that names a revenue of its own keeps it.
  if v_device
     or (v_final is distinct from OLD.total_boarded
         and NEW.estimated_revenue is not distinct from OLD.estimated_revenue) then
    select standard_fare into v_fare from public.fare_config where id = 1;
    NEW.estimated_revenue := round(v_final * coalesce(v_fare, 0), 2);
  end if;

  -- ------------------------------------------------------------------------
  -- Trail. Only the exceptional transitions are recorded. A count climbing one
  -- passenger at a time explains nothing and would bury the security events that
  -- audit_log exists for, and telemetry_data.total_passengers already carries the
  -- per-boarding timeline for any trip that needs reconstructing.
  -- ------------------------------------------------------------------------

  v_actor_type := case v_role
    when 'app_driver'   then 'user'
    when 'app_camera'   then 'device'
    when 'service_role' then 'admin'
    when 'anon'         then 'anon'
    else 'system'
  end;
  v_actor_id := coalesce(v_claims->>'user_id', v_claims->>'device_id');

  -- A surface that has been out of contact sending a figure from before the divergence.
  if v_device
     and v_claim is distinct from OLD.total_boarded
     and v_claim < OLD.boarded_counted then
    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      (v_actor_type, v_actor_id, v_role, 'count_clamped', 'trips', NEW.trip_id,
       'db', 'ok',
       format('Trip %s: a count of %s was not applied because %s had already been counted.',
              NEW.trip_id, v_claim, OLD.boarded_counted),
       jsonb_build_object('claimed', v_claim, 'counted', v_counted,
                          'adjustment', v_adjust, 'total_boarded', v_final));
  end if;

  -- A count that arrived after the trip closed, which is what a counter phone
  -- reconciling from a dead zone looks like.
  if v_device
     and OLD.trip_status = 'Completed'
     and v_counted > OLD.boarded_counted then
    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      (v_actor_type, v_actor_id, v_role, 'count_late_raise', 'trips', NEW.trip_id,
       'db', 'ok',
       format('Trip %s: count raised to %s after the trip was completed, previously %s.',
              NEW.trip_id, v_counted, OLD.boarded_counted),
       jsonb_build_object('claimed', v_claim, 'counted', v_counted,
                          'adjustment', v_adjust, 'total_boarded', v_final));
  end if;

  -- A driver correcting the machine count by hand.
  if v_adjust is distinct from OLD.boarded_adjustment then
    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      (v_actor_type, v_actor_id, v_role, 'count_adjusted', 'trips', NEW.trip_id,
       'db', 'ok',
       format('Trip %s: driver correction changed from %s to %s, reported total %s.',
              NEW.trip_id, OLD.boarded_adjustment, v_adjust, v_final),
       jsonb_build_object('claimed', v_claim, 'counted', v_counted,
                          'adjustment', v_adjust, 'total_boarded', v_final));
  end if;

  return NEW;
end
$guard$;

alter function public.trips_count_guard() owner to postgres;

-- A write that touches none of these columns, such as the heartbeat the counter phone
-- sends every five seconds, skips the guard entirely.
create or replace trigger trg_trips_count_guard
  before update on public.trips
  for each row
  when (
       old.total_boarded      is distinct from new.total_boarded
    or old.boarded_counted    is distinct from new.boarded_counted
    or old.boarded_adjustment is distinct from new.boarded_adjustment
    or old.estimated_revenue  is distinct from new.estimated_revenue
    or old.trip_status        is distinct from new.trip_status
  )
  execute function public.trips_count_guard();

commit;
