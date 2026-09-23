-- Removing inspection photographs that have served their purpose.
--
-- Not a migration. Nothing here runs on its own and nothing here is scheduled. These are
-- the two queries that decide what may go, written down so the decision is not remade from
-- memory each time somebody wonders whether the bucket is filling up.
--
-- Why it is not scheduled
--
-- Deleting an object needs the storage API, not SQL, so no amount of pg_cron can finish
-- the job on its own: a scheduled job could only ever mark rows and would leave the bytes
-- behind, which is the opposite of the problem. Wiring the real thing means a job that
-- holds the service key and makes HTTP calls, and at seven buses the arithmetic does not
-- ask for it. Two photographs a day is around twelve megabytes a month against a free
-- tier gigabyte.
--
-- Run these when somebody wants to know, or when the storage figure starts to look
-- interesting. If the fleet grows to where that is tedious, the work is a small scheduled
-- function rather than a rethink.
--
-- Both sweeps are two-phase and the order matters. Read the rows, delete the objects
-- through the storage API, then write back. Writing back first would leave objects that
-- nothing in the database can name, which is the one state neither sweep can recover from.

-- ---------------------------------------------------------------------------
-- Retention: photographs whose work order closed over thirty days ago
-- ---------------------------------------------------------------------------

-- Phase one. What may go, and the paths to hand the storage API.
--
-- Note that Resolve closes every open order on a vehicle at once, so a bus may present a
-- run of photographs that all became eligible on the same day.
select p.photo_id,
       p.object_key,
       l.vehicle_id,
       l.resolved_at
from public.inspection_photos p
join public.maintenance_logs l on l.log_id = p.log_id
where p.object_key is not null
  and l.resolved_at is not null
  and l.resolved_at < now() - interval '30 days'
order by l.resolved_at;

-- Phase two, after the objects are gone. The row stays and records that it once had a
-- photograph, because a fault nobody photographed and a fault whose photograph aged out
-- are different facts about how the fleet was run, and a deleted row cannot tell them
-- apart. The check constraint on the table ties these two columns together, so they are
-- written in one statement.
--
-- Substitute the identifiers actually deleted rather than repeating the select: between
-- the two phases a storage call may have failed, and a row marked swept while its object
-- survives is an orphan that the orphan sweep below will never recognise.
--
--   update public.inspection_photos
--      set object_key = null,
--          swept_at   = now()
--    where photo_id = any (array[...]);

-- ---------------------------------------------------------------------------
-- Orphans: objects no row ever claimed
-- ---------------------------------------------------------------------------

-- A photograph taken for an item the driver then marked Pass, or removed, is deleted by
-- the app at the moment they change their mind. An inspection abandoned outright is what
-- reaches here: the object was uploaded, the submit never happened, and nothing in the
-- database has ever referred to it.
--
-- This cannot be answered in SQL alone, because the bucket's contents are not a table.
-- List the bucket first, with the service key:
--
--   POST {SUPABASE_URL}/storage/v1/object/list/inspection-photos
--   { "prefix": "", "limit": 1000 }
--
-- Then ask which of those names are unknown here. Anything under two days old is left
-- alone: an inspection in progress has uploaded its photographs and not yet submitted
-- them, and sweeping those would delete evidence out from under a driver still walking
-- around the bus.
--
--   select k.name
--   from unnest(array[ ... names from the listing ... ]) as k(name)
--   where not exists (
--     select 1 from public.inspection_photos p where p.object_key = k.name
--   );
--
-- Delete the survivors of that through the storage API. There is no phase two: these
-- objects have no rows, which is what made them orphans.

-- ---------------------------------------------------------------------------
-- How much is actually stored
-- ---------------------------------------------------------------------------

-- What the sweeps are weighed against. If live photographs stay in the hundreds, neither
-- sweep is urgent.
select count(*) filter (where object_key is not null) as live,
       count(*) filter (where object_key is null)     as swept,
       min(taken_at)                                  as oldest,
       max(taken_at)                                  as newest
from public.inspection_photos;
