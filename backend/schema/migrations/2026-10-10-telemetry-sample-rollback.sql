-- Backs out 2026-10-10-telemetry-sample.sql. Without the function the dashboard learns no
-- stop-to-stop speeds and estimates from each bus's own speed or a default instead.

begin;

drop function if exists public.telemetry_sample(text[], integer);

commit;

notify pgrst, 'reload schema';
