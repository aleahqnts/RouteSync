-- Show the driver how much charge the counter phone has left.
--
-- The counter phone is bolted inside the bus, usually out of the driver's reach, and a
-- phone that runs flat mid-shift stops counting. The driver is the one person who can do
-- something about that before it happens, by plugging it in, and the trip screen is the
-- one place they look.
--
-- The phone already reports its charge to device_health_log, but that table is a history
-- kept for reliability figures: written by the phone alone and readable by nobody, and
-- rightly so. What the driver needs is the level now, which is what device_status is
-- for. The phone already holds select, insert and update on its own row there, and a
-- driver can already read the row of the camera on their active trip, so this needs no
-- new grant and no new policy.
--
-- The phone sends the level in a request of its own rather than with its heartbeat. A
-- heartbeat carrying a column this migration had not yet added would be refused whole,
-- and a refused heartbeat reads to the driver app as a dead camera. Kept apart, a phone
-- updated before this runs loses only the battery figure.

begin;

alter table public.device_status
  add column if not exists battery_level    integer,
  add column if not exists battery_charging boolean,
  add column if not exists battery_read_at  timestamp with time zone;

alter table public.device_status
  drop constraint if exists ck_device_status_battery_level;
alter table public.device_status
  add constraint ck_device_status_battery_level
  check (battery_level is null or battery_level between 0 and 100);

comment on column public.device_status.battery_level is
  'Charge percentage at the last reading. Null until the phone has reported one.';

comment on column public.device_status.battery_charging is
  'Whether the phone was plugged in at the last reading.';

comment on column public.device_status.battery_read_at is
  'When the phone last reported its charge. It reports on a change rather than on a '
  'schedule, so an old time with a live heartbeat means the level has held steady.';

commit;
