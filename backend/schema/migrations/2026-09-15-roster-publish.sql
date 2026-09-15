-- Roster publish: expanding a month's roster into trips, and remembering what people did to them.
--
-- Background
--
-- A published roster writes the month's trips in one go: every bus, every shift it runs,
-- every day, with a floater on each crew rest day. The dashboard works out the plan from
-- the roster and the schedule as it stands; this migration gives it the one function that
-- applies such a plan, and the rules that keep a person's changes safe from the next
-- publish.
--
-- Model
--
-- trips.roster_month   which roster wrote the trip. Null on a trip made by hand.
-- trips.hand_edited    set by trigger when anyone but a publish changes who, what, where or
--                      when on a roster trip. A re-publish never touches such a trip.
-- roster_skips         a slot the roster runs that must stay empty: a roster trip somebody
--                      deleted, or a bus marked as not running that day. Never refilled.
-- roster_gaps          a slot the last publish could not fill, with why. Replaced on every
--                      publish. Reads as open while no trip and no skip exists for it.
--
-- Both rules live in triggers rather than in the dashboard, so the planner, the dispatch
-- board and anything written later all mark hand edits and leave skips without having to
-- remember to. They stand down only inside publish_roster_month, which sets a flag for its
-- own transaction that no client can set.
--
-- Timestamps written to schedule_weeks follow that table's convention: Philippine wall
-- clock tagged as UTC.

begin;

-- ---------------------------------------------------------------------------
-- 1. Trips
-- ---------------------------------------------------------------------------

alter table public.trips
  add column if not exists roster_month date references public.roster_months (month) on delete set null,
  add column if not exists hand_edited  boolean not null default false;

comment on column public.trips.roster_month is
  'The roster month that wrote this trip, or null for a trip made by hand.';
comment on column public.trips.hand_edited is
  'True once the driver, bus, route, shift, date or break of a roster trip was changed '
  'outside a publish. Set by trg_trips_roster_edit; a re-publish leaves such a trip alone.';

create index if not exists trips_roster_month_idx
  on public.trips (roster_month)
  where roster_month is not null;

-- ---------------------------------------------------------------------------
-- 2. Skips and gaps
-- ---------------------------------------------------------------------------

create table if not exists public.roster_skips (
  date       date not null,
  vehicle_id character varying(20) not null references public.vehicles (vehicle_id) on delete cascade,
  shift      character varying(20) not null,
  month      date not null references public.roster_months (month) on delete cascade,
  reason     text not null,
  created_by text,
  created_at timestamptz not null default now(),
  primary key (date, vehicle_id, shift),
  constraint roster_skips_shift check (shift in ('Morning', 'Afternoon', 'Evening')),
  constraint roster_skips_reason check (reason in ('Deleted', 'Not running'))
);

create table if not exists public.roster_gaps (
  date       date not null,
  vehicle_id character varying(20) not null references public.vehicles (vehicle_id) on delete cascade,
  shift      character varying(20) not null,
  month      date not null references public.roster_months (month) on delete cascade,
  route_id   integer references public.routes (route_id) on delete cascade,
  reason     text not null,
  primary key (date, vehicle_id, shift),
  constraint roster_gaps_shift check (shift in ('Morning', 'Afternoon', 'Evening'))
);

create index if not exists roster_skips_month_idx on public.roster_skips (month);
create index if not exists roster_gaps_month_idx  on public.roster_gaps (month);

-- ---------------------------------------------------------------------------
-- 3. Hand edits and skips
--
-- Security definer so the skip is written whoever deletes the trip, since a delete must
-- never fail for want of a grant on a table the deleter never meant to touch.
-- ---------------------------------------------------------------------------

create or replace function public.trips_roster_guard() returns trigger
language plpgsql
security definer
set search_path = public
as $$
begin
  if coalesce(current_setting('routesync.publishing', true), '') = 'on' then
    if tg_op = 'DELETE' then return old; end if;
    return new;
  end if;

  if tg_op = 'UPDATE' then
    new.hand_edited := true;
    return new;
  end if;

  if tg_op = 'DELETE' then
    insert into roster_skips (date, vehicle_id, shift, month, reason)
    values (old.date, old.vehicle_id, old.shift_type, old.roster_month, 'Deleted')
    on conflict (date, vehicle_id, shift) do nothing;
    return old;
  end if;

  return null;
end;
$$;

revoke all on function public.trips_roster_guard() from public, anon, authenticated;

drop trigger if exists trg_trips_roster_edit on public.trips;
create trigger trg_trips_roster_edit
  before update on public.trips
  for each row
  when (
        old.roster_month is not null
    and not old.hand_edited
    and (   old.driver_id   is distinct from new.driver_id
         or old.vehicle_id  is distinct from new.vehicle_id
         or old.route_id    is distinct from new.route_id
         or old.shift_type  is distinct from new.shift_type
         or old.date        is distinct from new.date
         or old.break_start is distinct from new.break_start)
  )
  execute function public.trips_roster_guard();

drop trigger if exists trg_trips_roster_skip on public.trips;
create trigger trg_trips_roster_skip
  after delete on public.trips
  for each row
  when (old.roster_month is not null)
  execute function public.trips_roster_guard();

-- ---------------------------------------------------------------------------
-- 4. Publishing a month
--
-- p_plan is worked out by the dashboard:
--
--   { "inserts": [ { date, shift, route_id, vehicle_id, driver_id,
--                    shift_start_time, shift_end_time, break_start } ],
--     "updates": [ { trip_id, driver_id, break_start } ],
--     "deletes": [ trip_id ],
--     "gaps":    [ { date, vehicle_id, shift, route_id, reason } ] }
--
-- Nothing in it is believed. Every update and delete is checked again here against the
-- rule for a trip a publish may rewrite: written by this month's roster, never edited by
-- hand, not started or finished, and dated after today's operational day. A trip that has
-- stopped qualifying since the plan was made is kept and reported. An insert into a slot
-- that has since gained a trip or a skip is dropped. If what is left would put a driver or
-- a bus in two places on one shift, the whole publish is refused and nothing is written.
--
-- Refusals: RS404 no roster, RS409 stale version, RS422 a double booking.
-- ---------------------------------------------------------------------------

create or replace function public.publish_roster_month(
  p_month        date,
  p_base_version integer,
  p_plan         jsonb,
  p_published_by integer)
returns jsonb
language plpgsql
set search_path = public
as $$
declare
  v_version  integer;
  v_op_day   date := ((now() at time zone 'Asia/Manila') - interval '6 hours')::date;
  v_wall     timestamptz := (now() at time zone 'Asia/Manila') at time zone 'UTC';
  v_inserted integer := 0;
  v_updated  integer := 0;
  v_deleted  integer := 0;
  v_kept     jsonb := '[]'::jsonb;
  v_dropped  integer := 0;
  v_days     date[] := '{}';
  v_clash    record;
  r          jsonb;
  t          public.trips%rowtype;
begin
  select version into v_version from roster_months where month = p_month for update;

  if not found then
    raise exception 'there is no roster for %', to_char(p_month, 'YYYY-MM') using errcode = 'RS404';
  end if;

  if v_version <> p_base_version then
    raise exception 'roster for % was saved by someone else (version % not %)',
      to_char(p_month, 'YYYY-MM'), v_version, p_base_version
      using errcode = 'RS409';
  end if;

  perform set_config('routesync.publishing', 'on', true);

  -- Updates: a new driver, or a break slot, for a trip the roster wrote.
  for r in select * from jsonb_array_elements(coalesce(p_plan -> 'updates', '[]'::jsonb)) loop
    select * into t from trips where trip_id = r ->> 'trip_id' for update;
    if not found then continue; end if;

    if t.roster_month is distinct from p_month or t.hand_edited
       or t.trip_status in ('Active', 'Completed') or t.date <= v_op_day then
      v_kept := v_kept || to_jsonb(t.trip_id);
      continue;
    end if;

    update trips
       set driver_id   = (r ->> 'driver_id')::integer,
           break_start = nullif(r ->> 'break_start', '')::time
     where trip_id = t.trip_id;

    v_updated := v_updated + 1;
    v_days := v_days || t.date;
  end loop;

  -- Deletes: a slot the roster no longer runs, or can no longer fill.
  for r in select * from jsonb_array_elements(coalesce(p_plan -> 'deletes', '[]'::jsonb)) loop
    select * into t from trips where trip_id = r #>> '{}' for update;
    if not found then continue; end if;

    if t.roster_month is distinct from p_month or t.hand_edited
       or t.trip_status in ('Active', 'Completed') or t.date <= v_op_day then
      v_kept := v_kept || to_jsonb(t.trip_id);
      continue;
    end if;

    delete from trips where trip_id = t.trip_id;
    v_deleted := v_deleted + 1;
    v_days := v_days || t.date;
  end loop;

  -- Inserts: only into a slot still empty and not skipped.
  for r in select * from jsonb_array_elements(coalesce(p_plan -> 'inserts', '[]'::jsonb)) loop
    if (r ->> 'date')::date <= v_op_day
       or exists (select 1 from roster_skips s
                   where s.date = (r ->> 'date')::date
                     and s.vehicle_id = r ->> 'vehicle_id'
                     and s.shift = r ->> 'shift')
       or exists (select 1 from trips x
                   where x.date = (r ->> 'date')::date
                     and x.vehicle_id = r ->> 'vehicle_id'
                     and x.shift_type = r ->> 'shift') then
      v_dropped := v_dropped + 1;
      continue;
    end if;

    insert into trips (date, shift_type, shift_start_time, shift_end_time, route_id,
                       vehicle_id, driver_id, break_start, roster_month)
    values ((r ->> 'date')::date,
            r ->> 'shift',
            (r ->> 'shift_start_time')::time,
            (r ->> 'shift_end_time')::time,
            (r ->> 'route_id')::integer,
            r ->> 'vehicle_id',
            (r ->> 'driver_id')::integer,
            nullif(r ->> 'break_start', '')::time,
            p_month);

    v_inserted := v_inserted + 1;
    v_days := v_days || (r ->> 'date')::date;
  end loop;

  -- Whatever changed, nobody may be in two places on one shift.
  select date, shift_type, 'driver ' || driver_id as who into v_clash
    from trips
   where date = any (v_days)
   group by date, shift_type, driver_id
  having count(*) > 1
   limit 1;

  if not found then
    select date, shift_type, 'bus ' || vehicle_id as who into v_clash
      from trips
     where date = any (v_days)
     group by date, shift_type, vehicle_id
    having count(*) > 1
     limit 1;
  end if;

  if found then
    raise exception '% would be booked twice on the % shift of %: the schedule changed while publishing',
      v_clash.who, v_clash.shift_type, v_clash.date
      using errcode = 'RS422';
  end if;

  delete from roster_gaps where month = p_month;

  insert into roster_gaps (date, vehicle_id, shift, month, route_id, reason)
  select (g ->> 'date')::date, g ->> 'vehicle_id', g ->> 'shift', p_month,
         nullif(g ->> 'route_id', '')::integer, g ->> 'reason'
    from jsonb_array_elements(coalesce(p_plan -> 'gaps', '[]'::jsonb)) as g
  on conflict (date, vehicle_id, shift) do update set reason = excluded.reason, month = excluded.month;

  -- Every week touched reads as saved, so a planner left open on it refuses its stale save.
  insert into schedule_weeks (week_start, saved_at, saved_by)
  select distinct d - (extract(isodow from d)::integer - 1), v_wall, p_published_by
    from unnest(v_days) as d
  on conflict (week_start) do update set saved_at = excluded.saved_at, saved_by = excluded.saved_by;

  update roster_months
     set status       = 'Published',
         version      = v_version + 1,
         published_at = now(),
         published_by = p_published_by::text
   where month = p_month;

  -- Handed back before returning. The flag is local to the transaction, and a caller that
  -- goes on to change a trip in the same one must meet the triggers as usual.
  perform set_config('routesync.publishing', '', true);

  return jsonb_build_object(
    'version',  v_version + 1,
    'inserted', v_inserted,
    'updated',  v_updated,
    'deleted',  v_deleted,
    'dropped',  v_dropped,
    'kept',     v_kept);
end;
$$;

revoke all on function public.publish_roster_month(date, integer, jsonb, integer) from public;
revoke all on function public.publish_roster_month(date, integer, jsonb, integer) from anon, authenticated;
grant execute on function public.publish_roster_month(date, integer, jsonb, integer) to service_role;

-- ---------------------------------------------------------------------------
-- 5. Access
-- ---------------------------------------------------------------------------

alter table public.roster_skips enable row level security;
alter table public.roster_gaps  enable row level security;

revoke all on table public.roster_skips from anon, authenticated;
revoke all on table public.roster_gaps  from anon, authenticated;

grant all on table public.roster_skips to service_role;
grant all on table public.roster_gaps  to service_role;

commit;
