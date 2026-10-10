-- Checks 2026-10-10-telemetry-sample.sql. Run it after the migration, in the Supabase SQL
-- editor. A failure raises; a pass prints one notice. It reads only, and uses whichever
-- trip has the most readings.

do $test$
declare
  f constant text := 'public.telemetry_sample(text[], integer)';
  busiest text;
  total int;
  every_row int;
  thinned int;
begin
  if has_function_privilege('anon', f, 'execute') then
    raise exception 'anon can call telemetry_sample';
  end if;
  if has_function_privilege('authenticated', f, 'execute') then
    raise exception 'authenticated can call telemetry_sample';
  end if;
  if not has_function_privilege('service_role', f, 'execute') then
    raise exception 'service_role cannot call telemetry_sample';
  end if;

  select trip_id, count(*) into busiest, total
  from telemetry_data group by trip_id order by count(*) desc limit 1;

  if busiest is null then
    raise notice 'pass: grants are right; no readings to sample yet';
    return;
  end if;

  select json_array_length(telemetry_sample(array[busiest], 1) -> 'telemetry') into every_row;
  select json_array_length(telemetry_sample(array[busiest], 30) -> 'telemetry') into thinned;

  if thinned < 1 or thinned > every_row or every_row > total then
    raise exception 'sampling % gave % rows at 1 s and % at 30 s from % readings', busiest, every_row, thinned, total;
  end if;

  raise notice 'pass: % has % readings, % kept at one per 30 s', busiest, total, thinned;
end
$test$;
