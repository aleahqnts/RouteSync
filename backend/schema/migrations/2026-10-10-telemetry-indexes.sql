-- Indexes telemetry_data by trip and by time.
--
-- Every read of the table filters on one or both, and until now each scanned the whole
-- table: the fleet map's two-second poll (fleetmap_live: trip_id in the trips shown and
-- timestamp in the last half hour), the GPS check and the next-stop speeds (trip_id in a
-- list of trips), the trip reaper and the cascade from trips (trip_id), and the retention
-- sweep (timestamp older than the window).
--
-- (trip_id, timestamp) serves the trip reads and lets the poll take a trip's recent
-- readings in time order. timestamp alone serves the retention sweep, which names no trip.
--
-- Plain CREATE INDEX briefly blocks writes to the table while it builds, which on a table
-- this size is well under a second. Run outside service hours, or swap in
-- CREATE INDEX CONCURRENTLY (each statement on its own, outside the transaction).

begin;

create index if not exists idx_telemetry_trip_time
  on public.telemetry_data using btree (trip_id, "timestamp");

create index if not exists idx_telemetry_time
  on public.telemetry_data using btree ("timestamp");

commit;
