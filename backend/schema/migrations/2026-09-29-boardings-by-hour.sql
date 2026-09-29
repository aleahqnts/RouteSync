-- Boardings counted by the hour in the database, for the dashboard's hourly chart.
--
-- Background
--
-- The chart shows each hour's boardings for today beside the same weekday's average. It
-- used to read every boarding event behind those hours and count them on the web server.
-- The dashboard refreshes every five seconds, so each refresh downloaded every boarding of
-- the day so far, one row per passenger, only to reduce them to a handful of numbers. With
-- a full fleet that is thousands of rows per refresh, several megabytes a minute for each
-- open dashboard, and the project's monthly transfer allowance gone within hours.
--
-- Model
--
-- boardings_by_hour(trip ids)   one row per trip per hour in which that trip boarded
--                               anybody, with how many.
--
-- The count stays per trip rather than per hour alone, for two reasons. The dashboard can
-- be narrowed to one route, which it does by the trips it asks about. And a trip's count
-- can run ahead of its events when a driver adds passengers by hand during a camera
-- outage; the dashboard places that difference in the hour the trip ended, and working it
-- out needs each trip's own event count.
--
-- A day at full service is a few dozen trips, each boarding in eight or nine hours, so the
-- answer is a few hundred small rows however many passengers rode.
--
-- Trips are asked for by identifier, which the index on (trip_id, direction) answers
-- directly, and every boarding of those trips is returned whenever it happened. The caller
-- places each hour in its own service day and leaves out the ones that fall outside it.
--
-- Hours are whole UTC hours, returned as instants. The Philippines keeps a whole-hour
-- offset, so each is also a whole Philippine hour.
--
-- Access
--
-- For the web dashboard only, which holds the service key. It runs as its caller, so row
-- level security still decides what any other role could see, and no other role may call
-- it at all.

begin;

create or replace function public.boardings_by_hour(p_trip_ids text[])
returns table (trip_id text, hour_start timestamptz, boarded integer)
language sql
stable
set search_path = public
as $$
  select e.trip_id::text,
         date_bin(interval '1 hour', e.device_timestamp, timestamptz '2000-01-01 00:00:00+00'),
         count(*)::integer
  from public.boarding_events e
  where e.trip_id = any (p_trip_ids)
    and e.direction = 'in'
  group by 1, 2
  order by 1, 2
$$;

comment on function public.boardings_by_hour(text[]) is
  'Boardings per trip per hour, for the given trips. Only crossings inward are counted. Hours are whole hours, as instants.';

revoke all on function public.boardings_by_hour(text[]) from public;
revoke all on function public.boardings_by_hour(text[]) from anon, authenticated;
grant execute on function public.boardings_by_hour(text[]) to service_role;

commit;

notify pgrst, 'reload schema';
