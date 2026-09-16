-- Exercises empty crew places and stored suggestions in roster_slots.
--
-- Everything happens inside one transaction that ends in a rollback, so no roster survives
-- the run. It needs two users, a route and a vehicle to exist and uses the lowest ids it
-- finds, only ever inside the rollback. The month is January 2099, clear of any real one.
--
-- Run it after 2026-09-15-roster-tables.sql and this migration, as the whole file in the
-- Supabase SQL editor. A failure raises and aborts; "Success. No rows returned" is a pass.

begin;

do $test$
declare
  v_month   date := date '2099-01-01';
  v_d1      integer;
  v_d2      integer;
  v_route   integer;
  v_bus     text;
  v_version integer;
  v_case    text;
begin
  select user_id into v_d1 from public.users order by user_id limit 1;
  select user_id into v_d2 from public.users where user_id > v_d1 order by user_id limit 1;
  select route_id into v_route from public.routes order by route_id limit 1;
  select vehicle_id into v_bus from public.vehicles order by vehicle_id limit 1;

  if v_d2 is null or v_route is null or v_bus is null then
    raise exception 'needs two users, a route and a vehicle to run';
  end if;

  v_case := 'a crew place with nobody on it saves, beside a filled one, with its suggestion';
  v_version := public.save_roster_month(v_month, 0, jsonb_build_array(
    jsonb_build_object('driver_id', v_d1, 'kind', 'Crew', 'route_id', v_route, 'vehicle_id', v_bus,
                       'shift', 'Morning', 'rest_weekday', 2, 'suggested', 'Drove it on 12 days'),
    jsonb_build_object('driver_id', null, 'kind', 'Crew', 'route_id', v_route, 'vehicle_id', v_bus,
                       'shift', 'Afternoon', 'rest_weekday', null, 'suggested', 'Nobody free'),
    jsonb_build_object('driver_id', v_d2, 'kind', 'Floater', 'route_id', v_route, 'vehicle_id', null,
                       'shift', 'Morning', 'rest_weekday', 5)), null);
  if v_version <> 1
     or (select count(*) from public.roster_slots where month = v_month) <> 3
     or (select suggested from public.roster_slots where month = v_month and driver_id = v_d1) <> 'Drove it on 12 days'
     or (select suggested from public.roster_slots where month = v_month and driver_id = v_d2) is not null
     or not exists (select 1 from public.roster_slots where month = v_month and driver_id is null and shift = 'Afternoon') then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'two empty places in one month do not collide';
  v_version := public.save_roster_month(v_month, v_version, jsonb_build_array(
    jsonb_build_object('driver_id', null, 'kind', 'Crew', 'route_id', v_route, 'vehicle_id', v_bus, 'shift', 'Morning'),
    jsonb_build_object('driver_id', null, 'kind', 'Crew', 'route_id', v_route, 'vehicle_id', v_bus, 'shift', 'Evening')), null);
  if (select count(*) from public.roster_slots where month = v_month and driver_id is null) <> 2 then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'a driver in two places is still refused';
  begin
    perform public.save_roster_month(v_month, v_version, jsonb_build_array(
      jsonb_build_object('driver_id', v_d1, 'kind', 'Crew', 'route_id', v_route, 'vehicle_id', v_bus, 'shift', 'Morning', 'rest_weekday', 1),
      jsonb_build_object('driver_id', v_d1, 'kind', 'Floater', 'route_id', v_route, 'shift', 'Morning', 'rest_weekday', 3)), null);
    raise exception 'fail: %', v_case;
  exception when unique_violation then
    raise notice 'pass: %', v_case;
  end;

  v_case := 'a floater with no driver is refused';
  begin
    perform public.save_roster_month(v_month, v_version, jsonb_build_array(
      jsonb_build_object('driver_id', null, 'kind', 'Floater', 'route_id', v_route, 'shift', 'Morning')), null);
    raise exception 'fail: %', v_case;
  exception when check_violation then
    raise notice 'pass: %', v_case;
  end;

  v_case := 'a placed driver with no rest day is refused';
  begin
    perform public.save_roster_month(v_month, v_version, jsonb_build_array(
      jsonb_build_object('driver_id', v_d1, 'kind', 'Crew', 'route_id', v_route, 'vehicle_id', v_bus, 'shift', 'Morning')), null);
    raise exception 'fail: %', v_case;
  exception when check_violation then
    raise notice 'pass: %', v_case;
  end;

  v_case := 'two crew places on one bus shift are still refused';
  begin
    perform public.save_roster_month(v_month, v_version, jsonb_build_array(
      jsonb_build_object('driver_id', null, 'kind', 'Crew', 'route_id', v_route, 'vehicle_id', v_bus, 'shift', 'Morning'),
      jsonb_build_object('driver_id', v_d1, 'kind', 'Crew', 'route_id', v_route, 'vehicle_id', v_bus, 'shift', 'Morning', 'rest_weekday', 1)), null);
    raise exception 'fail: %', v_case;
  exception when unique_violation then
    raise notice 'pass: %', v_case;
  end;

  if (select count(*) from public.roster_slots where month = v_month) <> 2 then
    raise exception 'fail: a refused save changed the month';
  end if;

  raise notice 'all roster auto-fill cases passed';
end
$test$;

rollback;
