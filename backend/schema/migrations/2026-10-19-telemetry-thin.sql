-- Adds telemetry_thin: older readings cut down to what anything still reads.
--
-- The phones report every five seconds while a bus moves, which is what the live map
-- needs. Past the first two days nothing reads that detail: learning stop-to-stop speeds
-- takes the first reading in each thirty seconds (telemetry_sample), and the passenger
-- history needs only the readings where the count changed. Fifteen days of every reading
-- would take most of the free plan's 500 MB at full service; thinned, about a third.
--
-- For readings older than p_before, each trip keeps the first reading in each
-- p_every_seconds window and every reading whose passenger count differs from the one
-- before it. The windows are the same ones telemetry_sample uses, so a trip sampled at
-- the same interval returns the same readings before and after thinning. Running it again
-- removes nothing more.
--
-- Returns how many readings were removed, not the readings. Only the dashboard's service
-- role may call it.

begin;

create or replace function public.telemetry_thin(p_before timestamptz, p_every_seconds integer default 30)
returns integer
language sql
volatile
set search_path to 'public'
as $$
  with ranked as (
    select t.telemetry_id,
           row_number() over (
             partition by t.trip_id, floor(extract(epoch from t."timestamp") / greatest(p_every_seconds, 1))
             order by t."timestamp", t.telemetry_id) as nth,
           t.total_passengers is distinct from lag(t.total_passengers) over (
             partition by t.trip_id
             order by t."timestamp", t.telemetry_id) as count_changed
    from telemetry_data t
    where t."timestamp" < p_before
  ), gone as (
    delete from telemetry_data d
    using ranked r
    where d.telemetry_id = r.telemetry_id
      and r.nth > 1
      and not r.count_changed
    returning 1
  )
  select count(*)::integer from gone
$$;

comment on function public.telemetry_thin(timestamptz, integer) is
  'Removes readings older than p_before except the first in each p_every_seconds window per trip (the windows telemetry_sample uses) and those where the passenger count changed. Returns the number removed.';

revoke all on function public.telemetry_thin(timestamptz, integer) from public, anon, authenticated;
grant execute on function public.telemetry_thin(timestamptz, integer) to service_role;

commit;

notify pgrst, 'reload schema';
