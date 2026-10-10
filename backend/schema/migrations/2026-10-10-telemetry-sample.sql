-- Adds telemetry_sample: a few trips' readings thinned to one per stretch of time.
--
-- The dashboard learns how fast buses cover each stretch between stops from past trips.
-- That needs only the moment a bus passed each stop, and the phones report every five
-- seconds, so reading every row of sixty trips would move about half a gigabyte a day
-- out of the database at full service. One reading per thirty seconds still places each
-- stop crossing within a few seconds, at a sixth of the data.
--
-- Returns {"telemetry": [...]} rather than a set of rows, so the answer is not cut at
-- the API's row limit. Only the dashboard's service role may call it.

begin;

create or replace function public.telemetry_sample(p_trip_ids text[], p_every_seconds integer default 30)
returns json
language sql
stable
set search_path to 'public'
as $$
  select json_build_object('telemetry', coalesce((
    select json_agg(x order by x.trip_id, x."timestamp")
    from (
      select distinct on (t.trip_id, floor(extract(epoch from t."timestamp") / greatest(p_every_seconds, 1)))
             t.telemetry_id, t.trip_id, t.latitude, t.longitude, t.speed, t.heading, t.accuracy, t."timestamp"
      from telemetry_data t
      where t.trip_id = any (p_trip_ids)
      order by t.trip_id, floor(extract(epoch from t."timestamp") / greatest(p_every_seconds, 1)), t."timestamp"
    ) x), '[]'::json))
$$;

comment on function public.telemetry_sample(text[], integer) is
  'Readings of the given trips, the first in each p_every_seconds window per trip, for learning stop-to-stop speeds without reading every row.';

revoke all on function public.telemetry_sample(text[], integer) from public, anon, authenticated;
grant execute on function public.telemetry_sample(text[], integer) to service_role;

commit;

notify pgrst, 'reload schema';
