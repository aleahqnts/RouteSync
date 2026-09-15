-- Backs out 2026-09-15-trip-break-slot.sql.
--
-- Deploy the dashboard without the break slot first. A dashboard that still maps the
-- column writes it on every new trip, and once the column is gone every such insert is
-- refused.
--
-- Destructive: every slot assigned so far, including any a dispatcher moved by hand, is
-- dropped with the column. Running the migration again gives future trips fresh slots by
-- bus order, not the ones dispatch chose.

begin;

alter table public.trips
  drop constraint if exists trips_break_start_in_shift;

alter table public.trips
  drop column if exists break_start;

commit;
