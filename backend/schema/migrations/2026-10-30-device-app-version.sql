-- Adds device_status.app_version: the Sentinel release a camera phone runs, such as 1.14.0.
--
-- The phone writes it once each time the app starts, and the dashboard's camera panel
-- shows it beside the newest published Sentinel, so a camera left behind on an older
-- release is visible before the next planned visit. The camera role already has UPDATE on
-- the whole table under its own-row policy, so no grant changes.

begin;

alter table public.device_status add column if not exists app_version text;

comment on column public.device_status.app_version is
  'Sentinel release the phone runs, written once per app start. Null until a phone on 1.14.1 or later reports.';

commit;

notify pgrst, 'reload schema';
