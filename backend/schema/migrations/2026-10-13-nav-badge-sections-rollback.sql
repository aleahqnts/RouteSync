-- Backs out 2026-10-13-nav-badge-sections.sql: puts back the four-argument function that
-- always answers in full. A dashboard that passes p_sections notices the function is gone
-- and reads everything every five seconds, as before.

begin;

drop function if exists public.nav_badge_inputs(date, date, date, text[], text);

create function public.nav_badge_inputs(p_today date, p_this_month date, p_next_month date, p_open_statuses text[])
returns json
language plpgsql
stable
set search_path to 'public'
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
      'gap_trips', coalesce((select json_agg(x) from (
          select date, vehicle_id, shift_type from trips
          where date in (select g.date from roster_gaps g where g.date >= p_today + 1)) x), '[]'),
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

revoke all on function public.nav_badge_inputs(date, date, date, text[]) from public, anon, authenticated;
grant execute on function public.nav_badge_inputs(date, date, date, text[]) to service_role;

commit;

notify pgrst, 'reload schema';
