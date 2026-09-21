-- Backs out 2026-09-19-telemetry-received-at.sql.
--
-- Safe to run at any time. No client sends the column, nothing reads it to decide
-- anything, and the reading time every consumer uses is the untouched timestamp column.
-- What is lost is the ability to separate a slow arrival from a slow reading, for rows
-- written while the column existed.

begin;

alter table public.telemetry_data
  drop column if exists received_at;

commit;
