-- Backs out 2026-11-06-nav-badge-inputs-since.sql. Without the function the dashboard
-- falls back to nav_badge_inputs and reads every badge row on every asking, as before.

begin;

drop function if exists public.nav_badge_inputs_since(date, date, date, text[], text, text);

commit;

notify pgrst, 'reload schema';
