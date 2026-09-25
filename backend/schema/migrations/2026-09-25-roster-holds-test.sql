-- Exercises roster_holds and the five-argument save_roster_month.
--
-- Everything happens inside one transaction that ends in a rollback, so no roster, no hold
-- and no version survives the run. It needs two drivers, a bus and a route to exist, and uses
-- the lowest ids it finds, only ever inside the rollback.
--
-- Run it after the migration, in the Supabase SQL editor. A failure raises and aborts; a pass
-- prints one notice per case and a final line.

begin;

do $test$
declare
  v_month   date := date '2099-02-01';
  v_d1      integer;
  v_d2      integer;
  v_route   integer;
  v_bus     text;
  v_version integer;
  v_n       integer;
  v_case    text;
begin
  select user_id into v_d1 from public.users order by user_id limit 1;
  select user_id into v_d2 from public.users where user_id > v_d1 order by user_id limit 1;
  select route_id into v_route from public.routes order by route_id limit 1;
  select vehicle_id into v_bus from public.vehicles order by vehicle_id limit 1;

  if v_d2 is null or v_route is null or v_bus is null then
    raise exception 'needs two users, a route and a vehicle to run';
  end if;

  v_case := 'a hold is saved with the roster and the version moves once';
  v_version := public.save_roster_month(v_month, 0, jsonb_build_array(
    jsonb_build_object('driver_id', v_d1, 'kind', 'Crew', 'route_id', v_route,
                       'vehicle_id', v_bus, 'shift', 'Morning', 'rest_weekday', 2)),
    null, jsonb_build_array(v_d2));
  select count(*) into v_n from public.roster_holds where month = v_month and driver_id = v_d2;
  if v_version <> 1 or v_n <> 1 then
    raise exception 'fail: % (version %, % holds)', v_case, v_version, v_n;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'a driver placed on the roster is not held back from it';
  v_version := public.save_roster_month(v_month, 1, jsonb_build_array(
    jsonb_build_object('driver_id', v_d2, 'kind', 'Crew', 'route_id', v_route,
                       'vehicle_id', v_bus, 'shift', 'Morning', 'rest_weekday', 3)),
    null, jsonb_build_array(v_d2));
  select count(*) into v_n from public.roster_holds where month = v_month;
  if v_version <> 2 or v_n <> 0 then
    raise exception 'fail: % (version %, % holds)', v_case, v_version, v_n;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'a save without holds clears them, as an older dashboard would';
  perform public.save_roster_month(v_month, 2, '[]'::jsonb, null, jsonb_build_array(v_d1));
  perform public.save_roster_month(v_month, 3, '[]'::jsonb, null);
  select count(*) into v_n from public.roster_holds where month = v_month;
  if v_n <> 0 then
    raise exception 'fail: % (% holds left)', v_case, v_n;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'a stale save changes no holds';
  perform public.save_roster_month(v_month, 4, '[]'::jsonb, null, jsonb_build_array(v_d1));
  begin
    perform public.save_roster_month(v_month, 4, '[]'::jsonb, null, jsonb_build_array(v_d2));
    raise exception 'fail: % (stale save was accepted)', v_case;
  exception when sqlstate 'RS409' then
    null;
  end;
  select count(*) into v_n from public.roster_holds where month = v_month and driver_id = v_d1;
  if v_n <> 1 then
    raise exception 'fail: % (the earlier hold did not survive)', v_case;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'a month deleted takes its holds with it';
  delete from public.roster_slots where month = v_month;
  delete from public.roster_months where month = v_month;
  select count(*) into v_n from public.roster_holds where month = v_month;
  if v_n <> 0 then
    raise exception 'fail: % (% holds left)', v_case, v_n;
  end if;
  raise notice 'pass: %', v_case;

  raise notice 'roster holds: every case passed';
end
$test$;

rollback;
