-- The roster_slots columns and keys from 2026-09-15-roster-autofill.sql, applied on their own.
--
-- Background
--
-- That migration was never run against the live database. Two things have depended on it
-- since without anyone noticing:
--
--   - The rail's roster badge reads roster_slots.suggested. With no such column the read
--     fails, the badge is caught failing, and it shows nothing: no unfilled slots, no
--     roster waiting for review.
--   - save_roster_month as written by 2026-09-25-roster-holds.sql inserts into
--     roster_slots.suggested. With no such column every roster save is refused.
--
-- It also left a crew place that nobody is on unsavable, since driver_id could not be null.
--
-- What this runs, and what it does not
--
-- Everything from the autofill migration that changes the table, and nothing else. Its
-- save_roster_month is left out on purpose: roster-holds replaces it with a five-argument
-- version, and creating the old four-argument one beside that would leave two functions a
-- save could match, so every save would fail to choose between them.
--
-- Safe on the live rows: they all have a driver and a rest day, and no driver holds two
-- places in a month, since the old key already forbade it. Every step checks before it acts,
-- so running it twice changes nothing.

begin;

alter table public.roster_slots
  add column if not exists slot_id   bigint generated always as identity,
  add column if not exists suggested text;

do $key$
begin
  if exists (select 1 from pg_constraint
              where conrelid = 'public.roster_slots'::regclass and conname = 'roster_slots_pkey'
                and pg_get_constraintdef(oid) like 'PRIMARY KEY (month, driver_id)') then
    alter table public.roster_slots drop constraint roster_slots_pkey;
  end if;

  if not exists (select 1 from pg_constraint
                  where conrelid = 'public.roster_slots'::regclass and contype = 'p') then
    alter table public.roster_slots add constraint roster_slots_pkey primary key (slot_id);
  end if;
end
$key$;

alter table public.roster_slots
  alter column driver_id    drop not null,
  alter column rest_weekday drop not null;

-- A driver still sits in one place a month. Empty places are exempt: nulls never collide.
create unique index if not exists roster_slots_one_place_per_driver
  on public.roster_slots (month, driver_id);

alter table public.roster_slots drop constraint if exists roster_slots_floater_has_driver;
alter table public.roster_slots add constraint roster_slots_floater_has_driver
  check (kind = 'Crew' or driver_id is not null);

alter table public.roster_slots drop constraint if exists roster_slots_driver_rests;
alter table public.roster_slots add constraint roster_slots_driver_rests
  check ((driver_id is null) = (rest_weekday is null));

comment on column public.roster_slots.driver_id is
  'Null for a crew place the bus runs with nobody on it yet. A floater always has a driver.';
comment on column public.roster_slots.suggested is
  'Why auto-fill filled or emptied this place. Null for a place a person set.';

notify pgrst, 'reload schema';

commit;
