-- Backs out 2026-09-15-schedule-weeks-roster-only.sql.
--
-- Deploy a driver app and dashboard that no longer read roster_only first. Without the
-- column, a week a publish marked reads as built again, so days in it that nobody planned
-- show as rest days.

begin;

drop trigger if exists trg_schedule_weeks_roster_only on public.schedule_weeks;
drop function if exists public.schedule_weeks_roster_only();
alter table public.schedule_weeks drop column if exists roster_only;

commit;
