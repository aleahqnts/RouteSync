-- Backs out 2026-09-15-roster-autofill.sql.
--
-- Deploy a dashboard without Auto-fill first. One that still has it saves empty places and
-- suggestions, and without the columns those saves are refused.
--
-- Destructive: crew places with nobody on them are deleted, since the old key cannot hold
-- them, and every stored suggestion is dropped. The buses those places were on stop
-- running that shift at the next publish.

begin;

delete from public.roster_slots where driver_id is null;

alter table public.roster_slots drop constraint if exists roster_slots_driver_rests;
alter table public.roster_slots drop constraint if exists roster_slots_floater_has_driver;
drop index if exists public.roster_slots_one_place_per_driver;

alter table public.roster_slots drop constraint if exists roster_slots_pkey;
alter table public.roster_slots
  alter column driver_id    set not null,
  alter column rest_weekday set not null;
alter table public.roster_slots add constraint roster_slots_pkey primary key (month, driver_id);

alter table public.roster_slots
  drop column if exists slot_id,
  drop column if exists suggested;

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

commit;
