-- Trims two reads the dashboard repeats every few seconds.
--
-- fleetmap_live    Returned every column of each active trip and of each reading. It now
--                  returns only the columns the map draws from: a trip's ID, day, route,
--                  bus, driver, shift times, status, start and passengers, and a reading's
--                  position, speed, heading, accuracy, passengers and time.
-- dashboard_figures Returned every trip of yesterday on each poll, for the comparison with
--                  today. Yesterday no longer changes once its last shift has ended, so the
--                  dashboard now reads it on its own and keeps it. With p_yesterday_active_only
--                  set, only yesterday's trips still running come back here, which an
--                  overnight shift needs to be counted today.
--
-- Order: apply this before releasing the dashboard that passes p_yesterday_active_only. An
-- older dashboard calls without it and gets all of yesterday, as it does now.

begin;

create or replace function public.fleetmap_live(
    p_op_day date,
    p_route_id integer,
    p_since timestamp with time zone,
    p_seen jsonb default '{}'::jsonb)
returns json
language sql stable
set search_path to 'public'
as $$
  with active as (
    select trip_id, date, route_id, vehicle_id, driver_id, shift_start_time, shift_end_time,
           trip_status, actual_start_time, total_boarded
    from trips where trip_status = 'Active'
  ), shown as (
    select a.trip_id from active a
    where (p_route_id is null or a.route_id = p_route_id)
      and (a.date = p_op_day or (a.date = p_op_day - 1 and a.actual_start_time is not null))
  )
  select json_build_object(
    'trips', coalesce((select json_agg(a) from active a), '[]'),
    'telemetry', coalesce((select json_agg(x order by x."timestamp" desc) from (
        select t.telemetry_id, t.trip_id, t.latitude, t.longitude, t.total_passengers,
               t.speed, t.heading, t.accuracy, t."timestamp"
        from telemetry_data t
        where t.trip_id in (select trip_id from shown)
          and t."timestamp" >= p_since
          and t.telemetry_id >= coalesce((p_seen ->> t.trip_id)::bigint, 0)
        order by t."timestamp" desc
        limit 1000) x), '[]'),
    'fare', coalesce((select json_agg(f) from fare_config f), '[]'))
$$;

revoke all on function public.fleetmap_live(date, integer, timestamp with time zone, jsonb)
  from public, anon, authenticated;
grant all on function public.fleetmap_live(date, integer, timestamp with time zone, jsonb)
  to service_role;

drop function if exists public.dashboard_figures(date);

create or replace function public.dashboard_figures(
    p_today date,
    p_yesterday_active_only boolean default false)
returns json
language sql stable
set search_path to 'public'
as $$
  select json_build_object(
    'open_logs', coalesce((select json_agg(x) from (
        select log_id, vehicle_id from maintenance_logs
        where resolved_at is null) x), '[]'),
    'today_trips', coalesce((select json_agg(x) from (
        select trip_id, date, route_id, vehicle_id, shift_type, shift_start_time, shift_end_time,
               trip_status, estimated_revenue, total_boarded, actual_end_time
        from trips where date = p_today) x), '[]'),
    'yesterday_trips', coalesce((select json_agg(x) from (
        select trip_id, date, route_id, vehicle_id, shift_type, shift_start_time, shift_end_time,
               trip_status, estimated_revenue, total_boarded, actual_end_time
        from trips
        where date = p_today - 1
          and (not p_yesterday_active_only or trip_status = 'Active')) x), '[]'),
    'routes', coalesce((select json_agg(x order by x.route_name) from (
        select route_id, route_name from routes) x), '[]'))
$$;

comment on function public.dashboard_figures(date, boolean) is
  'The rows the dashboard''s cards are worked out from, in one request. With p_yesterday_active_only, only yesterday''s trips still running.';

revoke all on function public.dashboard_figures(date, boolean)
  from public, anon, authenticated;
grant all on function public.dashboard_figures(date, boolean) to service_role;

commit;

notify pgrst, 'reload schema';
