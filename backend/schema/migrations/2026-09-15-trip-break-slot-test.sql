-- Exercises trips_break_start_in_shift against a temporary copy of the trips table.
--
-- Everything happens inside one transaction that ends in a rollback, and the rows it
-- writes go to a temporary table, so nothing survives the run. It reads public.trips
-- only to report how the backfill left today's trips.
--
-- Run it after the migration, in the Supabase SQL editor. A failure raises and aborts;
-- a pass prints one notice per case and a final line.

begin;

create temp table trips_break_test (like public.trips including defaults including constraints)
  on commit drop;

do $test$
declare
  v_case text;
  v_bad  integer;
begin
  -- Each shift's three slots are accepted, including the Evening ones after midnight.
  v_case := 'the three slots of each shift are accepted';
  insert into trips_break_test (trip_id, date, shift_type, shift_start_time, shift_end_time,
                                route_id, vehicle_id, driver_id, break_start)
  values
    ('T-M1', current_date, 'Morning',   '06:00', '14:00', 1, 'V1', 1, '09:00'),
    ('T-M2', current_date, 'Morning',   '06:00', '14:00', 1, 'V2', 2, '10:00'),
    ('T-M3', current_date, 'Morning',   '06:00', '14:00', 1, 'V3', 3, '11:00'),
    ('T-A1', current_date, 'Afternoon', '14:00', '22:00', 1, 'V1', 4, '17:00'),
    ('T-A3', current_date, 'Afternoon', '14:00', '22:00', 1, 'V3', 5, '19:00'),
    ('T-E1', current_date, 'Evening',   '22:00', '06:00', 1, 'V1', 6, '01:00'),
    ('T-E3', current_date, 'Evening',   '22:00', '06:00', 1, 'V3', 7, '03:00'),
    ('T-NB', current_date, 'Morning',   '06:00', '14:00', 1, 'V4', 8, null);
  raise notice 'pass: %', v_case;

  -- Anything else is refused: the shift's first hour, its last hour, and a slot that
  -- belongs to a different shift.
  foreach v_case in array array['06:00', '13:00', '17:00', '09:30'] loop
    begin
      insert into trips_break_test (trip_id, date, shift_type, shift_start_time, shift_end_time,
                                    route_id, vehicle_id, driver_id, break_start)
      values ('T-BAD', current_date, 'Morning', '06:00', '14:00', 1, 'V9', 9, v_case::time);
      raise exception 'fail: a Morning break at % was accepted', v_case;
    exception when check_violation then
      raise notice 'pass: a Morning break at % is refused', v_case;
    end;
  end loop;

  -- Moving a trip's start without moving its break is refused, so the two cannot drift.
  v_case := 'a start time moved away from its break is refused';
  begin
    update trips_break_test set shift_start_time = '07:00' where trip_id = 'T-M1';
    raise exception 'fail: %', v_case;
  exception when check_violation then
    raise notice 'pass: %', v_case;
  end;

  -- The backfill, as it left the real table: no trip from today on outside its slots.
  select count(*) into v_bad
    from public.trips
   where break_start is not null
     and break_start not in (shift_start_time + interval '3 hours',
                             shift_start_time + interval '4 hours',
                             shift_start_time + interval '5 hours');
  if v_bad > 0 then
    raise exception 'fail: % trips hold a break outside their slots', v_bad;
  end if;
  raise notice 'pass: every stored break is one of its trip''s slots';

  raise notice 'all break slot cases passed';
end
$test$;

rollback;
