-- Backs out 2026-10-30-device-app-version.sql. Phones keep trying to write the version and
-- are refused, which costs nothing else; the dashboard shows no version.

begin;

alter table public.device_status drop column if exists app_version;

commit;

notify pgrst, 'reload schema';
