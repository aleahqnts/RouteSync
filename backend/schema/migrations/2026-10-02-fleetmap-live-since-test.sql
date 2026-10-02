-- Checks 2026-10-02-fleetmap-live-since.sql. Run it after the migration, in the Supabase
-- SQL editor. A failure raises; a pass prints one notice. Nothing is written.

do $test$
declare
  v_all  json;
  v_none json;
begin
  if to_regprocedure('public.fleetmap_live(date, integer, timestamp with time zone)') is not null then
    raise exception 'the old three-argument fleetmap_live is still there';
  end if;
  if to_regprocedure('public.fleetmap_live(date, integer, timestamp with time zone, jsonb)') is null then
    raise exception 'fleetmap_live with p_seen is missing';
  end if;

  -- Without p_seen, as an older dashboard calls it: the whole window.
  v_all := public.fleetmap_live(current_date, null, now() - interval '1 day');
  if v_all -> 'trips' is null or v_all -> 'telemetry' is null or v_all -> 'fare' is null then
    raise exception 'a part of the answer is missing: %', v_all;
  end if;

  -- Every shown trip marked as seen past any reading: nothing comes back.
  select public.fleetmap_live(current_date, null, now() - interval '1 day',
           coalesce(jsonb_object_agg(trip_id, 9223372036854775807), '{}'::jsonb))
    into v_none
    from trips where trip_status = 'Active';
  if json_array_length(v_none -> 'telemetry') <> 0 then
    raise exception 'readings before the cursor came back: %', json_array_length(v_none -> 'telemetry');
  end if;

  raise notice 'pass: % readings in the window, none past the cursor',
    json_array_length(v_all -> 'telemetry');
end
$test$;
