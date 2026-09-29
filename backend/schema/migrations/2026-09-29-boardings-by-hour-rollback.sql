-- Backs out 2026-09-29-boardings-by-hour.sql.
--
-- Deploy a dashboard that reads the boarding events itself first. One that still calls
-- boardings_by_hour would find it gone, and the dashboard would fail to load.
--
-- Nothing is lost: the function only reads.

begin;

drop function if exists public.boardings_by_hour(text[]);

commit;

notify pgrst, 'reload schema';
