-- Adds trip_active_poll: what the driver's running trip screen checks every five seconds,
-- in one request.
--
-- The screen used to read the whole trip row every five seconds for the camera's count
-- and heartbeat, the counter phone's status every five seconds, and the trip and its bus
-- again every fifteen for a reassignment: eight requests every fifteen seconds for each
-- bus on the road. This answers all of it in one, with only the columns the screen reads:
--
--   {"trip": {...} | null, "counter_device_id": text | null, "device": {...} | null}
--
-- It runs as the caller, so the driver's row policies decide what comes back exactly as
-- they did for the separate reads: a trip that is not theirs comes back null, and the
-- counter phone's status only while their trip is running. Only the driver role may call it.

begin;

create or replace function public.trip_active_poll(p_trip_id character varying)
returns json
language sql
stable
security invoker
set search_path to 'public'
as $$
  with t as (
    select trip_id, trip_status, route_id, vehicle_id, break_start, total_boarded,
           estimated_revenue, count_heartbeat, actual_end_time
    from trips
    where trip_id = p_trip_id
  ), v as (
    select vh.counter_device_id
    from vehicles vh
    join t on vh.vehicle_id = t.vehicle_id
  )
  select json_build_object(
    'trip', (select to_json(t) from t),
    'counter_device_id', (select counter_device_id from v),
    'device', (select to_json(d) from (
        select device_id, last_seen, battery_level, battery_charging
        from device_status
        where device_id = (select counter_device_id from v)) d))
$$;

comment on function public.trip_active_poll(character varying) is
  'The driver''s running trip, its bus''s counter phone and that phone''s status, in one request. Runs as the caller, so row policies apply.';

revoke all on function public.trip_active_poll(character varying) from public, anon, authenticated;
grant execute on function public.trip_active_poll(character varying) to app_driver;

commit;

notify pgrst, 'reload schema';
