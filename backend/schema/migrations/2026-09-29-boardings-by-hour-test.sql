-- Exercises boardings_by_hour.
--
-- Reads only, inside a transaction that ends in a rollback. The counting cases use whatever
-- trips already have boarding events and are skipped when none do; the access cases always
-- run.
--
-- Run it after the migration, in the Supabase SQL editor. A failure raises and aborts; a pass
-- prints one notice per case and a final line.

begin;

do $test$
declare
  v_trips  text[];
  v_n      bigint;
  v_m      bigint;
  v_case   text;
begin
  select array_agg(distinct trip_id::text) into v_trips from public.boarding_events;

  if v_trips is null then
    raise notice 'skipped: no boarding events to count';
  else
    v_case := 'the hours of each trip add up to its inward crossings';
    select count(*) into v_n
    from (
      select h.trip_id
      from public.boardings_by_hour(v_trips) h
      group by h.trip_id
      having sum(h.boarded) <> (select count(*) from public.boarding_events e
                                where e.trip_id = h.trip_id and e.direction = 'in')
    ) mismatched;
    if v_n <> 0 then
      raise exception 'fail: % (% trips disagree)', v_case, v_n;
    end if;
    raise notice 'pass: %', v_case;

    v_case := 'every inward crossing is counted, and no outward one';
    select coalesce(sum(boarded), 0) into v_n from public.boardings_by_hour(v_trips);
    select count(*) into v_m from public.boarding_events where direction = 'in';
    if v_n <> v_m then
      raise exception 'fail: % (% counted, % inward)', v_case, v_n, v_m;
    end if;
    raise notice 'pass: %', v_case;

    v_case := 'each hour starts on the hour';
    select count(*) into v_n from public.boardings_by_hour(v_trips)
    where hour_start <> date_trunc('hour', hour_start at time zone 'UTC') at time zone 'UTC';
    if v_n <> 0 then
      raise exception 'fail: % (% hours off the hour)', v_case, v_n;
    end if;
    raise notice 'pass: %', v_case;

    v_case := 'a trip appears at most once per hour';
    select count(*) into v_n from (
      select 1 from public.boardings_by_hour(v_trips)
      group by trip_id, hour_start having count(*) > 1) dup;
    if v_n <> 0 then
      raise exception 'fail: % (% repeats)', v_case, v_n;
    end if;
    raise notice 'pass: %', v_case;
  end if;

  v_case := 'a trip with no events, or no trips at all, returns nothing';
  select count(*) into v_n from public.boardings_by_hour(array['NO-SUCH-TRIP']);
  select count(*) into v_m from public.boardings_by_hour(array[]::text[]);
  if v_n <> 0 or v_m <> 0 then
    raise exception 'fail: % (% and % rows)', v_case, v_n, v_m;
  end if;
  raise notice 'pass: %', v_case;

  v_case := 'only the service key may call it';
  if has_function_privilege('anon', 'public.boardings_by_hour(text[])', 'execute')
     or has_function_privilege('authenticated', 'public.boardings_by_hour(text[])', 'execute')
     or not has_function_privilege('service_role', 'public.boardings_by_hour(text[])', 'execute') then
    raise exception 'fail: %', v_case;
  end if;
  raise notice 'pass: %', v_case;

  raise notice 'all boardings_by_hour cases passed';
end
$test$;

rollback;
