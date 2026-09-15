-- Backs out 2026-09-15-roster-tables.sql.
--
-- Deploy a dashboard without the Roster page first. A dashboard that still has it calls
-- save_roster_month, and once the function is gone every save is refused.
--
-- Destructive: every roster built so far is dropped with its tables. The roster
-- permission is taken back from every role that holds it, not only the ones the
-- migration granted it to.

begin;

drop function if exists public.save_roster_month(date, integer, jsonb, integer);

drop table if exists public.roster_slots;
drop table if exists public.roster_months;

update public.roles
   set web_permissions = web_permissions - 'roster'
 where web_permissions ? 'roster';

commit;
