-- Reverses 2026-09-25-device-status-battery.sql. A phone still sending the battery
-- figure is refused on that request alone; its heartbeat is unaffected.

begin;

alter table public.device_status
  drop constraint if exists ck_device_status_battery_level,
  drop column if exists battery_level,
  drop column if exists battery_charging,
  drop column if exists battery_read_at;

commit;
