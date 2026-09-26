-- Checks 2026-09-26-real-bgc-routes.sql after it has run.
--
-- Read-only apart from one insert that is expected to be refused. Everything ends in a
-- rollback. A failure raises and names what is wrong; a pass prints one notice per check.

begin;

do $test$
declare
  v_north  integer;
  v_second integer;
  v_name   text;
  v_n      integer;
  v_names  text;
  v_trip   text;
  v_gap    double precision;
begin
  select route_id into v_north from public.routes where route_name ilike '%north%';
  select route_id into v_second from public.routes where route_id <> v_north;

  -- Backup
  select count(*) into v_n from public.routes_before_osm where route_id in (v_north, v_second);
  if v_n <> 2 then raise exception 'fail: routes_before_osm holds % of the 2 route rows', v_n; end if;
  raise notice 'pass: both original route rows are backed up';

  -- Names
  select route_name into v_name from public.routes where route_id = v_second;
  if v_name not ilike '%central%' then raise exception 'fail: route % is named %', v_second, v_name; end if;
  raise notice 'pass: route % is now %', v_second, v_name;

  -- Lines
  select jsonb_array_length(waypoints_json::jsonb) into v_n from public.routes where route_id = v_north;
  if v_n <> 407 then raise exception 'fail: North Express has % line points, expected 407', v_n; end if;
  select jsonb_array_length(waypoints_json::jsonb) into v_n from public.routes where route_id = v_second;
  if v_n <> 296 then raise exception 'fail: Central has % line points, expected 296', v_n; end if;
  raise notice 'pass: both lines hold the expected number of points';

  -- The longest step between neighbouring points. A line joined in the wrong order jumps
  -- hundreds of metres somewhere; the built lines never step more than about 220 m.
  select max(2 * 6371000 * asin(sqrt(
           power(sin(radians(lat - prev_lat) / 2), 2)
         + cos(radians(lat)) * cos(radians(prev_lat)) * power(sin(radians(lng - prev_lng) / 2), 2))))
    into v_gap
    from (select (p->>'lat')::float8 as lat, (p->>'lng')::float8 as lng,
                 lag((p->>'lat')::float8) over (partition by r.route_id order by i) as prev_lat,
                 lag((p->>'lng')::float8) over (partition by r.route_id order by i) as prev_lng
            from public.routes r
            cross join lateral jsonb_array_elements(r.waypoints_json::jsonb) with ordinality as e(p, i)
           where r.route_id in (v_north, v_second)) pts
   where prev_lat is not null;
  if v_gap > 250 then raise exception 'fail: a line jumps % m between neighbouring points', round(v_gap::numeric); end if;
  raise notice 'pass: no line jumps more than % m between neighbouring points', round(v_gap::numeric);

  -- Stops, in order, with one terminal each
  select string_agg(s->>'name', ' > ' order by i) into v_names
    from public.routes r, jsonb_array_elements(r.stops_json::jsonb) with ordinality as e(s, i)
   where r.route_id = v_north;
  if v_names <> 'EDSA-Ayala Terminal > NutriAsia > HSBC > Lexus Manila > Avida Towers Verte > Uptown Parade > The Globe Tower > The Fort Strip' then
    raise exception 'fail: North Express stops are %', v_names;
  end if;
  select string_agg(s->>'name', ' > ' order by i) into v_names
    from public.routes r, jsonb_array_elements(r.stops_json::jsonb) with ordinality as e(s, i)
   where r.route_id = v_second;
  if v_names <> 'Market! Market! > NutriAsia > The Fort Station > Net One > Bonifacio Stopover > Crescent Park West > The Globe Tower > One Parkade > University Parkway' then
    raise exception 'fail: Central stops are %', v_names;
  end if;
  raise notice 'pass: both stop lists are complete and in travel order';

  select count(*) into v_n
    from public.routes r, jsonb_array_elements(r.stops_json::jsonb) s
   where r.route_id in (v_north, v_second) and (s->>'terminal')::boolean;
  if v_n <> 2 then raise exception 'fail: % stops are marked terminal, expected one per route', v_n; end if;
  raise notice 'pass: one terminal per route';

  -- Accuracy
  if not exists (select 1 from information_schema.columns
                  where table_schema = 'public' and table_name = 'telemetry_data' and column_name = 'accuracy') then
    raise exception 'fail: telemetry_data has no accuracy column';
  end if;

  select trip_id into v_trip from public.trips order by trip_id desc limit 1;
  if v_trip is not null then
    begin
      insert into public.telemetry_data (trip_id, latitude, longitude, total_passengers, accuracy)
      values (v_trip, 14.55, 121.05, 0, -1);
      raise exception 'fail: a negative accuracy was accepted';
    exception when check_violation then
      null;
    end;
  end if;
  raise notice 'pass: telemetry_data.accuracy exists and refuses a negative radius';

  raise notice 'all real route checks passed';
end
$test$;

rollback;
