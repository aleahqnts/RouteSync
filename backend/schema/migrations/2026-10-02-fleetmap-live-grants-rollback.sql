-- Backs out 2026-10-02-fleetmap-live-grants.sql, giving anon and authenticated back the
-- grants the project's defaults gave them.

begin;

grant all on function public.fleetmap_live(date, integer, timestamp with time zone, jsonb)
  to anon, authenticated;

commit;

notify pgrst, 'reload schema';
