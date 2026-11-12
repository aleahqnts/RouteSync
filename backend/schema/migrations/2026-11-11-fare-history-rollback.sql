-- Puts revenue back on the current fare_config fare and removes the fare history.

DROP TRIGGER IF EXISTS "trg_fare_config_history" ON "public"."fare_config";
DROP FUNCTION IF EXISTS "public"."fare_config_history"();

CREATE OR REPLACE FUNCTION "public"."trips_count_guard"() RETURNS "trigger"
    LANGUAGE "plpgsql" SECURITY DEFINER
    SET "search_path" TO 'public'
    AS $$
declare
  v_claims      json;
  v_role        text;
  v_actor_type  text;
  v_actor_id    text;
  v_device      boolean;
  v_claim       integer;
  v_final       integer;
  v_fare        numeric(10,2);
  v_camera_live boolean;
  v_may_lower   boolean := false;
begin
  v_claims := nullif(current_setting('request.jwt.claims', true), '')::json;
  v_role   := coalesce(v_claims->>'role', 'db');
  v_device := v_role in ('app_camera', 'app_driver');

  v_claim := coalesce(NEW.total_boarded, OLD.total_boarded);

  if not v_device then
    -- The service key sets the figure literally, which is how an admin settles a count.
    v_final := v_claim;
  else
    -- Twelve seconds, matching HeartbeatStale in the driver app. The counter phone
    -- stamps every five, so a live camera is never mistaken for a dark one.
    v_camera_live := OLD.count_heartbeat is not null
                     and OLD.count_heartbeat > now() - interval '12 seconds';

    -- Only the driver, and only with no camera counting. The counter phone can never
    -- lower a figure: its own count is monotonic, so a lower one from it is stale.
    v_may_lower := v_role = 'app_driver' and not v_camera_live;

    v_final := case when v_may_lower then v_claim
                    else greatest(OLD.total_boarded, v_claim) end;
  end if;

  NEW.total_boarded := greatest(0, v_final);

  -- Revenue follows the count. A phone never authors this figure: the driver app sends
  -- one computed from whatever total it held, and the counter phone cannot write the
  -- column at all. A write through the service key naming its own revenue keeps it.
  if v_device
     or (NEW.total_boarded is distinct from OLD.total_boarded
         and NEW.estimated_revenue is not distinct from OLD.estimated_revenue) then
    select standard_fare into v_fare from public.fare_config where id = 1;
    NEW.estimated_revenue := round(NEW.total_boarded * coalesce(v_fare, 0), 2);
  end if;

  -- ------------------------------------------------------------------------
  -- Trail. Only the exceptional transitions, so a count climbing one passenger at a
  -- time does not bury the security events audit_log exists for.
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
  if v_device and not v_may_lower
     and v_claim is distinct from OLD.total_boarded
     and v_claim < OLD.total_boarded then
    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      (v_actor_type, v_actor_id, v_role, 'count_clamped', 'trips', NEW.trip_id,
       'db', 'ok',
       format('Trip %s: a count of %s was not applied because %s had already been counted.',
              NEW.trip_id, v_claim, OLD.total_boarded),
       jsonb_build_object('claimed', v_claim, 'total_boarded', NEW.total_boarded));
  end if;

  -- The driver taking the count down while no camera was counting. Permitted, and the
  -- only way a device can reduce a recorded figure, so it is written down.
  if v_may_lower and v_claim < OLD.total_boarded then
    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      (v_actor_type, v_actor_id, v_role, 'count_lowered', 'trips', NEW.trip_id,
       'db', 'ok',
       format('Trip %s: driver corrected the count from %s to %s while no camera was counting.',
              NEW.trip_id, OLD.total_boarded, v_claim),
       jsonb_build_object('was', OLD.total_boarded, 'total_boarded', NEW.total_boarded));
  end if;

  -- A count arriving after the trip closed, which is a counter phone reconciling from a
  -- dead zone.
  if v_device and OLD.trip_status = 'Completed'
     and NEW.total_boarded > OLD.total_boarded then
    insert into public.audit_log
      (actor_type, actor_id, actor_role, action, target_table, target_id,
       source, outcome, summary, changes)
    values
      (v_actor_type, v_actor_id, v_role, 'count_late_raise', 'trips', NEW.trip_id,
       'db', 'ok',
       format('Trip %s: count raised to %s after the trip was completed, previously %s.',
              NEW.trip_id, NEW.total_boarded, OLD.total_boarded),
       jsonb_build_object('claimed', v_claim, 'total_boarded', NEW.total_boarded));
  end if;

  return NEW;
end
$$;

DROP TABLE IF EXISTS "public"."fare_history";
