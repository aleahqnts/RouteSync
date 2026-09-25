-- Drivers held back from a month's roster automation.
--
-- Background
--
-- The Roster page lists every active driver with no place on the roster. Auto-fill draws on
-- that list to fill empty seats and floater rows, and Suggest rest days draws on it to add a
-- spare floater before it will move anybody's rest day. Some of those drivers are there on
-- purpose: kept in reserve for sick calls, on light duty, finishing training, or about to
-- move route. Nothing let a person say so, so the automation would place them the next time
-- it ran.
--
-- Model
--
-- roster_holds   one row per driver held back from one month's roster.
--
-- A hold switches off roster automation only. The driver is still offered for one-off cover
-- when a slot needs somebody, and a person can still place them by hand. Whether a driver may
-- drive at all is the account's status, not this.
--
-- Holds are saved with the roster, under the same lock and the same version, so a roster and
-- who is held back from it are never written apart. The month's generation carries them into
-- the next month with the rest of the roster, before auto-fill runs.
--
-- A driver placed on the roster is not held back from it. The save drops such a hold rather
-- than keeping it behind the person's back, since it would otherwise reappear the day that
-- driver is unplaced again.
--
-- No reason is recorded. The hold is a toggle.

begin;

-- ---------------------------------------------------------------------------
-- 1. Table
-- ---------------------------------------------------------------------------

create table if not exists public.roster_holds (
  month      date    not null,
  driver_id  integer not null,

  constraint roster_holds_pkey primary key (month, driver_id),
  constraint roster_holds_month_fkey
    foreign key (month) references public.roster_months (month) on delete cascade,
  constraint roster_holds_driver_fkey
    foreign key (driver_id) references public.users (user_id) on delete cascade
);

comment on table public.roster_holds is
  'Drivers held back from a month''s roster automation. Auto-fill and the suggestions pass them over; one-off cover and placing by hand do not.';

-- ---------------------------------------------------------------------------
-- 2. Saving
--
-- The four-argument function is replaced rather than overloaded. Two functions a call could
-- match by name leave the API unable to choose between them, and every save would fail.
-- p_held defaults to empty, so a dashboard built before holds existed still saves, and
-- saving from it clears the month's holds: it had no way to show them either.
-- ---------------------------------------------------------------------------

drop function if exists public.save_roster_month(date, integer, jsonb, integer);

create or replace function public.save_roster_month(
  p_month        date,
  p_base_version integer,
  p_slots        jsonb,
  p_saved_by     integer,
  p_held         jsonb default '[]'::jsonb)
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

  delete from roster_holds where month = p_month;

  -- A driver the roster places is not held back from it.
  insert into roster_holds (month, driver_id)
  select distinct p_month, h::integer
    from jsonb_array_elements_text(coalesce(p_held, '[]'::jsonb)) as h
   where not exists (
           select 1 from roster_slots s
            where s.month = p_month and s.driver_id = h::integer);

  return v_version + 1;
end;
$$;

revoke all on function public.save_roster_month(date, integer, jsonb, integer, jsonb) from public;
revoke all on function public.save_roster_month(date, integer, jsonb, integer, jsonb) from anon, authenticated;
grant execute on function public.save_roster_month(date, integer, jsonb, integer, jsonb) to service_role;

-- ---------------------------------------------------------------------------
-- 3. Access
--
-- The service key only, which is how the dashboard reaches the database. The driver app has
-- no reason to know who is held back.
-- ---------------------------------------------------------------------------

alter table public.roster_holds enable row level security;

revoke all on table public.roster_holds from anon, authenticated;
grant all on table public.roster_holds to service_role;

notify pgrst, 'reload schema';

commit;
