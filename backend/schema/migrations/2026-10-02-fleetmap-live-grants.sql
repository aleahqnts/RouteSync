-- Takes fleetmap_live back to the dashboard alone.
--
-- Recreating the function in 2026-10-02-fleetmap-live-since.sql picked up the project's
-- default privileges, which grant every new function in public to anon and authenticated.
-- The function runs with the caller's rights, so row security still held, but only the
-- dashboard's service role should call it, as before.

begin;

revoke all on function public.fleetmap_live(date, integer, timestamp with time zone, jsonb)
  from anon, authenticated;

commit;

notify pgrst, 'reload schema';
