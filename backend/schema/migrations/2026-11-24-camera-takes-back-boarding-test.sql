-- Expect: camera_may_take_back = true, clamp_checks_stored = true.
select
  position('OLD.total_boarded - 1' in pg_get_functiondef('public.trips_count_guard'::regproc)) > 0 as camera_may_take_back,
  position('v_claim < NEW.total_boarded' in pg_get_functiondef('public.trips_count_guard'::regproc)) > 0 as clamp_checks_stored;
