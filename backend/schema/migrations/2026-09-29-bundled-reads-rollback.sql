-- Backs out 2026-09-29-bundled-reads.sql.
--
-- Deploy a dashboard that makes the separate reads first. One that still calls these
-- functions would find them gone, and the sidebar, the dashboard and the fleet map would
-- stop updating.
--
-- Nothing is lost: the functions only read.

begin;

drop function if exists public.nav_badge_inputs(date, date, date, text[]);
drop function if exists public.dashboard_figures(date);
drop function if exists public.fleetmap_live(date, integer, timestamptz);

commit;

notify pgrst, 'reload schema';
