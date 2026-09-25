-- Backs out 2026-09-25-closed-trip-guard.sql.
--
-- What returns to being possible
--
-- A driver phone still holding a trip that was closed without it, at handover or by the stale
-- trip closer, can write to that trip again. Its running count raises the closed trip's total,
-- its End replaces the end time the close stamped, and the End's release sets the bus to Ready
-- to Deploy even while the next driver is out on it.
--
-- A driver app built with the closed trip notice still stops counting once it sees the trip
-- closed, and its writes are filtered to active trips, so those builds stay safe. Older builds
-- lose the protection.
--
-- What is kept
--
-- The trip_end_after_close rows already in audit_log stay, since that table does not permit
-- deletion. Trips the guard protected keep the values they were closed with.

begin;

drop trigger if exists trg_trips_closed_guard on public.trips;
drop function if exists public.trips_closed_guard();

drop trigger if exists trg_vehicles_release_guard on public.vehicles;
drop function if exists public.vehicles_release_guard();

commit;
