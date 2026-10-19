-- Checks 2026-10-19-telemetry-thin.sql. Run it after the migration, in the Supabase SQL
-- editor. A failure raises; a pass prints one notice. It thins a made-up trip inside a
-- transaction that is rolled back, so no real reading is touched.

do $test$
declare
  f constant text := 'public.telemetry_thin(timestamptz, integer)';
  sampled_before json;
  sampled_after json;
  removed int;
  again int;
  left_rows int;
begin
  if has_function_privilege('anon', f, 'execute') then
    raise exception 'anon can call telemetry_thin';
  end if;
  if has_function_privilege('authenticated', f, 'execute') then
    raise exception 'authenticated can call telemetry_thin';
  end if;
  if not has_function_privilege('service_role', f, 'execute') then
    raise exception 'service_role cannot call telemetry_thin';
  end if;

  -- Readings must belong to a trip, so the made-up ones borrow a real trip's id.
  if not exists (select 1 from trips) then
    raise notice 'pass: grants are right; no trip to borrow for the thinning check';
    return;
  end if;

  begin
    -- Two minutes of readings every five seconds, ten days ago. The count goes 0, 0, 3 at
    -- the 50 s reading, then 3 to the end, so one reading inside a window is kept for it.
    insert into telemetry_data (telemetry_id, trip_id, latitude, longitude, total_passengers, speed, heading, "timestamp", accuracy)
    select -1000 - g,
           (select trip_id from trips order by trip_id limit 1),
           14.55, 121.05,
           case when g * 5 < 50 then 0 else 3 end,
           5, 90,
           date_trunc('minute', now()) - interval '10 days' + make_interval(secs => g * 5),
           2
    from generate_series(0, 23) g;

    select telemetry_sample(array[(select trip_id from telemetry_data where telemetry_id = -1000)], 30)
      into sampled_before;
    -- Only the made-up readings are compared: other readings of a borrowed trip are
    -- thinned too, but they are not what the check counts.
    sampled_before := (select json_agg(x) from json_array_elements(sampled_before -> 'telemetry') x
                       where (x ->> 'telemetry_id')::bigint < -999);

    -- The cutoff sits just after the made-up readings. Real readings older than it are
    -- thinned too, but the whole block is rolled back below.
    removed := telemetry_thin(date_trunc('minute', now()) - interval '10 days' + interval '2 minutes', 30);
    again := telemetry_thin(date_trunc('minute', now()) - interval '10 days' + interval '2 minutes', 30);

    select telemetry_sample(array[(select trip_id from telemetry_data where telemetry_id = -1000)], 30)
      into sampled_after;
    sampled_after := (select json_agg(x) from json_array_elements(sampled_after -> 'telemetry') x
                      where (x ->> 'telemetry_id')::bigint < -999);

    select count(*) into left_rows from telemetry_data where telemetry_id between -1023 and -1000;

    -- 24 readings in 4 windows of 30 s: the first of each window stays, plus the 50 s
    -- reading where the count changed, which is second in its window.
    if left_rows <> 5 then
      raise exception 'expected 5 readings left, found %', left_rows;
    end if;
    if not exists (select 1 from telemetry_data where telemetry_id = -1010) then
      raise exception 'the reading where the count changed was removed';
    end if;
    if again <> 0 then
      raise exception 'a second run removed % more readings', again;
    end if;
    if sampled_before::text is distinct from sampled_after::text then
      raise exception 'telemetry_sample changed: before % after %', sampled_before, sampled_after;
    end if;

    raise exception 'rollback-pass: removed %, kept %', removed, left_rows;
  exception
    when others then
      if sqlerrm like 'rollback-pass:%' then
        raise notice 'pass: grants are right; %', substr(sqlerrm, 16);
      else
        raise;
      end if;
  end;
end
$test$;
