-- Backs out 2026-09-19-device-health-log.sql.
--
-- Deploy a counter phone that does not report its own health first. A phone still
-- sending restarts and charge readings gets 404 from PostgREST on every attempt, and
-- while that costs nothing on the bus, it fills the device log with failures that look
-- like a fault on the phone.
--
-- Destructive: every restart and every charge reading recorded so far is dropped with
-- the table. Nothing else reads them, so no figure anywhere changes and no screen goes
-- blank. What is lost is the reliability record itself. Crash frequency and battery
-- consumption were derived from these rows and from device_status.last_seen, and
-- last_seen is rewritten in place rather than kept, so nothing that remains can rebuild
-- them. Any shift already observed would have to be run again.

begin;

drop view if exists public.device_health_log_ph;

-- The policy and the grant belong to the table and go with it. Naming them separately
-- would fail here rather than tidy up, since both statements require the table to exist.
drop table if exists public.device_health_log;

commit;
