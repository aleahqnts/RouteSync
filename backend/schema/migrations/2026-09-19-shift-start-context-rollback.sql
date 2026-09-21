-- Backs out 2026-09-19-shift-start-context.sql.
--
-- Deploy a driver app that does not call it first. An app still calling it gets a 404
-- from PostgREST on every shift screen load. Both features that depend on it fail
-- closed rather than open: the handover dialog never appears, so a driver is held to
-- the plain start window, and the skip button never appears, so every driver runs the
-- full inspection. Neither lets anyone start a trip they could not start before.
--
-- Nothing else reads it, no row changes, and no policy is touched.

begin;

drop function if exists public.shift_start_context(character varying);

commit;
