-- Backs out 2026-10-10-telemetry-indexes.sql. Nothing depends on the indexes; reads go
-- back to scanning the table.

begin;

drop index if exists public.idx_telemetry_trip_time;
drop index if exists public.idx_telemetry_time;

commit;
