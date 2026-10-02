-- Backs out 2026-10-02-fleetmap-live-since.sql.
--
-- Puts back the three-argument fleetmap_live. A dashboard that sends p_seen falls back to
-- calling it without, so it keeps working, at the old cost.

begin;

drop function if exists public.fleetmap_live(date, integer, timestamp with time zone, jsonb);

create or replace function public.fleetmap_live(
    p_op_day date,
    p_route_id integer,
    p_since timestamp with time zone)
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
        order by t."timestamp" desc
        limit 1000) x), '[]'),
    'fare', coalesce((select json_agg(f) from fare_config f), '[]'))
$$;

comment on function public.fleetmap_live(date, integer, timestamp with time zone) is
  'What the fleet map reads on every poll, in one request: active trips, recent positions of those shown, and the fare.';

revoke all on function public.fleetmap_live(date, integer, timestamp with time zone) from public;
grant all on function public.fleetmap_live(date, integer, timestamp with time zone) to service_role;

commit;

notify pgrst, 'reload schema';
