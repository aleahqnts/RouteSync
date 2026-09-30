-- One request for each of the three things the web dashboard asks for every few seconds.
--
-- Background
--
-- Every request the API answers is logged, and the log is metered by volume, so what a
-- page costs is how many requests it makes, not how much each one carries. Three polls ran
-- all day on every open page, and each made several separate reads:
--
--   the sidebar badges, every 5 seconds on every page    about 13 reads
--   the dashboard's cards, every 5 seconds               4 reads
--   the fleet map's buses, every 2 seconds               3 reads
--
-- An open dashboard made about four requests a second, and a working day with one left open
-- wrote hundreds of megabytes of logs.
--
-- Model
--
-- Each poll becomes one call. Each function returns one JSON object holding, under its own
-- name, exactly the rows each separate read used to fetch: the same columns and the same
-- filters. The dashboard reads them into the same models and works out the same figures,
-- so what is shown does not change, only how many requests it takes to show it. How often
-- each page asks is unchanged.
--
--   nav_badge_inputs(...)   the rows the sidebar badges are counted from. The roster and
--                           security incident sections are each read on their own and come
--                           back null if they cannot be, so one table that cannot be read
--                           takes down its own badge rather than the whole sidebar.
--   dashboard_figures(...)  the rows the dashboard's cards are worked out from.
--   fleetmap_live(...)      the active trips, their recent positions and the fare, which is
--                           what the fleet map reads on every poll. The lists that change
--                           rarely, such as buses, routes and staff, are still read apart
--                           and reused for a minute.
--
-- Positions keep the limit of a thousand rows, newest first, that the separate read had
-- from the API, so a poll carries no more than it did.
--
-- Access
--
-- For the web dashboard only, which holds the service key. Each runs as its caller and no
-- other role may call it.

begin;

-- ---------------------------------------------------------------------------
-- 1. Sidebar badges
-- ---------------------------------------------------------------------------

create or replace function public.nav_badge_inputs(
  p_today          date,
  p_this_month     date,
  p_next_month     date,
  p_open_statuses  text[])
returns json
language plpgsql
stable
set search_path = public
as $$
declare
  v_roster    json;
  v_incidents json;
begin
  begin
    v_roster := json_build_object(
      'months', coalesce((select json_agg(x) from (
          select month, status from roster_months
          where month in (p_this_month, p_next_month)) x), '[]'),
      'gaps', coalesce((select json_agg(x) from (
          select date, vehicle_id, shift from roster_gaps
          where date >= p_today + 1) x), '[]'),
      'skips', coalesce((select json_agg(x) from (
          select date, vehicle_id, shift from roster_skips
          where date >= p_today + 1) x), '[]'),
      'slots', coalesce((select json_agg(x) from (
          select month, driver_id, vehicle_id, route_id, shift, suggested from roster_slots
          where month in (p_this_month, p_next_month)) x), '[]'),
      -- Trips already running on the days that have gaps, which fill them.
      'gap_trips', coalesce((select json_agg(x) from (
          select date, vehicle_id, shift_type from trips
          where date in (select g.date from roster_gaps g where g.date >= p_today + 1)) x), '[]'),
      -- The drivers and buses placed on this month's roster, to find places that no longer hold.
      'slot_drivers', coalesce((select json_agg(x) from (
          select user_id, account_status from users
          where user_id in (select s.driver_id from roster_slots s
                            where s.month = p_this_month and s.driver_id is not null)) x), '[]'),
      'slot_buses', coalesce((select json_agg(x) from (
          select vehicle_id, retired_at, route_id from vehicles
          where vehicle_id in (select s.vehicle_id from roster_slots s
                               where s.month = p_this_month and s.vehicle_id is not null)) x), '[]'));
  exception when others then
    v_roster := null;
  end;

  begin
    v_incidents := coalesce((select json_agg(x) from (
        select severity from security_incidents
        where needs_review is true
        limit 1000) x), '[]');
  exception when others then
    v_incidents := null;
  end;

  return json_build_object(
    'trips', coalesce((select json_agg(x) from (
        select trip_id, date, vehicle_id, driver_id, trip_status, shift_start_time, shift_end_time
        from trips
        where date >= p_today and date <= p_today + 1) x), '[]'),
    'vehicles', coalesce((select json_agg(x) from (
        select vehicle_id, out_of_service, retired_at from vehicles) x), '[]'),
    'availability', coalesce((select json_agg(x) from (
        select user_id, availability_status from driver_availability) x), '[]'),
    'open_logs', coalesce((select json_agg(x) from (
        select log_id, vehicle_id from maintenance_logs
        where resolved_at is null) x), '[]'),
    'leave_open', coalesce((select json_agg(x) from (
        select request_id, user_id, status, leave_type, start_date, end_date, revoked_dates
        from leave_requests
        where status = any (p_open_statuses)) x), '[]'),
    'leave_asked', coalesce((select json_agg(x) from (
        select request_id from leave_requests
        where withdraw_requested_at is not null
          and withdraw_answered_at is null
          and status = 'Approved') x), '[]'),
    'leave_today', coalesce((select json_agg(x) from (
        select request_id, user_id, status, start_date, end_date, revoked_dates
        from leave_requests
        where status = 'Approved' and start_date <= p_today and end_date >= p_today) x), '[]'),
    'roster', v_roster,
    'incidents', v_incidents);
end
$$;

comment on function public.nav_badge_inputs(date, date, date, text[]) is
  'The rows the sidebar badges are counted from, in one request. roster and incidents are null when their tables cannot be read.';

revoke all on function public.nav_badge_inputs(date, date, date, text[]) from public;
revoke all on function public.nav_badge_inputs(date, date, date, text[]) from anon, authenticated;
grant execute on function public.nav_badge_inputs(date, date, date, text[]) to service_role;

-- ---------------------------------------------------------------------------
-- 2. Dashboard cards
-- ---------------------------------------------------------------------------

create or replace function public.dashboard_figures(p_today date)
returns json
language sql
stable
set search_path = public
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

revoke all on function public.dashboard_figures(date) from public;
revoke all on function public.dashboard_figures(date) from anon, authenticated;
grant execute on function public.dashboard_figures(date) to service_role;

-- ---------------------------------------------------------------------------
-- 3. Fleet map
-- ---------------------------------------------------------------------------

-- p_route_id narrows the positions to one route's trips, as the map's filter does. The
-- trips themselves come back whole, every active one, and are narrowed by the caller.
create or replace function public.fleetmap_live(
  p_op_day    date,
  p_route_id  integer,
  p_since     timestamptz)
returns json
language sql
stable
set search_path = public
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

comment on function public.fleetmap_live(date, integer, timestamptz) is
  'What the fleet map reads on every poll, in one request: active trips, recent positions of those shown, and the fare.';

revoke all on function public.fleetmap_live(date, integer, timestamptz) from public;
revoke all on function public.fleetmap_live(date, integer, timestamptz) from anon, authenticated;
grant execute on function public.fleetmap_live(date, integer, timestamptz) to service_role;

commit;

notify pgrst, 'reload schema';
