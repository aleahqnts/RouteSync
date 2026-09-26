-- Backs out 2026-09-26-real-bgc-routes.sql.
--
-- The two route rows go back to what routes_before_osm saved: the hand-entered line and
-- stops, South Line's name, origin and destination. The backup table is then dropped.
--
-- What is kept
--
-- telemetry_data.accuracy stays. A driver app built to send it would have every GPS reading
-- refused once the column is gone, and a refused reading is held on the phone and retried,
-- so the bus would drop off the fleet map until the phone is reinstalled. Drop it by hand,
-- with the two statements at the end, only once no phone sends it.
--
-- Run the whole file in the Supabase SQL editor with nothing highlighted.

begin;

do $restore$
begin
  if to_regclass('public.routes_before_osm') is null then
    raise exception 'There is no routes_before_osm table, so there is nothing to restore.';
  end if;

  if not exists (select 1 from public.routes_before_osm) then
    raise exception 'routes_before_osm is empty, so there is nothing to restore.';
  end if;
end
$restore$;

update public.routes r
   set route_name     = b.route_name,
       origin         = b.origin,
       destination    = b.destination,
       waypoints_json = b.waypoints_json,
       stops_json     = b.stops_json,
       updated_at     = b.updated_at
  from public.routes_before_osm b
 where b.route_id = r.route_id;

drop table public.routes_before_osm;

commit;

-- Only once no driver app sends accuracy:
--
-- alter table public.telemetry_data drop constraint if exists ck_telemetry_accuracy;
-- alter table public.telemetry_data drop column if exists accuracy;
