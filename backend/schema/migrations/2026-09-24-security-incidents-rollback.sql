-- Backs out 2026-09-24-security-incidents.sql.
--
-- Safe at any time. Nothing but the incidents page and the detector reads either table,
-- and the audit trail they describe is untouched, so every entry an incident gathered is
-- still there to be read directly. What is lost is the grouping and the review history.

begin;

drop table if exists public.security_incidents;
drop table if exists public.security_detector_state;

commit;
