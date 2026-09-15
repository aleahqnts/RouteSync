-- Monthly roster: who drives which bus on which shift, who floats, and each one's rest day.
--
-- Background
--
-- The dispatch schedule is being automated. A standing roster is built once on the
-- dashboard's Roster page and carried from month to month: each bus has a crew, one
-- driver per shift the bus runs, and each route has floaters who cover the crew's rest
-- days. Only the shift rotates between months. The roster is what later expands into a
-- month of trips; this migration holds the roster alone and writes no trips.
--
-- Model
--
-- roster_months   one row per month, with a version bumped on every save so a page built
--                 on an older copy is refused rather than written over somebody's work.
-- roster_slots    one row per driver per month: a crew seat on a bus and shift, or a
--                 floater on a route. A driver sits in one place a month, and a bus shift
--                 has one crew driver.
--
-- A save replaces the month's slots whole, inside save_roster_month, so a page that loses
-- its connection halfway never leaves half a roster.
--
-- Access
--
-- Writes go through the service key only, which is how the dashboard reaches the
-- database, and the save function is executable by nothing else. The driver app may read
-- its own slot, for the line on its home screen, and whether a month is published, for its
-- calendar. Nothing else is exposed.

begin;

-- ---------------------------------------------------------------------------
-- 1. Tables
-- ---------------------------------------------------------------------------

create table if not exists public.roster_months (
  month        date primary key,
  status       text not null default 'Draft',
  version      integer not null default 0,
  saved_at     timestamptz,
  saved_by     integer references public.users (user_id) on delete set null,
  generated_at timestamptz,
  generated_by text,
  published_at timestamptz,
  published_by text,
  constraint roster_months_first_of_month check (month = date_trunc('month', month)::date),
  constraint roster_months_status check (status in ('Draft', 'Published'))
);

comment on table public.roster_months is
  'One roster per month. version is bumped by every save_roster_month call and a save '
  'built on an older version is refused.';

create table if not exists public.roster_slots (
  month        date not null references public.roster_months (month) on delete cascade,
  driver_id    integer not null references public.users (user_id) on delete restrict,
  kind         text not null,
  route_id     integer not null references public.routes (route_id) on delete restrict,
  vehicle_id   character varying(20) references public.vehicles (vehicle_id) on delete restrict,
  shift        character varying(20) not null,
  rest_weekday smallint not null,
  primary key (month, driver_id),
  constraint roster_slots_kind check (kind in ('Crew', 'Floater')),
  constraint roster_slots_shift check (shift in ('Morning', 'Afternoon', 'Evening')),
  constraint roster_slots_rest_weekday check (rest_weekday between 1 and 7),
  -- A crew seat is on a bus; a floater belongs to a route and no bus.
  constraint roster_slots_crew_has_bus check ((kind = 'Crew') = (vehicle_id is not null))
);

comment on column public.roster_slots.shift is
  'Crew: the shift driven this month. Floater: the home shift, preferred when choosing '
  'cover and not a lock.';
comment on column public.roster_slots.rest_weekday is
  'ISO weekday, 1 Monday to 7 Sunday. Fixed across months.';

-- One crew driver per bus per shift.
create unique index if not exists roster_slots_one_crew_per_bus_shift
  on public.roster_slots (month, vehicle_id, shift)
  where kind = 'Crew';

-- ---------------------------------------------------------------------------
-- 2. Saving a month
--
-- The version check and the replacement happen under one row lock, so two dispatchers
-- saving at once cannot both win: the second waits, then finds the version moved.
-- A month saved for the first time is created at version 0 inside the same transaction
-- and leaves it at 1, so a page that opened on no roster at all sends 0.
--
-- Refusals carry their own SQLSTATE so the dashboard can tell a stale page from a
-- broken one without reading prose: RS409 for a moved version.
-- ---------------------------------------------------------------------------

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

  insert into roster_slots (month, driver_id, kind, route_id, vehicle_id, shift, rest_weekday)
  select p_month,
         (s ->> 'driver_id')::integer,
         s ->> 'kind',
         (s ->> 'route_id')::integer,
         nullif(s ->> 'vehicle_id', ''),
         s ->> 'shift',
         (s ->> 'rest_weekday')::smallint
    from jsonb_array_elements(coalesce(p_slots, '[]'::jsonb)) as s;

  return v_version + 1;
end;
$$;

revoke all on function public.save_roster_month(date, integer, jsonb, integer) from public;
revoke all on function public.save_roster_month(date, integer, jsonb, integer) from anon, authenticated;
grant execute on function public.save_roster_month(date, integer, jsonb, integer) to service_role;

-- ---------------------------------------------------------------------------
-- 3. Access
--
-- New tables in this schema are granted to anon and authenticated by default. Those
-- grants are taken back, so the tables are closed even before row security is read.
-- ---------------------------------------------------------------------------

alter table public.roster_months enable row level security;
alter table public.roster_slots  enable row level security;

revoke all on table public.roster_months from anon, authenticated;
revoke all on table public.roster_slots  from anon, authenticated;

grant all on table public.roster_months to service_role;
grant all on table public.roster_slots  to service_role;

grant select on table public.roster_months to app_driver;
grant select on table public.roster_slots  to app_driver;

drop policy if exists p_roster_months_driver_select on public.roster_months;
create policy p_roster_months_driver_select on public.roster_months
  for select to app_driver
  using (status = 'Published');

drop policy if exists p_roster_slots_driver_select on public.roster_slots;
create policy p_roster_slots_driver_select on public.roster_slots
  for select to app_driver
  using (driver_id = public.jwt_uid()
         and exists (select 1 from public.roster_months m
                      where m.month = roster_slots.month and m.status = 'Published'));

-- ---------------------------------------------------------------------------
-- 4. The roster permission
--
-- Building and publishing the roster is its own permission, granted here to the
-- administrator roles. Any other role can be given it from the Users page.
-- ---------------------------------------------------------------------------

do $grant$
declare
  v_n integer;
begin
  update public.roles
     set web_permissions = web_permissions || '{"roster": true}'::jsonb
   where role_name ilike 'admin%'
     and coalesce((web_permissions ->> 'roster')::boolean, false) = false;
  get diagnostics v_n = row_count;
  raise notice 'roster permission granted to % role(s)', v_n;
end
$grant$;

commit;
