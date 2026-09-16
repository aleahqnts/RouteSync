-- Roster auto-fill: places with nobody on them yet, and why a place was filled for a person.
--
-- Background
--
-- The Roster page gains Auto-fill, which clears a roster of drivers no longer active and
-- buses no longer running, then fills the empty places by rule for a person to review. The
-- monthly cycle runs the same fill on the draft it makes on the 20th, so one deactivated
-- driver no longer stops the 25th publish.
--
-- Two things the tables could not hold. A crew place a bus runs with nobody free to put on
-- it had to be dropped, which silently takes the bus off that shift, or kept with the
-- driver who left, which stops the publish. And a draft made by the server could not say
-- which of its places it chose itself.
--
-- Model
--
-- roster_slots.driver_id   now nullable, for a crew place still waiting for a driver. A
--                          publish treats it like a crew driver away every day: a floater
--                          covers it or the slot is stored as a gap. A floater row always
--                          has a driver, and a driver still sits in one place a month.
-- roster_slots.rest_weekday
--                          nullable with the driver: an empty place has no rest day, and
--                          a placed driver always has one.
-- roster_slots.suggested   why auto-fill filled or emptied the place, in a sentence for the
--                          page to show. Null for a place a person set.
-- roster_slots.slot_id     the key, since a month's empty places share a null driver.
--
-- save_roster_month writes the new column. Nothing else changes for the driver app: a
-- driver reads only their own place, which always has them in it.

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

create or replace function public.save_roster_month(
  p_month        date,
  p_base_version integer,
  p_slots        jsonb,
  p_saved_by     integer)
returns integer
language plpgsql
set search_path = public
as $$
declare
  v_version integer;
begin
  if p_month is null or p_month <> date_trunc('month', p_month)::date then
    raise exception 'roster month must be the first day of a month'
      using errcode = '22023';
  end if;

  insert into roster_months (month) values (p_month) on conflict (month) do nothing;

  select version into v_version from roster_months where month = p_month for update;

  if v_version <> p_base_version then
    raise exception 'roster for % was saved by someone else (version % not %)',
      to_char(p_month, 'YYYY-MM'), v_version, p_base_version
      using errcode = 'RS409';
  end if;

  update roster_months
     set version  = v_version + 1,
         saved_at = now(),
         saved_by = p_saved_by
   where month = p_month;

  delete from roster_slots where month = p_month;

  insert into roster_slots (month, driver_id, kind, route_id, vehicle_id, shift, rest_weekday, suggested)
  select p_month,
         (s ->> 'driver_id')::integer,
         s ->> 'kind',
         (s ->> 'route_id')::integer,
         nullif(s ->> 'vehicle_id', ''),
         s ->> 'shift',
         (s ->> 'rest_weekday')::smallint,
         nullif(s ->> 'suggested', '')
    from jsonb_array_elements(coalesce(p_slots, '[]'::jsonb)) as s;

  return v_version + 1;
end;
$$;

revoke all on function public.save_roster_month(date, integer, jsonb, integer) from public;
revoke all on function public.save_roster_month(date, integer, jsonb, integer) from anon, authenticated;
grant execute on function public.save_roster_month(date, integer, jsonb, integer) to service_role;

commit;
