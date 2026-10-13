-- Lets nav_badge_inputs return part of its answer: 'fast', 'slow' or 'all' (the default).
--
-- The sidebar badges are read every five seconds. The trips, buses, availability, open
-- faults and security incidents are the 'fast' part and stay on that pace. The leave
-- requests and the roster change at human pace and are the larger half of the answer,
-- so they are the 'slow' part, read once a minute. A section left out comes back null.
--
-- 'all' answers exactly as the function did before, so the dashboard running now keeps
-- working once this is applied. Apply it before the code that passes p_sections.

begin;

drop function if exists public.nav_badge_inputs(date, date, date, text[]);

create function public.nav_badge_inputs(
  p_today date, p_this_month date, p_next_month date, p_open_statuses text[],
  p_sections text default 'all')
returns json
language plpgsql
stable
set search_path to 'public'
as $$
declare
  v_fast      boolean := p_sections in ('all', 'fast');
  v_slow      boolean := p_sections in ('all', 'slow');
  v_roster    json;
  v_incidents json;
begin
  if v_slow then
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
  end if;

  if v_fast then
    begin
      v_incidents := coalesce((select json_agg(x) from (
          select severity from security_incidents
          where needs_review is true
          limit 1000) x), '[]');
    exception when others then
      v_incidents := null;
    end;
  end if;

  return json_build_object(
    'trips', case when v_fast then coalesce((select json_agg(x) from (
        select trip_id, date, vehicle_id, driver_id, trip_status, shift_start_time, shift_end_time
        from trips
        where date >= p_today and date <= p_today + 1) x), '[]') end,
    'vehicles', case when v_fast then coalesce((select json_agg(x) from (
        select vehicle_id, out_of_service, retired_at from vehicles) x), '[]') end,
    'availability', case when v_fast then coalesce((select json_agg(x) from (
        select user_id, availability_status from driver_availability) x), '[]') end,
    'open_logs', case when v_fast then coalesce((select json_agg(x) from (
        select log_id, vehicle_id from maintenance_logs
        where resolved_at is null) x), '[]') end,
    'leave_open', case when v_slow then coalesce((select json_agg(x) from (
        select request_id, user_id, status, leave_type, start_date, end_date, revoked_dates
        from leave_requests
        where status = any (p_open_statuses)) x), '[]') end,
    'leave_asked', case when v_slow then coalesce((select json_agg(x) from (
        select request_id from leave_requests
        where withdraw_requested_at is not null
          and withdraw_answered_at is null
          and status = 'Approved') x), '[]') end,
    'leave_today', case when v_slow then coalesce((select json_agg(x) from (
        select request_id, user_id, status, start_date, end_date, revoked_dates
        from leave_requests
        where status = 'Approved' and start_date <= p_today and end_date >= p_today) x), '[]') end,
    'roster', v_roster,
    'incidents', v_incidents);
end
$$;

comment on function public.nav_badge_inputs(date, date, date, text[], text) is
  'The rows the sidebar badges are counted from, in one request. p_sections picks fast (trips, vehicles, availability, open_logs, incidents), slow (leave and roster) or all; a section left out is null. roster and incidents are also null when their tables cannot be read.';

revoke all on function public.nav_badge_inputs(date, date, date, text[], text) from public, anon, authenticated;
grant execute on function public.nav_badge_inputs(date, date, date, text[], text) to service_role;

commit;

notify pgrst, 'reload schema';
