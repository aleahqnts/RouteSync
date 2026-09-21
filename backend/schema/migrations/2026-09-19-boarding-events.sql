-- One row per crossing the counter phone detects, alongside the count it already sends.
--
-- Background
--
-- trips.total_boarded is the reported figure and stays so. It is a running total the
-- counter phone raises every few seconds, guarded by trips_count_guard, and every reader
-- in the system takes it as the answer. Nothing here changes that.
--
-- What the system cannot currently show is the individual crossings behind that number.
-- A single integer cannot say when a passenger boarded, cannot be de-duplicated, cannot
-- distinguish an outward crossing that was correctly excluded from a camera that saw
-- nothing at all, and cannot separate a figure the machine produced from one a driver
-- typed by hand. Those are the questions accuracy testing has to answer.
--
-- Model
--
-- Events are evidence, not the source of truth. The reported figure is still
-- total_boarded; the sum of events is what it is compared against. The two are expected
-- to differ, and the difference is the useful part:
--
--   total_boarded - count(events where direction = 'in')  =  what a human added by hand
--
-- A trip where that difference is zero was counted by the machine alone, and only such a
-- trip measures the machine. A trip where it is positive had the driver's manual counter
-- contribute, which is legitimate during a camera outage and disqualifying for an
-- accuracy observation. Nothing else in the schema can tell those apart.
--
-- Outward crossings are recorded too. They are never counted, but a camera that detected
-- thirty exits and excluded all thirty and a camera that detected nothing both report
-- zero boardings, and only the recorded exits separate them.
--
-- Identity
--
-- event_id is generated on the device, not here. A phone that loses contact holds its
-- events and sends them again later, and it cannot know whether the first attempt landed.
-- With the identifier fixed at the crossing, a resend collides with the primary key and
-- is refused, so delivering twice stores once. A server-generated key would make the
-- second copy a new row.
--
-- The trip is stamped at the crossing as well, from what the device believed at that
-- moment. A phone out of contact across a shift change keeps counting into the trip it
-- last saw, and stamping at the crossing means its events say the same thing its count
-- said. Resolving the trip on arrival instead would quietly correct the attribution and
-- leave the count disagreeing with its own evidence.
--
-- Device attribution
--
-- counter_device_id carries no foreign key. There is no table of devices: device_status
-- holds a heartbeat that is rewritten every few seconds, and vehicles.counter_device_id
-- holds a binding that changes when a phone is swapped. An event must record which phone
-- counted it at the time, which neither of those can answer later.

begin;

create table if not exists public.boarding_events (
  event_id          text        not null,
  trip_id           character varying(20) not null,
  counter_device_id text        not null,
  direction         text        not null,
  device_timestamp  timestamptz not null,
  synced_at         timestamptz,
  received_at       timestamptz not null default now(),

  constraint pk_boarding_events primary key (event_id),
  constraint fk_boarding_events_trip
    foreign key (trip_id) references public.trips (trip_id),
  constraint ck_boarding_events_direction
    check (direction in ('in', 'out'))
);

comment on table public.boarding_events is 'One detected doorway crossing. Evidence for the count in trips.total_boarded, never the count itself.';

comment on column public.boarding_events.event_id is 'Generated on the device at the crossing, so a resend collides here and stores once.';

comment on column public.boarding_events.counter_device_id is 'The phone that detected it, as it was then. Deliberately not a foreign key: no table of devices exists, and the vehicle binding it would point at changes when a phone is swapped.';

comment on column public.boarding_events.direction is 'in for a boarding, out for a crossing the counter detected and excluded. Only in is ever counted.';

comment on column public.boarding_events.device_timestamp is 'When the crossing happened, by the device clock.';

comment on column public.boarding_events.synced_at is 'When the event left the device queue. Null when it was sent without being held. With device_timestamp, both on the same clock, it gives the time spent waiting for a signal.';

comment on column public.boarding_events.received_at is 'When the database accepted it. With device_timestamp it gives end to end latency.';

-- Reconciling a trip against its events, and reading latency across a run, are the two
-- ways this table is queried.
create index if not exists ix_boarding_events_trip
  on public.boarding_events (trip_id, direction);

create index if not exists ix_boarding_events_device_timestamp
  on public.boarding_events (device_timestamp);

-- ---------------------------------------------------------------------------
-- Reading
-- ---------------------------------------------------------------------------

-- Stored as real instants, because received_at comes from now() and subtracting a naked
-- local time from it would answer in hours. Read in Philippine time, because every other
-- date in this system is, and a test result nobody can read is not a result.
create or replace view public.boarding_events_ph as
select
  event_id,
  trip_id,
  counter_device_id,
  direction,
  device_timestamp at time zone 'Asia/Manila' as device_time_ph,
  synced_at        at time zone 'Asia/Manila' as synced_ph,
  received_at      at time zone 'Asia/Manila' as received_ph,
  extract(epoch from (received_at - device_timestamp)) as latency_seconds,
  extract(epoch from (received_at - coalesce(synced_at, device_timestamp))) as transit_seconds
from public.boarding_events;

comment on view public.boarding_events_ph is 'boarding_events in Philippine wall-clock time, with latency worked out on the stored instants so a device clock offset cannot distort it.';

-- ---------------------------------------------------------------------------
-- Access
-- ---------------------------------------------------------------------------

alter table public.boarding_events enable row level security;

-- The counter phone writes; nothing else does. No update and no delete for any device
-- role: an event is a record of something that happened, not a piece of state. The
-- service key is unaffected and remains the way an admin corrects anything.
grant insert on public.boarding_events to app_camera;

-- A device may only write events under its own identity. The trip is left to the foreign
-- key, which refuses an event naming a trip that does not exist.
--
-- The policy deliberately does not require the device to still hold the bus. A phone
-- draining a queue after a dead zone can be writing hours later, by which time the trip
-- has closed and the binding may have moved or been released, and refusing those is
-- refusing exactly the evidence the offline cases exist to collect.
drop policy if exists p_boarding_insert_own_device on public.boarding_events;
create policy p_boarding_insert_own_device on public.boarding_events
  for insert to app_camera
  with check (
    counter_device_id = nullif(current_setting('request.jwt.claims', true), '')::json->>'device_id'
  );

commit;
