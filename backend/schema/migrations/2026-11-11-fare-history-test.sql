-- Expect: history_rows >= 1, today_fare = current_fare, guard_reads_history = true,
-- trigger_present = true, anon_can_read = false.
select
  (select count(*) from fare_history) as history_rows,
  (select standard_fare from fare_history
     where effective_from <= ((now() at time zone 'Asia/Manila') - interval '6 hours')::date
     order by effective_from desc limit 1) as today_fare,
  (select standard_fare from fare_config where id = 1) as current_fare,
  position('fare_history' in pg_get_functiondef('public.trips_count_guard'::regproc)) > 0 as guard_reads_history,
  exists (select 1 from pg_trigger where tgname = 'trg_fare_config_history') as trigger_present,
  has_table_privilege('anon', 'public.fare_history', 'select') as anon_can_read;
