-- Backs out 2026-10-13-trip-active-poll.sql. A driver app that calls the function then
-- fails its five-second check, and after three failures shows the manual count buttons.
-- Install a build from before the change alongside this.

begin;

drop function if exists public.trip_active_poll(character varying);

commit;

notify pgrst, 'reload schema';
