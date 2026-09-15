-- Week markers: telling a week the planner saved from one a roster publish only marked.
--
-- Background
--
-- schedule_weeks has two readers. The planner holds a week's saved_at as its version and
-- refuses a save built on an older one. The driver app reads a row as "this week has
-- been built", which is what tells a rest day from a day nobody has planned yet.
--
-- publish_roster_month writes a row for every week it puts trips in, for the planner's
-- sake: a planner left open on one of those weeks must refuse its save rather than delete
-- the trips it never saw. A month's first and last weeks reach into the months either
-- side, though, so publishing October also marked the week of 29 September, and 29 and
-- 30 September, which nobody had planned, read as rest days on every driver's calendar.
--
-- Model
--
-- schedule_weeks.roster_only   true while every write to the week has come from a
--                              publish. Any other write clears it, and a publish never
--                              sets it on a week the planner has already saved.
--
-- The driver app counts a day as scheduled when its week has a row that is not
-- roster_only, or a roster_only row and a month with a published roster. The planner's
-- version check is unchanged: a publish still moves saved_at.
--
-- Kept by trigger, so neither the planner nor publish_roster_month has to send the column,
-- and a dashboard deployed before or after this migration writes the same rows. It reads
-- the same transaction-local flag publish_roster_month sets for the trips triggers.

begin;

alter table public.schedule_weeks
  add column if not exists roster_only boolean not null default false;

create or replace function public.schedule_weeks_roster_only() returns trigger
language plpgsql
set search_path = public
as $$
begin
  if coalesce(current_setting('routesync.publishing', true), '') <> 'on' then
    new.roster_only := false;
  elsif tg_op = 'INSERT' then
    new.roster_only := true;
  else
    new.roster_only := old.roster_only;
  end if;
  return new;
end;
$$;

revoke all on function public.schedule_weeks_roster_only() from public, anon, authenticated;

drop trigger if exists trg_schedule_weeks_roster_only on public.schedule_weeks;
create trigger trg_schedule_weeks_roster_only
  before insert or update on public.schedule_weeks
  for each row
  execute function public.schedule_weeks_roster_only();

commit;
