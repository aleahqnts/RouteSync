-- Backs out 2026-10-19-telemetry-thin.sql. Without the function the dashboard's sweep
-- logs that thinning is unavailable and only removes readings past the retention window.
-- Readings already thinned stay thinned.

begin;

drop function if exists public.telemetry_thin(timestamptz, integer);

commit;

notify pgrst, 'reload schema';
