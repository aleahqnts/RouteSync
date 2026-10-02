-- Backs out 2026-10-02-leaner-reads.sql: fleetmap_live returns whole rows again, and
-- dashboard_figures goes back to one argument. A dashboard that passes
-- p_yesterday_active_only falls back to calling without it.

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
    select * from trips where trip_status = 'Active'
  ), shown as (
    select a.trip_id from active a
    where (p_route_id is null or a.route_id = p_route_id)
      and (a.date = p_op_day or (a.date = p_op_day - 1 and a.actual_start_time is not null))
  )
  select json_build_object(
    'trips', coalesce((select json_agg(a) from active a), '[]'),
    'telemetry', coalesce((select json_agg(x order by x."timestamp" desc) from (
        select t.* from telemetry_data t
        where t.trip_id in (select trip_id from shown)
          and t."timestamp" >= p_since
          and t.telemetry_id >= coalesce((p_seen ->> t.trip_id)::bigint, 0)
        order by t."timestamp" desc
        limit 1000) x), '[]'),
    'fare', coalesce((select json_agg(f) from fare_config f), '[]'))
$$;

drop function if exists public.dashboard_figures(date, boolean);

create or replace function public.dashboard_figures(p_today date)
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
        from trips where date = p_today - 1) x), '[]'),
    'routes', coalesce((select json_agg(x order by x.route_name) from (
        select route_id, route_name from routes) x), '[]'))
$$;

comment on function public.dashboard_figures(date) is
  'The rows the dashboard''s cards are worked out from, in one request.';

revoke all on function public.dashboard_figures(date) from public, anon, authenticated;
grant all on function public.dashboard_figures(date) to service_role;

commit;

notify pgrst, 'reload schema';
