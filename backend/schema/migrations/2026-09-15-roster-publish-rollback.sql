-- Backs out 2026-09-15-roster-publish.sql.
--
-- Two stages. Stage one is for an incident: it stops publishing and stops marking hand
-- edits and writing skips, and keeps every trip, skip and gap. Stage two removes the
-- columns and tables, and with them which trips a roster wrote and which were edited by
-- hand.

-- ---------------------------------------------------------------------------
-- Stage 1. Stop publishing and the triggers. Nothing is lost.
-- ---------------------------------------------------------------------------

begin;

drop function if exists public.publish_roster_month(date, integer, jsonb, integer);
drop trigger if exists trg_trips_roster_edit on public.trips;
drop trigger if exists trg_trips_roster_skip on public.trips;
drop function if exists public.trips_roster_guard();

commit;

-- ---------------------------------------------------------------------------
-- Stage 2. Full teardown.
--
-- Destructive. Trips written by a roster stay, as ordinary trips. Deploy a dashboard that
-- no longer reads these columns first.
-- ---------------------------------------------------------------------------

-- begin;
-- drop table if exists public.roster_gaps;
-- drop table if exists public.roster_skips;
-- drop index if exists public.trips_roster_month_idx;
-- alter table public.trips
--   drop column if exists hand_edited,
--   drop column if exists roster_month;
-- commit;
