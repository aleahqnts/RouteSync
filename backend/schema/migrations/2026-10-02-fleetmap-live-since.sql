-- Lets the fleet map ask only for the GPS readings it has not used yet.
--
-- fleetmap_live returned every reading from the last thirty minutes for each bus on the
-- map, up to a thousand rows, on every two-second poll. The map uses each reading once, so
-- all but the newest one or two were sent again on every poll and thrown away.
--
-- p_seen names, per trip, the ID of the last reading the map used. Only that reading and
-- the ones after it come back: the newest for the marker's details, and anything new for
-- the route snapper. A trip not named gets its whole recent window, as before, which is
-- what the first poll after the dashboard starts asks for.
--
-- Order: apply this before releasing the dashboard that sends p_seen. An older dashboard
-- calls without it and gets the whole window, as it does now.

begin;

drop function if exists public.fleetmap_live(date, integer, timestamp with time zone);

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

comment on function public.fleetmap_live(date, integer, timestamp with time zone, jsonb) is
  'What the fleet map reads on every poll, in one request: active trips, the positions of those shown from the last one the map used (p_seen, by trip), and the fare.';

revoke all on function public.fleetmap_live(date, integer, timestamp with time zone, jsonb) from public;
grant all on function public.fleetmap_live(date, integer, timestamp with time zone, jsonb) to service_role;

commit;

notify pgrst, 'reload schema';
