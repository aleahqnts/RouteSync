-- Checks 2026-10-13-trip-active-poll.sql. Run it after the migration, in the Supabase SQL
-- editor. A failure raises; a pass prints one notice. It reads only, and uses the most
-- recent trip. The editor runs as the owner, so row policies are not exercised here: a
-- driver sees only their own trip, which the phone shows.

do $test$
declare
  f constant text := 'public.trip_active_poll(character varying)';
  t trips%rowtype;
  a json;
begin
  if has_function_privilege('anon', f, 'execute') then
    raise exception 'anon can call trip_active_poll';
  end if;
  if has_function_privilege('authenticated', f, 'execute') then
    raise exception 'authenticated can call trip_active_poll';
  end if;
  if not has_function_privilege('app_driver', f, 'execute') then
    raise exception 'app_driver cannot call trip_active_poll';
  end if;

  if json_typeof(trip_active_poll('no-such-trip') -> 'trip') <> 'null' then
    raise exception 'an unknown trip came back';
  end if;

  select * into t from trips order by date desc, trip_id desc limit 1;
  if t.trip_id is null then
    raise notice 'pass: grants are right; no trips to read yet';
    return;
  end if;

  a := trip_active_poll(t.trip_id);
  if a -> 'trip' ->> 'trip_id' is distinct from t.trip_id::text
     or (a -> 'trip' ->> 'total_boarded')::int is distinct from t.total_boarded
     or a -> 'trip' ->> 'vehicle_id' is distinct from t.vehicle_id::text then
    raise exception 'trip % read back as %', t.trip_id, a -> 'trip';
  end if;
  if a ->> 'counter_device_id' is distinct from
     (select counter_device_id from vehicles where vehicle_id = t.vehicle_id) then
    raise exception 'counter phone for % read back as %', t.vehicle_id, a ->> 'counter_device_id';
  end if;

  raise notice 'pass: % read back in % bytes', t.trip_id, length(a::text);
end
$test$;
