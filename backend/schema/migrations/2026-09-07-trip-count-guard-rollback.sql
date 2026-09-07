-- Backs out 2026-09-07-trip-count-guard.sql.
--
-- Two stages, because they undo different amounts. Stage one is the one to reach for
-- during an incident: it restores the previous behaviour immediately and keeps every
-- figure the guard has recorded. Stage two removes the columns, and with them the
-- record of which counts were machine counts and which were driver corrections.

-- ---------------------------------------------------------------------------
-- Stage 1. Restore the previous behaviour.
--
-- total_boarded goes back to being whatever the last writer says. The columns stay, so
-- nothing is lost and the guard can be recreated by running the migration again.
--
-- Note that total_boarded already includes any driver correction, so trips keep the
-- figures they are showing now. Only the protection goes.
-- ---------------------------------------------------------------------------

drop trigger if exists trg_trips_count_guard on public.trips;

-- ---------------------------------------------------------------------------
-- Stage 2. Full teardown.
--
-- Destructive. boarded_counted and boarded_adjustment are dropped, so a trip whose
-- reported total differs from its machine count keeps only the reported total and the
-- reason is gone. The audit_log rows written by the guard survive, since that table
-- does not permit deletion.
--
-- Run only when the model itself is being abandoned, not to recover from a bad write.
-- ---------------------------------------------------------------------------

-- revoke update ("boarded_adjustment") on public.trips from app_driver;
-- drop function if exists public.trips_count_guard();
-- alter table public.trips
--   drop column if exists boarded_counted,
--   drop column if exists boarded_adjustment;
