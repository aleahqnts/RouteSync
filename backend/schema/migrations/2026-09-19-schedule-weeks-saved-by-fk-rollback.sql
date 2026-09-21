-- Backs out 2026-09-19-schedule-weeks-saved-by-fk.sql.
--
-- Safe to run at any time. Dropping the constraint changes no row and no reader; it only
-- stops the database refusing a week that names an operator who is not there.

begin;

alter table public.schedule_weeks
  drop constraint if exists fk_schedule_weeks_saved_by;

commit;
