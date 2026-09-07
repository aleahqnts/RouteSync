-- Report a counter phone's undelivered counts to the fleet.
--
-- A count made while the bus was out of contact is held on the phone until the database
-- confirms it has been stored. Most of the time that takes seconds. It can also never
-- happen: a phone that lost its trip claim while it was offline is refused by the
-- row-level security policy on every retry, because the policy admits a write to a
-- completed trip only from the device still named in counter_device_id.
--
-- Without this, the only place that fact exists is a screen on a phone bolted inside a
-- bus, where nobody is looking. The counter phone already heartbeats device_status every
-- twelve seconds and already holds select, insert and update on the table, so reporting
-- the backlog along the same path needs no new grant and no new policy.

begin;

alter table public.device_status
  add column if not exists unreconciled_counts    integer not null default 0,
  add column if not exists unreconciled_oldest_at timestamp with time zone;

comment on column public.device_status.unreconciled_counts is
  'Trips this device has counted but cannot confirm as stored. Zero in normal '
  'operation. A number that does not fall means deliveries are failing.';

comment on column public.device_status.unreconciled_oldest_at is
  'When the oldest undelivered count was made, so a backlog can be told apart from a '
  'count that is merely a few seconds behind.';

commit;
